/**
 * @file system_info.h
 * @brief System probe: processor topology, instruction sets, physical memory and the
 *        lock-pages privilege.
 *
 * Backs rq_query_system and the engine's worker placement: one worker per active logical
 * processor across every processor group, each pinned explicitly, because Windows 10 does not
 * spread a process's threads across groups.
 *
 * Deliberately free of <windows.h>: core/runner/worker_pool.h and core/errors/error_collector.h
 * include this header for logical_processor, and Windows types stay in system_info.cpp. The
 * AVX2 and AVX-512 kernel translation units must not include it.
 *
 * Every function here is callable from any thread and has no side effects on the process or
 * the system.
 */
#pragma once

#include <cstdint>
#include <vector>

#include "ramquick_engine.h"

namespace rq::engine {

/**
 * One active logical processor, addressed the way SetThreadGroupAffinity addresses it:
 * GROUP_AFFINITY{ .Mask = 1ull << number, .Group = group }.
 *
 * The single processor identity used across the engine: worker_pool pins worker i to entry i
 * of enumerate_logical_processors(), and error_collector copies group and number into
 * rq_error_record.processor_group and rq_error_record.logical_processor.
 */
struct logical_processor {
    /** Processor group, 0-based. */
    std::uint16_t group = 0;
    /** Index within the group, 0-63: the bit position in the group's ActiveProcessorMask. */
    std::uint8_t number = 0;

    /** Two entries are equal when they name the same processor. */
    bool operator==(const logical_processor&) const = default;
};

/**
 * Lists every active logical processor in group-major order: group 0 by ascending number, then
 * group 1, and so on. Read from GetLogicalProcessorInformationEx(RelationGroup).
 *
 * The count equals rq_system_info.logical_processors as filled by query_system.
 *
 * @return One entry per active logical processor; never empty on success.
 * @throws std::system_error carrying the Windows error when the OS query fails. ABI entry points
 *         convert it to RQ_E_INTERNAL, and the coordinator to RQ_END_INTERNAL_FAULT.
 * @throws std::bad_alloc when the result cannot be allocated.
 */
std::vector<logical_processor> enumerate_logical_processors();

/**
 * Reports whether the process token holds SeLockMemoryPrivilege, enabled or not.
 *
 * Observes only: it never enables, adjusts or assigns a privilege. Enabling a privilege the
 * token already holds is left to core/memory/memory_region.cpp, immediately before a
 * large-page allocation.
 *
 * @return true when the token holds the privilege; false when it does not, or when the token
 *         cannot be queried.
 */
bool lock_memory_privilege_held();

/**
 * Fills a caller-owned rq_system_info; the implementation of rq_query_system.
 *
 * Fields written: logical_processors and processor_groups (from enumerate_logical_processors),
 * isa_available_mask (bit n set for each available RQ_ISA_* value n; bit RQ_ISA_AUTO is never
 * set, and bit RQ_ISA_SSE2 always is), isa_best (never RQ_ISA_AUTO), lock_privilege_held (0 or
 * 1, from lock_memory_privilege_held), total_phys_bytes, avail_phys_bytes (at the time of the
 * call), large_page_min_bytes (0 when large pages are unsupported) and cpu_name (the CPUID brand
 * string, UTF-8, NUL-terminated, at most RQ_NAME_SIZE - 1 bytes). struct_size is left as the
 * caller set it.
 *
 * Has no side effects: nothing is enabled, allocated beyond the call or changed in the system.
 *
 * @param out Caller-owned; non-null, with struct_size set to sizeof(rq_system_info).
 * @return RQ_OK; RQ_E_INVALID_ARGUMENT for a null out or a wrong struct_size, in which case
 *         nothing is written.
 * @throws std::system_error when an OS query fails, and std::bad_alloc on allocation failure.
 *         api.cpp wraps the call in the boundary try/catch, which returns RQ_E_INTERNAL.
 */
rq_result query_system(rq_system_info* out);

}  // namespace rq::engine
