/**
 * @file isa_dispatch.cpp
 * @brief CPUID and XGETBV feature read, pure instruction-set selection and kernel-table lookup.
 *
 * This unit is compiled at the x64 SSE2 baseline and must never receive an /arch setting: it runs
 * before any instruction set has been selected, so every instruction it contains has to execute
 * on every x64 CPU. It reads the higher-ISA kernel tables only as data and never calls an entry.
 */
#include "core/kernels/isa_dispatch.h"
#include "core/kernels/kernels.h"
#include "ramquick_engine.h"

// __cpuidex is declared in <intrin.h> and _xgetbv in <immintrin.h>. Both headers hold
// declarations only, so they add no code to this unit.
#include <intrin.h>
#include <immintrin.h>
#include <cstdint>

namespace rq::engine {

cpu_features read_cpu_features() noexcept {
    cpu_features features{};
    int regs[4] = {};

    __cpuidex(regs, 0, 0);
    features.max_basic_leaf = static_cast<std::uint32_t>(regs[0]);

    // A leaf above the highest basic leaf is not an error: Intel CPUs answer it with the data of
    // the highest basic leaf, whose bits would be misread as features. Unreadable leaves stay 0.
    if (features.max_basic_leaf >= 1) {
        __cpuidex(regs, 1, 0);
        features.leaf1_ecx = static_cast<std::uint32_t>(regs[2]);
    }
    if (features.max_basic_leaf >= 7) {
        __cpuidex(regs, 7, 0);
        features.leaf7_ebx = static_cast<std::uint32_t>(regs[1]);
    }

    // XGETBV raises #UD unless the OS has set CR4.OSXSAVE, which CPUID.1:ECX bit 27 reports. That
    // fault is an SEH exception the engine never catches, so it would terminate the process.
    if ((features.leaf1_ecx & k_cpuid1_ecx_osxsave) != 0) {
        features.xcr0 = _xgetbv(0);
    }
    return features;
}

isa_selection select_isa(const cpu_features& features, std::uint32_t isa_override) noexcept {
    // Synthetic inputs may claim leaf-7 bits or an XCR0 value the CPU could not have reported;
    // both are discarded exactly where read_cpu_features would have left them 0.
    const std::uint32_t leaf7 = features.max_basic_leaf >= 7 ? features.leaf7_ebx : 0u;
    const bool osxsave = (features.leaf1_ecx & k_cpuid1_ecx_osxsave) != 0;
    const std::uint64_t xcr0 = osxsave ? features.xcr0 : 0u;

    // Hardening beyond the bare AVX2 and AVX-512 feature bits: /arch:AVX2 may also emit FMA and
    // BMI1/BMI2 instructions, so the AVX2 masks include those bits, and /arch:AVX512 code may use
    // any AVX2 instruction, so AVX-512 requires the whole AVX2 condition. A hypervisor masking one
    // of them then falls back a level instead of faulting on an illegal instruction.
    const bool avx2_ok =
        (features.leaf1_ecx & k_avx2_required_leaf1_ecx) == k_avx2_required_leaf1_ecx &&
        (xcr0 & k_xcr0_avx_state) == k_xcr0_avx_state &&
        (leaf7 & k_avx2_required_leaf7_ebx) == k_avx2_required_leaf7_ebx;
    // F, CD, BW, DQ and VL are every subset /arch:AVX512 may emit, and the OS must save the
    // opmask and full ZMM state, or a context switch would corrupt the kernels' registers.
    const bool avx512_ok = avx2_ok &&
                           (leaf7 & k_avx512_required_leaf7_ebx) == k_avx512_required_leaf7_ebx &&
                           (xcr0 & k_xcr0_avx512_state) == k_xcr0_avx512_state;

    const std::uint32_t available_mask = (1u << RQ_ISA_SSE2) | (avx2_ok ? 1u << RQ_ISA_AVX2 : 0u) |
                                         (avx512_ok ? 1u << RQ_ISA_AVX512 : 0u);
    const std::uint32_t best = avx512_ok ? RQ_ISA_AVX512 : avx2_ok ? RQ_ISA_AVX2 : RQ_ISA_SSE2;

    // The range check guards the shift. RQ_ISA_AUTO has no bit in available_mask, so it is never
    // an accepted override and resolves to best without being rejected.
    const bool override_accepted =
        isa_override <= RQ_ISA_AVX512 && (available_mask & (1u << isa_override)) != 0;

    return isa_selection{
        .selected = override_accepted ? isa_override : best,
        .best = best,
        .available_mask = available_mask,
        .clflushopt = (leaf7 & k_cpuid7_ebx_clflushopt) != 0,
        .override_rejected = isa_override != RQ_ISA_AUTO && !override_accepted,
    };
}

kernel_table tables_for(const isa_selection& selection) noexcept {
    // Copying a table reads constinit data only; no entry of a higher-ISA set executes here.
    kernel_table table = selection.selected == RQ_ISA_AVX512 ? k_kernels_avx512
                         : selection.selected == RQ_ISA_AVX2 ? k_kernels_avx2
                                                             : k_kernels_sse2;

    // Every set's own flush_range and reread entries use CLFLUSHOPT, which raises #UD where
    // CPUID.7.0:EBX bit 23 is clear. CLFLUSH has the same write-back-and-invalidate effect.
    if (!selection.clflushopt) {
        table.flush_range = &flush_range_clflush;
        table.reread = &reread_clflush;
    }

    // Returned by value: the session keeps this copy for its lifetime, which fixes the selection
    // per session.
    return table;
}

}  // namespace rq::engine
