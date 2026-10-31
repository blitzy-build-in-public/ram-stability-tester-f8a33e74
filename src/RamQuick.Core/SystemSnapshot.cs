namespace RamQuick.Core;

/// <summary>
/// An instruction set the engine's memory kernels can run on.
/// </summary>
/// <remarks>
/// <para>
/// The numeric values equal the native <c>RQ_ISA_*</c> codes of <c>ramquick_engine.h</c>, so
/// <c>RamQuick.Interop</c> converts by value. Native <c>RQ_ISA_AUTO</c> (0) is a request to select
/// the best set, not an instruction set, so it has no member: the wrapper represents it as a null
/// <c>InstructionSet?</c>, and 0 is never a valid value of this type.
/// </para>
/// <para>
/// The summary writes <c>system.instructionSet</c> as <c>AVX-512</c>, <c>AVX2</c> or <c>SSE2</c>
/// through its own mapping, not through the member names.
/// </para>
/// </remarks>
public enum InstructionSet
{
    /// <summary>
    /// SSE2 kernels, architectural on x64. The reduced path, selected only when AVX2 is
    /// unavailable; the UI then shows "Reduced instruction set (SSE2): this test runs slower".
    /// Native <c>RQ_ISA_SSE2</c>.
    /// </summary>
    Sse2 = 1,

    /// <summary>
    /// AVX2 kernels, the full-speed baseline. Native <c>RQ_ISA_AVX2</c>.
    /// </summary>
    Avx2 = 2,

    /// <summary>
    /// AVX-512 kernels, selected only when the CPU reports the F, CD, BW, DQ and VL subsets and
    /// the OS has enabled ZMM state. Native <c>RQ_ISA_AVX512</c>.
    /// </summary>
    Avx512 = 3,
}

/// <summary>
/// The kind of pages that back the locked region under test.
/// </summary>
/// <remarks>
/// The numeric values equal the native <c>RQ_PAGE_MODE_*</c> codes of <c>ramquick_engine.h</c>, so
/// <c>RamQuick.Interop</c> converts by value; 0 is never a valid value of this type. The member
/// names are a serialization contract, written as the summary's <c>memory.pageMode</c>: never
/// rename a member.
/// </remarks>
public enum PageMode
{
    /// <summary>
    /// Every chunk uses 4 KiB pages locked with <c>VirtualLock</c>. Native
    /// <c>RQ_PAGE_MODE_STANDARD</c>.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// Every chunk uses 2 MiB large pages. Used only when the process token already holds
    /// <c>SeLockMemoryPrivilege</c>. Native <c>RQ_PAGE_MODE_LARGE</c>.
    /// </summary>
    Large = 2,

    /// <summary>
    /// Large pages were attempted, but some chunks fell back to 4 KiB pages, for example because
    /// physical memory was too fragmented for a large-page allocation. Native
    /// <c>RQ_PAGE_MODE_MIXED</c>.
    /// </summary>
    Mixed = 3,
}

/// <summary>
/// The DRAM generation of the installed memory modules, read from SMBIOS Type 17 by
/// <c>RamQuick.Sensors</c> (memory type 0x1A is DDR4, 0x22 is DDR5).
/// </summary>
/// <remarks>
/// Managed only: the engine has no equivalent. The member names are written as the summary's
/// <c>memory.type</c> and follow the casing the specification uses, so they are never renamed.
/// </remarks>
public enum MemoryType
{
    /// <summary>
    /// The memory type field is absent or could not be parsed, or no populated module was found.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// DDR4 modules, each with a single 64-bit data bus.
    /// </summary>
    DDR4 = 1,

    /// <summary>
    /// DDR5 modules, each with two independent 32-bit sub-channels. The bit-lane cause rule
    /// therefore takes bit positions modulo 32 instead of 64.
    /// </summary>
    DDR5 = 2,

    /// <summary>
    /// Any other known memory type, or modules that disagree on their type.
    /// </summary>
    Other = 3,
}

/// <summary>
/// An immutable description of the machine as the engine probe sees it, built by
/// <c>RamQuick.Interop</c> from <c>rq_system_info</c> (<c>rq_query_system</c>).
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is captured at Start and written into the progress journal's <c>begin</c> record
/// and the run's summary, so a summary recovered after a crash reports the crashed run's machine
/// state, never that of the next launch. Value equality compares every property.
/// </para>
/// <para>
/// The operating-system description is not part of the snapshot: <c>RunController</c> captures it
/// separately for the journal and the summary.
/// </para>
/// </remarks>
public sealed record SystemSnapshot
{
    /// <summary>
    /// Gets the CPU brand string reported by CPUID, with surrounding whitespace trimmed.
    /// Source: <c>rq_system_info.cpu_name</c>, UTF-8 in a 64-byte field.
    /// </summary>
    public required string CpuName { get; init; }

    /// <summary>
    /// Gets the number of logical processors across all processor groups. The engine starts one
    /// pinned worker per logical processor.
    /// Source: <c>rq_system_info.logical_processors</c>.
    /// </summary>
    public required int LogicalProcessors { get; init; }

    /// <summary>
    /// Gets the number of processor groups; a group holds at most 64 logical processors.
    /// Source: <c>rq_system_info.processor_groups</c>.
    /// </summary>
    public required int ProcessorGroups { get; init; }

    /// <summary>
    /// Gets the best instruction set the CPU and the OS both support. The set a session actually
    /// selects is reported separately when the engine reports Prepared.
    /// Source: <c>rq_system_info.isa_best</c>, which is never <c>RQ_ISA_AUTO</c>.
    /// </summary>
    public required InstructionSet BestIsa { get; init; }

    /// <summary>
    /// Gets the total physical memory, as reported by <c>GlobalMemoryStatusEx</c> at query time.
    /// Source: <c>rq_system_info.total_phys_bytes</c>, in bytes.
    /// </summary>
    public required ulong TotalPhysicalBytes { get; init; }

    /// <summary>
    /// Gets the physical memory available at query time, as reported by
    /// <c>GlobalMemoryStatusEx</c>. Region sizing plans the memory under test from this figure.
    /// Source: <c>rq_system_info.avail_phys_bytes</c>, in bytes.
    /// </summary>
    public required ulong AvailablePhysicalBytes { get; init; }

    /// <summary>
    /// Gets the large-page granularity reported by <c>GetLargePageMinimum</c>: 2 MiB on x64, or 0
    /// when large pages are unsupported.
    /// Source: <c>rq_system_info.large_page_min_bytes</c>, in bytes.
    /// </summary>
    public required ulong LargePageMinimum { get; init; }

    /// <summary>
    /// Gets whether the process token already holds <c>SeLockMemoryPrivilege</c>, which large pages
    /// require. RAMQuick only observes the privilege and never assigns it.
    /// Source: <c>rq_system_info.lock_privilege_held</c>, where 0 maps to
    /// <see langword="false"/> and 1 to <see langword="true"/>.
    /// </summary>
    public required bool LockPrivilegeHeld { get; init; }
}
