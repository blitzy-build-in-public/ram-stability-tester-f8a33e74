/**
 * @file isa_dispatch.h
 * @brief CPU feature read, pure instruction-set selection and kernel-table lookup.
 *
 * The engine runs the CPUID and XGETBV checks once per session, at session creation, selects
 * AVX-512, else AVX2, else SSE2, and keeps that kernel set for the session's lifetime. The
 * selected value is reported in rq_prepared.isa_selected; the host shows "Reduced instruction
 * set (SSE2): this test runs slower" when it is RQ_ISA_SSE2, so that value must be exact.
 *
 * The work is split so the decision can be tested without the CPU it describes:
 * read_cpu_features executes the instructions and keeps raw register values, select_isa is a
 * pure function of those values, and tables_for maps the selection to a kernel table.
 *
 * Call sites:
 * - core/session.cpp, once in session creation: read_cpu_features, then
 *   select_isa(features, cfg.isa_override), then tables_for(selection), whose copy the session
 *   keeps for its lifetime. A selection with override_rejected set makes the configuration
 *   invalid (RQ_E_INVALID_ARGUMENT).
 * - core/system_info.cpp: select_isa(read_cpu_features(), RQ_ISA_AUTO) for
 *   rq_system_info.isa_available_mask and rq_system_info.isa_best.
 * - tests/engine.tests: select_isa over synthetic cpu_features built from the constants below,
 *   and tables_for over synthetic selections.
 *
 * @par Availability conditions (flagged deviation: hardening)
 * The plan defines AVX2 as AVX (OSXSAVE, AVX and the XCR0 SSE and AVX state) plus
 * CPUID.7.0:EBX bit 5, and AVX-512 as F, CD, BW, DQ and VL plus XCR0 0xE6, because those are
 * every subset /arch:AVX512 may emit. The same reasoning is applied to /arch:AVX2: Microsoft
 * Learn's /arch (x64) page states that it also enables FMA and "certain BMI instructions", and
 * it defines __AVX2__ under /arch:AVX512. AVX2 availability therefore also requires FMA, BMI1
 * and BMI2, and AVX-512 availability requires the full AVX2 condition. Every shipping AVX2 CPU
 * reports these bits, so selection is unchanged on real hardware, while a hypervisor that masks
 * one of them can no longer lead to an illegal-instruction fault: the engine never catches SEH
 * faults, so one would terminate the process. Synthetic AVX2 and AVX-512 inputs in tests must
 * set these bits.
 *
 * @par Build constraint
 * Baseline only: included by isa_dispatch.cpp, core/session.cpp, core/system_info.cpp and the
 * native tests, never by kernels_avx2.cpp or kernels_avx512.cpp. It still holds only constants,
 * compile-time checks, aggregates with trivial special members and function declarations, so
 * it emits no code into any translation unit that includes it.
 *
 * Threading: every function is stateless and callable from any thread.
 */
#pragma once

#include <cstdint>

#include "ramquick_engine.h"
#include "core/kernels/kernels.h"

namespace rq::engine {

/* ---------------------------------------------------------------------------------------------
 * CPUID and XCR0 bits (Intel SDM Vol. 2A, CPUID; Vol. 1, chapters 13-15)
 * ------------------------------------------------------------------------------------------- */

/**
 * @name CPUID.(EAX=1):ECX feature bits
 * @{
 */
/** Bit 12, FMA: the fused multiply-add instructions /arch:AVX2 may emit. */
inline constexpr std::uint32_t k_cpuid1_ecx_fma = 1u << 12;
/**
 * Bit 27, OSXSAVE: the OS has set CR4.OSXSAVE, so XGETBV may execute. Without it XGETBV raises
 * #UD and XCR0 cannot be read, so no AVX state may be assumed.
 */
inline constexpr std::uint32_t k_cpuid1_ecx_osxsave = 1u << 27;
/** Bit 28, AVX. */
inline constexpr std::uint32_t k_cpuid1_ecx_avx = 1u << 28;
/** @} */

/**
 * @name CPUID.(EAX=7,ECX=0):EBX feature bits
 * Valid only when CPUID.(EAX=0):EAX, the highest basic leaf, is at least 7.
 * @{
 */
/** Bit 3, BMI1: bit-manipulation instructions /arch:AVX2 may emit. */
inline constexpr std::uint32_t k_cpuid7_ebx_bmi1 = 1u << 3;
/** Bit 5, AVX2. */
inline constexpr std::uint32_t k_cpuid7_ebx_avx2 = 1u << 5;
/** Bit 8, BMI2: bit-manipulation instructions /arch:AVX2 may emit. */
inline constexpr std::uint32_t k_cpuid7_ebx_bmi2 = 1u << 8;
/** Bit 16, AVX512F (foundation). */
inline constexpr std::uint32_t k_cpuid7_ebx_avx512f = 1u << 16;
/** Bit 17, AVX512DQ (doubleword and quadword instructions). */
inline constexpr std::uint32_t k_cpuid7_ebx_avx512dq = 1u << 17;
/**
 * Bit 23, CLFLUSHOPT. Independent of the vector level: where it is absent, tables_for
 * substitutes the CLFLUSH helpers, because CLFLUSHOPT would raise #UD there.
 */
inline constexpr std::uint32_t k_cpuid7_ebx_clflushopt = 1u << 23;
/** Bit 28, AVX512CD (conflict detection). */
inline constexpr std::uint32_t k_cpuid7_ebx_avx512cd = 1u << 28;
/** Bit 30, AVX512BW (byte and word instructions). */
inline constexpr std::uint32_t k_cpuid7_ebx_avx512bw = 1u << 30;
/** Bit 31, AVX512VL (128- and 256-bit vector lengths). */
inline constexpr std::uint32_t k_cpuid7_ebx_avx512vl = 1u << 31;
/** @} */

/**
 * @name XCR0 state components
 * A CPU feature is usable only when the OS also saves and restores its register state across
 * context switches; XCR0 reports which state components the OS has enabled.
 * @{
 */
/** Bits 1 and 2: SSE (XMM) and AVX (upper YMM halves) state, required for AVX and AVX2. */
inline constexpr std::uint64_t k_xcr0_avx_state = 0x6;
/**
 * Bits 1, 2, 5, 6 and 7: SSE, AVX, opmask (k0-k7), ZMM_Hi256 (upper halves of ZMM0-15) and
 * Hi16_ZMM (ZMM16-31) state, required for AVX-512.
 */
inline constexpr std::uint64_t k_xcr0_avx512_state = 0xE6;
/** @} */

/**
 * @name Required masks: the exact conditions select_isa applies
 * AVX2 is available when every bit of k_avx2_required_leaf1_ecx is set in CPUID.1:ECX, every
 * bit of k_avx2_required_leaf7_ebx is set in CPUID.7.0:EBX, the highest basic leaf is at least
 * 7, and (XCR0 & k_xcr0_avx_state) == k_xcr0_avx_state. AVX-512 is available when AVX2 is,
 * every bit of k_avx512_required_leaf7_ebx is set in CPUID.7.0:EBX and
 * (XCR0 & k_xcr0_avx512_state) == k_xcr0_avx512_state. FMA, BMI1 and BMI2 are part of the AVX2
 * condition by the hardening described in the file comment.
 * @{
 */
/** CPUID.1:ECX bits AVX2 requires: OSXSAVE, AVX and FMA. */
inline constexpr std::uint32_t k_avx2_required_leaf1_ecx =
    k_cpuid1_ecx_osxsave | k_cpuid1_ecx_avx | k_cpuid1_ecx_fma;
/** CPUID.7.0:EBX bits AVX2 requires: AVX2, BMI1 and BMI2. */
inline constexpr std::uint32_t k_avx2_required_leaf7_ebx =
    k_cpuid7_ebx_avx2 | k_cpuid7_ebx_bmi1 | k_cpuid7_ebx_bmi2;
/**
 * CPUID.7.0:EBX bits AVX-512 requires: the AVX2 bits plus F, DQ, CD, BW and VL, every subset
 * /arch:AVX512 may emit.
 */
inline constexpr std::uint32_t k_avx512_required_leaf7_ebx =
    k_avx2_required_leaf7_ebx | k_cpuid7_ebx_avx512f | k_cpuid7_ebx_avx512dq |
    k_cpuid7_ebx_avx512cd | k_cpuid7_ebx_avx512bw | k_cpuid7_ebx_avx512vl;
/** @} */

/* The AVX-512 condition must imply the AVX2 condition, since /arch:AVX512 code may use AVX2. */
static_assert((k_avx512_required_leaf7_ebx & k_avx2_required_leaf7_ebx) ==
                  k_avx2_required_leaf7_ebx,
              "AVX-512 leaf-7 requirements must include the AVX2 requirements");
static_assert((k_xcr0_avx512_state & k_xcr0_avx_state) == k_xcr0_avx_state,
              "AVX-512 XCR0 state must include the AVX state");
/* isa_selection.available_mask holds one bit per RQ_ISA_* value in a uint32_t. */
static_assert(RQ_ISA_AUTO < RQ_ISA_SSE2 && RQ_ISA_SSE2 < RQ_ISA_AVX2 &&
                  RQ_ISA_AVX2 < RQ_ISA_AVX512 && RQ_ISA_AVX512 < 32u,
              "RQ_ISA_* values must be ordered and fit the available mask");

/* ---------------------------------------------------------------------------------------------
 * Feature record and selection
 * ------------------------------------------------------------------------------------------- */

/**
 * Raw CPUID and XCR0 register values: the only input select_isa reads.
 *
 * Raw values rather than decoded flags keep the whole decision in one pure function, which the
 * tests drive with synthetic inputs built from the constants above. SSE2 and CLFLUSH are
 * architectural on x64 and are not recorded. Like kernel_table it has no member initialisers,
 * so its special members stay trivial: value-initialise it (cpu_features{}) before setting
 * fields by hand.
 */
struct cpu_features {
    /** CPUID.(EAX=0):EAX, the highest basic leaf the CPU supports. */
    std::uint32_t max_basic_leaf;
    /** CPUID.(EAX=1):ECX; 0 when max_basic_leaf < 1. */
    std::uint32_t leaf1_ecx;
    /**
     * CPUID.(EAX=7,ECX=0):EBX; 0 when max_basic_leaf < 7. select_isa also ignores it when
     * max_basic_leaf < 7, so a synthetic input cannot claim leaf-7 features the CPU cannot
     * report.
     */
    std::uint32_t leaf7_ebx;
    /**
     * XCR0 as returned by _xgetbv(0), read only when leaf1_ecx has k_cpuid1_ecx_osxsave set;
     * otherwise 0, because XGETBV without OSXSAVE raises #UD, an SEH fault the engine never
     * catches. select_isa ignores it when OSXSAVE is clear.
     */
    std::uint64_t xcr0;
};

/**
 * The instruction-set decision for one session or one system query.
 *
 * An aggregate with trivial special members, so tests can build one with designated
 * initialisers to exercise tables_for without the CPU it names.
 */
struct isa_selection {
    /**
     * RQ_ISA_* set the session uses: RQ_ISA_SSE2, RQ_ISA_AVX2 or RQ_ISA_AVX512, never
     * RQ_ISA_AUTO and never a set missing from available_mask. Reported in
     * rq_prepared.isa_selected.
     */
    std::uint32_t selected;
    /**
     * Best available set, AVX-512 before AVX2 before SSE2; never RQ_ISA_AUTO. Reported in
     * rq_system_info.isa_best.
     */
    std::uint32_t best;
    /**
     * Bit n set when RQ_ISA_* value n is available. Bit RQ_ISA_SSE2 is always set and bit
     * RQ_ISA_AUTO never is. Reported in rq_system_info.isa_available_mask.
     */
    std::uint32_t available_mask;
    /**
     * CPUID.7.0:EBX bit 23 with a highest basic leaf of at least 7. When false, tables_for
     * returns the CLFLUSH flush and re-read helpers.
     */
    bool clflushopt;
    /**
     * True when the requested override was neither RQ_ISA_AUTO nor an available set, including
     * values outside the RQ_ISA_* range; selected then equals best. core/session.cpp rejects
     * such a configuration with RQ_E_INVALID_ARGUMENT, because forcing an unavailable set would
     * execute instructions the CPU lacks.
     */
    bool override_rejected;
};

/**
 * Reads the CPU's feature registers for select_isa.
 *
 * Executes CPUID leaf 0, leaf 1 when the highest basic leaf is at least 1, leaf 7 subleaf 0
 * when it is at least 7, and XGETBV(0) only when CPUID.1:ECX reports OSXSAVE. Neither
 * instruction changes processor or OS state, so the call has no side effects, may be repeated
 * and returns the same values for the life of the process. Compiled at the SSE2 baseline, so it
 * runs on every x64 CPU.
 *
 * @return The raw register values, with fields the CPU cannot report set to 0.
 */
cpu_features read_cpu_features() noexcept;

/**
 * Chooses the instruction set from raw feature values.
 *
 * Pure: it executes no CPUID or XGETBV, reads no global or static state and performs no I/O, so
 * its result depends only on its arguments and tests can call it with synthetic inputs.
 *
 * Availability, exactly:
 * - leaf7 is features.leaf7_ebx when features.max_basic_leaf >= 7, else 0; xcr0 is
 *   features.xcr0 when features.leaf1_ecx has k_cpuid1_ecx_osxsave set, else 0.
 * - SSE2 is always available.
 * - AVX2 is available when (leaf1_ecx & k_avx2_required_leaf1_ecx) == k_avx2_required_leaf1_ecx,
 *   (leaf7 & k_avx2_required_leaf7_ebx) == k_avx2_required_leaf7_ebx and
 *   (xcr0 & k_xcr0_avx_state) == k_xcr0_avx_state.
 * - AVX-512 is available when AVX2 is,
 *   (leaf7 & k_avx512_required_leaf7_ebx) == k_avx512_required_leaf7_ebx and
 *   (xcr0 & k_xcr0_avx512_state) == k_xcr0_avx512_state.
 * - best is AVX-512 when available, else AVX2 when available, else SSE2; clflushopt is
 *   (leaf7 & k_cpuid7_ebx_clflushopt) != 0.
 *
 * Override:
 * - RQ_ISA_AUTO, which the application always passes, selects best.
 * - An available set is selected as given, so native tests can force each path for
 *   equivalence checks; forcing RQ_ISA_SSE2 is always possible.
 * - Any other value, an unavailable set or one outside the RQ_ISA_* range, sets
 *   override_rejected and selects best, so selected is never an unavailable set.
 *
 * @param features Raw register values, normally from read_cpu_features.
 * @param isa_override rq_session_config.isa_override: RQ_ISA_AUTO or a forced RQ_ISA_* set.
 * @return The selection; see isa_selection for each field's guarantee.
 */
isa_selection select_isa(const cpu_features& features, std::uint32_t isa_override) noexcept;

/**
 * Returns the kernel table for a selection, by value.
 *
 * Copies k_kernels_avx512, k_kernels_avx2 or k_kernels_sse2 for selection.selected; any other
 * value yields the SSE2 set, which runs on every x64 CPU. When selection.clflushopt is false,
 * flush_range and reread are replaced by flush_range_clflush and reread_clflush, because the
 * sets' own entries use CLFLUSHOPT, which would raise #UD there. Copying a table reads data only
 * and executes no higher-ISA code. The session keeps the returned copy for its lifetime, which
 * fixes the selection per session, and real_memory_access copies it again.
 *
 * @param selection A selection from select_isa, or a synthetic one in tests.
 * @return The table whose isa field is the set copied.
 */
kernel_table tables_for(const isa_selection& selection) noexcept;

}  // namespace rq::engine
