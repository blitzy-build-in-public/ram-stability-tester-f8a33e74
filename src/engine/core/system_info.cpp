/**
 * @file system_info.cpp
 * @brief System probe: processor groups and their active logical processors, instruction sets,
 *        physical memory, large-page granularity, the lock-pages privilege and the CPU name.
 *
 * Observes only. Nothing here enables a privilege, changes the working set or allocates beyond
 * the temporaries of one call, so rq_query_system can be called from any thread at any time,
 * and core/memory/memory_region.cpp alone decides when the lock-pages privilege is enabled.
 *
 * Compiled at the x64 SSE2 baseline, like every engine_core file except the AVX2 and AVX-512
 * kernels: the ISA probe it calls must run on every x64 CPU.
 */
#include <windows.h>
#include <intrin.h>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <new>
#include <system_error>
#include <vector>

#include "core/system_info.h"
#include "core/clock.h"
#include "core/kernels/isa_dispatch.h"

namespace rq::engine {

namespace {

/** Closes a kernel handle; the deleter of unique_handle. */
struct handle_closer {
    void operator()(HANDLE handle) const noexcept { CloseHandle(handle); }
};

/** Owns a kernel handle (the process token) so it is closed on every path. */
using unique_handle = std::unique_ptr<void, handle_closer>;

/** Active processors and active group count, read together from one RelationGroup query. */
struct processor_topology {
    std::vector<logical_processor> processors;
    std::uint32_t group_count = 0;
};

/** Bytes before the relationship union: the Relationship and Size fields. */
constexpr std::size_t k_record_header_size =
    offsetof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX, Group);

/** Processors addressable in one group: the width of KAFFINITY. */
constexpr unsigned k_processors_per_group = sizeof(KAFFINITY) * 8;

[[noreturn]] void throw_os_error(DWORD error, const char* what) {
    throw std::system_error(static_cast<int>(error), std::system_category(), what);
}

/**
 * Returns the raw GetLogicalProcessorInformationEx(RelationGroup) buffer.
 *
 * The first call only sizes the buffer. The call repeats while the OS reports a larger size,
 * which a processor hot-add between the two calls can cause.
 */
std::vector<std::byte> read_group_records() {
    std::vector<std::byte> buffer;
    DWORD length = 0;
    for (;;) {
        PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records = nullptr;
        if (!buffer.empty()) {
            records = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer.data());
        }
        if (GetLogicalProcessorInformationEx(RelationGroup, records, &length)) {
            buffer.resize(length);
            return buffer;
        }
        const DWORD error = GetLastError();
        if (error != ERROR_INSUFFICIENT_BUFFER || length <= buffer.size()) {
            throw_os_error(error, "GetLogicalProcessorInformationEx failed");
        }
        buffer.resize(length);
    }
}

/**
 * Walks the RelationGroup records into group-major processor entries.
 *
 * A group holds at most 64 logical processors, so larger systems span several groups, and
 * Windows 10 does not spread a process's threads across groups. Worker pinning therefore
 * addresses each processor by group and number explicitly, exactly as listed here.
 *
 * Records are walked by their Size field rather than assuming one record, and every size is
 * bounds-checked so a malformed buffer cannot be read past its end.
 */
processor_topology read_processor_topology() {
    const std::vector<std::byte> buffer = read_group_records();
    processor_topology topology;

    std::size_t offset = 0;
    while (offset < buffer.size()) {
        if (buffer.size() - offset < k_record_header_size) {
            throw_os_error(ERROR_INVALID_DATA,
                           "GetLogicalProcessorInformationEx returned a truncated record");
        }
        const auto* const record = reinterpret_cast<const SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*>(
            buffer.data() + offset);
        if (record->Size < k_record_header_size || record->Size > buffer.size() - offset) {
            throw_os_error(ERROR_INVALID_DATA,
                           "GetLogicalProcessorInformationEx returned a malformed record size");
        }

        if (record->Relationship == RelationGroup) {
            const GROUP_RELATIONSHIP& groups = record->Group;
            const std::size_t group_info_bytes =
                std::size_t{groups.ActiveGroupCount} * sizeof(PROCESSOR_GROUP_INFO);
            const std::size_t group_info_offset =
                k_record_header_size + offsetof(GROUP_RELATIONSHIP, GroupInfo);
            if (group_info_offset + group_info_bytes > record->Size) {
                throw_os_error(ERROR_INVALID_DATA,
                               "GetLogicalProcessorInformationEx group record is too short");
            }
            // GroupInfo is declared [ANYSIZE_ARRAY]; indexing through a pointer keeps the
            // compiler from treating it as a one-element array.
            const PROCESSOR_GROUP_INFO* const group_info = groups.GroupInfo;
            for (WORD group = 0; group < groups.ActiveGroupCount; ++group) {
                const KAFFINITY mask = group_info[group].ActiveProcessorMask;
                for (unsigned number = 0; number < k_processors_per_group; ++number) {
                    if (((mask >> number) & 1u) != 0) {
                        logical_processor processor;
                        processor.group = static_cast<std::uint16_t>(group);
                        processor.number = static_cast<std::uint8_t>(number);
                        topology.processors.push_back(processor);
                    }
                }
            }
            topology.group_count += groups.ActiveGroupCount;
        }
        offset += record->Size;
    }

    if (topology.processors.empty() || topology.group_count == 0) {
        throw_os_error(ERROR_INVALID_DATA,
                       "GetLogicalProcessorInformationEx reported no active logical processor");
    }
    return topology;
}

/**
 * Writes the CPUID brand string (leaves 0x80000002-0x80000004) to dst, trimmed.
 *
 * Intel pads the string with leading spaces and some hypervisors with NULs, so both are trimmed
 * at either end. Bytes outside printable ASCII become '?': the brand string is ASCII by
 * definition, and the C API promises valid UTF-8 even when a hypervisor reports arbitrary
 * bytes. An unsupported leaf or an empty string yields "Unknown CPU".
 */
void read_cpu_brand(char* dst, std::size_t dst_size) noexcept {
    constexpr unsigned k_brand_leaf_count = 3;
    constexpr std::size_t k_brand_bytes = k_brand_leaf_count * 4 * sizeof(int);
    char brand[k_brand_bytes + 1] = {};

    int regs[4] = {};
    __cpuid(regs, static_cast<int>(0x80000000u));
    if (static_cast<unsigned>(regs[0]) >= 0x80000004u) {
        for (unsigned leaf = 0; leaf < k_brand_leaf_count; ++leaf) {
            __cpuid(regs, static_cast<int>(0x80000002u + leaf));
            std::memcpy(brand + leaf * sizeof(regs), regs, sizeof(regs));
        }
    }

    const auto is_padding = [](char c) noexcept { return c == ' ' || c == '\0'; };
    std::size_t begin = 0;
    std::size_t end = k_brand_bytes;
    while (begin < end && is_padding(brand[begin])) {
        ++begin;
    }
    while (end > begin && is_padding(brand[end - 1])) {
        --end;
    }
    if (begin == end) {
        copy_utf8_truncated(dst, dst_size, "Unknown CPU");
        return;
    }
    for (std::size_t i = begin; i < end; ++i) {
        const auto byte = static_cast<unsigned char>(brand[i]);
        if (byte < 0x20u || byte > 0x7Eu) {
            brand[i] = '?';
        }
    }
    brand[end] = '\0';
    copy_utf8_truncated(dst, dst_size, brand + begin);
}

}  // namespace

std::vector<logical_processor> enumerate_logical_processors() {
    return read_processor_topology().processors;
}

bool lock_memory_privilege_held() {
    HANDLE raw_token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw_token)) {
        return false;
    }
    const unique_handle token(raw_token);

    LUID luid{};
    if (!LookupPrivilegeValueW(nullptr, SE_LOCK_MEMORY_NAME, &luid)) {
        return false;
    }

    DWORD length = 0;
    if (GetTokenInformation(token.get(), TokenPrivileges, nullptr, 0, &length) ||
        GetLastError() != ERROR_INSUFFICIENT_BUFFER ||
        length < offsetof(TOKEN_PRIVILEGES, Privileges)) {
        return false;
    }

    // An allocation failure means the token cannot be queried, which the contract reports as
    // false rather than as an exception.
    std::vector<std::byte> buffer;
    try {
        buffer.resize(length);
    } catch (const std::bad_alloc&) {
        return false;
    }
    if (!GetTokenInformation(token.get(), TokenPrivileges, buffer.data(), length, &length) ||
        length < offsetof(TOKEN_PRIVILEGES, Privileges) || length > buffer.size()) {
        return false;
    }

    const auto* const privileges = reinterpret_cast<const TOKEN_PRIVILEGES*>(buffer.data());
    const std::size_t count = privileges->PrivilegeCount;
    if (count > (length - offsetof(TOKEN_PRIVILEGES, Privileges)) / sizeof(LUID_AND_ATTRIBUTES)) {
        return false;
    }
    // Privileges is declared [ANYSIZE_ARRAY]; see read_processor_topology. A listed privilege
    // is held whether or not it is enabled.
    const LUID_AND_ATTRIBUTES* const entries = privileges->Privileges;
    for (std::size_t i = 0; i < count; ++i) {
        if (entries[i].Luid.LowPart == luid.LowPart && entries[i].Luid.HighPart == luid.HighPart) {
            return true;
        }
    }
    return false;
}

rq_result query_system(rq_system_info* out) {
    if (out == nullptr || out->struct_size != sizeof(rq_system_info)) {
        return RQ_E_INVALID_ARGUMENT;
    }

    // Filled locally and copied once, so an OS query that throws leaves *out untouched.
    rq_system_info info{};
    info.struct_size = out->struct_size;

    // logical_processors counts the entries enumerate_logical_processors returns, from the same
    // walk, so worker placement can rely on the two agreeing.
    const processor_topology topology = read_processor_topology();
    info.logical_processors = static_cast<std::uint32_t>(topology.processors.size());
    info.processor_groups = topology.group_count;

    const isa_selection selection = select_isa(read_cpu_features(), RQ_ISA_AUTO);
    info.isa_available_mask = selection.available_mask;
    info.isa_best = selection.best;

    info.lock_privilege_held = lock_memory_privilege_held() ? 1u : 0u;

    MEMORYSTATUSEX memory{};
    memory.dwLength = sizeof(memory);
    if (!GlobalMemoryStatusEx(&memory)) {
        throw_os_error(GetLastError(), "GlobalMemoryStatusEx failed");
    }
    info.total_phys_bytes = memory.ullTotalPhys;
    info.avail_phys_bytes = memory.ullAvailPhys;

    info.large_page_min_bytes = static_cast<std::uint64_t>(GetLargePageMinimum());

    read_cpu_brand(info.cpu_name, sizeof(info.cpu_name));

    *out = info;
    return RQ_OK;
}

}  // namespace rq::engine
