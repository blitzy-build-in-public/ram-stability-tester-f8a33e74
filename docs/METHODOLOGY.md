# RAMQuick methodology

RAMQuick is a fail-fast checker for the tune–test–adjust loop: change one BIOS memory setting, boot, run a test, read PASS, FAIL or ABORTED with a ranked cause hint, and adjust. It does not force failures, look for the failure point, or stress memory beyond what heavy daily workloads produce. This document records the research behind the test plan, maps every step to the instability mechanisms it targets, states what software can and cannot establish, and holds the templates for the baseline and validation results.

## Purpose, scope and stress bound

RAMQuick judges whether the PC's current BIOS memory configuration (speed, timings, voltages) is stable enough for daily use. It reports a PASS, FAIL or ABORTED verdict within minutes, with ranked cause hints that are always labelled as hints. The three test types run about 5 minutes (Quick), 20 minutes (Standard) and 60 minutes (Exhaustive). Quick is the iteration test; Standard and Exhaustive confirm a profile before daily use.

Scope limits:

- No BIOS changes, and no reading or writing of memory timings or voltages. The detected-memory summary reads only the SMBIOS capacity, configured speed, memory type and locators.
- No deliberately forced failures, failure-point search, auto-tuning or row-hammer-style access.
- Windows 10/11 on x64 only: no Linux, macOS, pre-OS or Arm operation.
- Coverage is stated against the memory the app could lock, never against memory the OS did not make available to it.
- No GPU VRAM testing and no ECC error-counter reporting.

### Stress bound

This is the authoritative definition of the stress bound; every other section refers to it.

**What software can and cannot control.** RAMQuick chooses data values, virtual access orders, thread counts and idle holds. It cannot observe DRAM row activations, bus switching activity or DRAM power. Peak memory-controller bandwidth is therefore not, by itself, evidence that the stress stays within daily use.

**Equivalence assumptions.** These are assumptions, stated so that they can be challenged. None of them is a measured fact.

- **A1.** If RAMQuick's DRAM bandwidth, CPU package power and DIMM temperature stay within the levels that reference daily workloads reach on the same PC, its activation rate, switching activity and DRAM power are assumed comparable to theirs.
- **A2.** Visiting each virtual line or block once per phase bounds repetition per address. It does not bound activations per physical row, because page and interleave mapping are unknown. The assumption is that whole-window sweeps spread activations across many rows, unlike row-hammer loops, which alternate between a few addresses as fast as possible.
- **A3.** High-toggle and checkerboard values are logical worst cases. Controller data scrambling, where present, makes bus data pseudo-random, as the data of ordinary workloads also is.

**Design limits that hold regardless of measurement.**

- No voltage, frequency or timing control.
- Cache-line flushes only as the [flush-before-verification rule](#flush-before-verification-rule) requires: once per line per verification, once more before a hold, and once per re-read. There are no flush-and-reload loops over a small address set.
- No step alternates over a small address set.
- Thermal limits stop a run only where a sensor is available. Each fresh `Available` reading enforces its own limit (CPU 95 °C, RAM 85 °C); a missing or stale reading leaves that limit inactive, and the notice line says so.
- Workers run at below-normal priority, so Windows and the UI thread keep normal priority.
- `test_access_orders.cpp` checks the visitation property behind A2 at the virtual level.

**Workload-referenced operating bound.**

- **References.** On the validation PC, two reference daily workloads run for 10 minutes each: CPU-only local LLM inference with llama.cpp, using a model larger than the last-level cache (local AI), and the 7-Zip built-in benchmark `7z b` (content creation). They are the heaviest memory workloads among the daily-use categories that can be run reproducibly.
- **Metrics.** A1 is stated over three metrics, so all three are required: DRAM read+write bandwidth, CPU package power and DIMM (SPD) temperature. HWiNFO64 sensor logging, the user's own tool and not shipped with RAMQuick, records them at a 1 s polling interval. A metric HWiNFO64 does not expose may come from another logger the user runs, for example Intel PCM or AMD uProf, provided it samples the references and RAMQuick the same way.
- **Statistics.** The references and a full Exhaustive run are logged in the same session conditions, with the same tool, sensors and polling interval. One statistic is computed identically on both sides: the peak of the 10-second rolling mean for bandwidth and package power, and the peak sample for DIMM temperature.
- **Bound.** For each metric, RAMQuick's statistic over the whole Exhaustive run must not exceed the larger of the two reference values. The 60-minute run offers more chances for a high peak than the 10-minute references, which biases the comparison against RAMQuick, never in its favour.
- **Per-step statistics** are reported as well, so that a step exceeding the bound can be identified.
- **Missing evidence.** If any of the three metrics cannot be logged, A1's premise cannot be checked and no other basis is defined. The constraint is then recorded as **unverified**, naming the missing metric. It is never judged on the remaining metrics.
- **Remedy.** A step that exceeds the bound is retuned in `src/RamQuick.Plans/TestPlans.cs` only, for example to `Half` threads or with a hold, and re-measured.
- The constraint counts as satisfied only once the measurement passes on all three metrics. Results go in [Stress-bound measurement](#stress-bound-measurement) under Validation results.

## Reference tool study

Four reference tools were studied for their step inventories, ordering and speed of exposure. None of them is linked, bundled or copied (see [Provenance](#provenance)); Karhu RAM Test and TestMem5 also serve as validation baselines.

### Karhu RAM Test

- Proprietary algorithm, described by its vendor as "not simple bit patterns", with a selectable random-number generator and an optional CPU-cache test mode.
- Karhu warns that its CPU-cache option can surface cache instability rather than DRAM instability. That warning motivates RAMQuick's [flush-before-verification rule](#flush-before-verification-rule), under which no verification load is served by a CPU-cache copy older than the line's last write.
- **Vendor figures** (v1.1.0.0 statistics): 47.42 % of detected failures by 1 minute, 87.62 % by 30 minutes, 92.97 % by 1 hour and 99.30 % by 6 hours. These vendor figures describe Karhu's own runs. The cohort of systems and profiles behind them, and the denominator, are not characterised. They do not show that RAMQuick will detect most marginal profiles within 5 minutes, and they do not rank the mechanisms in [Instability mechanisms and signatures](#instability-mechanisms-and-signatures).
- Community runs take about 26.5–34 minutes per 1,000 % coverage on 27–30 GB tested, which is about 30–38 %/min.
- Precedent adopted: coverage percentage as the progress metric.

Sources:

- Karhu Software: https://www.karhusoftware.com/

### TestMem5

- Configurations are data: a list of test functions, a sequence and a cycle count. This is the precedent for RAMQuick's data-driven plan, in which each test type is an ordered list of `StepDefinition` records and the step runner holds no step-specific code.
- The `extreme@anta777` configuration:

| Item | Value |
| --- | --- |
| Test functions | 16, IDs 0–15 |
| Function kinds | IDs 0, 1 and 2 are `RefreshStable`, `MirrorMove` and `MirrorMove128`; IDs 3–15 are `SimpleTest` in simple, inverted or arithmetic mode with differing parameters and seeds |
| Cycles | `Cycles=3` |
| Time per test | `Time=500%` |
| Sequence | `4,6,1,12,2,4,12,2,5,7,1,13,2,5,13,2,8,9,1,14,2,8,14,2,10,11,1,15,2,10,3` |
| Run time | About 1.5–2 h for three cycles at 16 GB |

- The `1usmus_v3` configuration: 16 functions, `Time=100%`, 20 cycles, sequence `6,12,2,10,5,1,4,3,0,13,9,14,7,8,1,11,15`, and several hours per run.
- No controlled data ties a first-failing test ID to a particular setting. Community error charts list several possible causes per test, and they are heuristic.

Sources:

- TestMem5 `extreme@anta777` configuration: https://github.com/integralfx/MemTestHelper/blob/oc-guide/TM5-Configs/extreme%40anta777.cfg
- OCCT's TestMem5 conversion guide: https://ocbase.com/resources/TM5-to-OCCT-conversion-guide.pdf
- integralfx DDR4 OC Guide (TestMem5 run times): https://github.com/integralfx/MemTestHelper/blob/oc-guide/DDR4%20OC%20Guide.md
- The TestMem5 `1usmus_v3` configuration, cited by name.

### MemTest86

The current test catalogue (v10.0 onward), and how each test relates to RAMQuick:

| Test | Description | Relation to RAMQuick |
| --- | --- | --- |
| 0 | Address test, walking ones | Not adopted as a separate step |
| 1 | Address test, own address, single core | Not adopted as a separate step |
| 2 | Address test, own address, multi-core | Not adopted as a separate step |
| 3 | Moving inversions, ones and zeros | Precedent for `MovingInversionWalking` (Q4, E13) |
| 4 | Moving inversions, 8-bit walking pattern | Precedent for `MovingInversionWalking` (Q4, E13) |
| 5 | Moving inversions, random pattern | Precedent for `MovingInversionRandom` (S7) |
| 6 | Block move | Not adopted as a separate step |
| 7 | Moving inversions, 32-bit pattern | Precedent for `MovingInversionWalking` (Q4, E13) |
| 8 | Random number sequence | Precedent for the `Random` data steps |
| 9 | Modulo 20, random pattern | Not adopted as a separate step |
| 10 | Bit fade, verification after a wait | Precedent for the hold steps (Q5, S10, E16) |
| 11 | Random number sequence, 64-bit | Precedent for the `Random` data steps |
| 12 | Random number sequence, 128-bit | Precedent for the `Random` data steps |
| 13 | Hammer test | **Excluded**: row-hammer-style access, which the request excludes |
| 14 | DMA test | **Not applicable**: experimental since v10.0 and disabled by default. It writes to a scratch partition on the MemTest86 USB drive by memory-to-disk DMA and reads it back by disk-to-memory DMA, so it validates device DMA paths pre-OS. A user-mode Windows application drives no device DMA |

- The `Random` data steps are Q2, Q3, Q5, Q6, S10–S12, E14–E17 and E19.
- MemTest86 runs pre-OS and takes hours per full pass. Its fixed, ordered test list is the precedent for RAMQuick's fixed step order.

Sources:

- MemTest86 individual test descriptions: https://www.memtest86.com/tech_individual-test-descr.html
- Archived copy of the same page (Internet Archive): https://web.archive.org/web/20261002110228/https://www.memtest86.com/tech_individual-test-descr.html

### HCI MemTest

- Varied patterns held for varying periods before verification.
- One instance per core, with the available RAM split evenly among them. This is the precedent for RAMQuick's even split of the region across one pinned worker per logical processor.
- The manual calls 100 % coverage thorough and 400 % suitable for rare intermittent errors. It does not define how coverage aggregates across instances.

Sources:

- HCI MemTest manual: https://hcidesign.com/memtest/manual.html

### Findings that shape RAMQuick

Precedents adopted:

- Data-driven configurations (TestMem5): each test type is a declarative, ordered list of named steps with time and coverage budgets.
- An even split of the region per worker (HCI MemTest), pinned to every logical processor across all processor groups.
- Coverage percentage as the progress metric (Karhu RAM Test).
- A fixed, ordered test list (MemTest86), with moving-inversion and bit-fade-style hold steps.

Pitfalls avoided:

- Testing cache instead of DRAM: every verified line is flushed first, by the [flush-before-verification rule](#flush-before-verification-rule).
- Relying on `MOVNTDQA` for write-back memory: it gives no non-temporal effect there and is not used.
- Holding a stop request behind a long sleep: holds wait on the stop event, and workers check the stop flag after every 1 MiB block.
- One UI callback per error: error callbacks are batched at most 10 times a second, and detailed records are capped at 10,000 with a true total kept.
- Over-allocation causing paging: sizing always leaves at least 10 % of available memory, and never less than 1 GiB, free, and the region is locked.
- C++ exceptions crossing the ABI: the engine converts them to result codes at its boundary.
- Row-hammer loops (MemTest86 test 13): excluded.
- A journal left in OS cache across a power cut: every durable journal record is written through and flushed to disk.

Ordering and targets:

- No tool publishes a controlled mechanism-to-test mapping. RAMQuick's step ordering therefore rests on reasoning about how soon each mechanism can be exposed. It is a design rationale, not measured fact.
- The coverage targets of 200 %, 800 % and 2500 % over 5, 20 and 60 minutes (about 40 %/min) match Karhu's observed community rate. They are floors that a bandwidth-bound engine is expected to exceed, not throughput goals.
- **Hypothesis H1**: dense random traffic from the first second lets a 5-minute run catch a failing profile.
- **Hypothesis H2**: the step order of Quick exposes instability soonest.
- Both hypotheses rest partly on the Karhu vendor figures and are tested by criterion 1 on the validation PC. Their status is recorded under [Hypotheses](#hypotheses).

## DRAM tuning practice

No reviewed source ranks settings by how often they fail across platforms. The IC, board, IMC, frequency and temperature all shift the margins. The weak margins below are those commonly reported in tuning practice:

- **Refresh.** Too low a tRFC gives fast errors or lockups. Too high a tREFI is temperature-sensitive. Sources: integralfx DDR4 OC Guide (https://github.com/integralfx/MemTestHelper/blob/oc-guide/DDR4%20OC%20Guide.md) and goraxyz DDR5 OC Guide (https://github.com/goraxyz/DDR5-OC/blob/main/OC%20Guide.md).
- **Primary and secondary timings.** tRCD is a commonly difficult DDR5 primary timing. The floors for tRP, tWR, tWTR and tRTP vary by IC. Source: goraxyz DDR5 OC Guide.
- **Voltages and IMC/PHY margin.** Low VDD/VDDQ and limited IMC/PHY margin cap frequency and timings: VDDIO and SoC/VDDP on AM5 (AMD Ryzen Master user guide, https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/RAM), and VDD2 on Intel (Intel Core Ultra 200S datasheet). Poor ODT, RTT or drive strength shows up as reduced margin, training failure or no-POST.
- **Warm-up failures** are common. One ASUS DDR5-8400 setup erred after about 40 minutes at 67–68 °C and passed near 38 °C with added airflow (https://rog-forum.asus.com/t5/push-the-limits/dram-temperatures-and-stability/bc-p/1150839). In Igor's Lab measurements the SPD-hub reading ran about 12 °C below the module surface (https://www.igorslab.de/en/how-hot-is-ddr5-memory-really-reading-value-from-spd-stroke-vs-real-measurement-at-module-investigative/2/ and its archived copy https://web.archive.org/web/20261011044454/https://www.igorslab.de/en/how-hot-is-ddr5-memory-really-reading-value-from-spd-stroke-vs-real-measurement-at-module-investigative/2/).

### DDR5 refresh

DDR5 refresh depends on mode and temperature (JESD79-5, https://faculty-web.msoe.edu/johnsontimoj/ELE4142/files4142/DDR5_Spec.pdf):

- In normal all-bank refresh mode, 8192 refresh commands fall within each 32 ms refresh period, so tREFI is about 3.9 µs at Tcase ≤ 85 °C. It halves, to about 1.95 µs, from 85 °C to 95 °C.
- Fine-granularity refresh mode uses half the normal interval at ≤ 85 °C and a quarter of it above.
- Same-bank refresh commands have their own timing.
- An example normal-mode tRFC1 minimum is 295 ns for 16 Gb dies.
- DDR4 values, and the values tuned BIOS profiles use, differ again.

Elsewhere in this document, "the refresh interval" means "every few microseconds"; the exact interval is set by the DRAM type, the refresh mode and the BIOS tREFI.

## Instability mechanisms and signatures

The exposures below are **heuristics**. Software chooses only data values and virtual addresses. Address mapping, channel and rank interleaving, bank hashing and data scrambling decide which banks, rows and DQ lines are actually exercised. Every exposure stays inside the [stress bound](#stress-bound).

| Mechanism | Typical failure signature | Soonest exposure (pattern, order, delay) | First step |
| --- | --- | --- | --- |
| Signal integrity at high frequency (ODT, RTT, drive strength) | Errors recurring on one or a few bit lanes, data-pattern dependent, within seconds of heavy traffic | High-toggle data, sequential, no delay, all threads | Q1 |
| Insufficient VDD/VDDQ | Scattered single-bit flips, often one lane for VDDQ, worse when hot | High-toggle, then moving inversion with walking bits | Q1, Q4 |
| tRCD/tRP/tRAS | Errors only under row-miss-heavy orders, addresses scattered | Random data, bank-stride order | Q2 |
| tFAW/tRRD | Errors under dense activations across banks in short windows | Bank-stride order cycling power-of-two strides | Q2 |
| Bank, rank and channel switching | Errors under orders crossing banks and ranks, unaffected by delay | Bank-stride and random-block orders on all threads | Q2, Q6 |
| tWR/tWTR/tRTP/tCWL; read/write turnaround | Errors on reads issued just after writes to nearby addresses; time since write under 1 ms | Turnaround order: write a block, verify the block four positions behind | Q3 |
| tRFC (too low) | Fast errors or a hang under any heavy traffic, since refresh recurs every few microseconds | Any full-bandwidth step | Q1–Q3 |
| tREFI (too high) / retention | Errors only on verification after an idle hold; temperature-dependent | Random data, whole-slice write, hold 5 s / 30 s / 120 s, verify | Q5 |
| SoC/IMC voltage, power delivery | Errors spread over addresses and bits, present at full thread load, absent at half load | All-thread random mix contrasted with a half-thread step | Q6 (contrast in S11) |
| Temperature sensitivity | Errors appearing only after warm-up, rising with SPD-hub temperature | Sustained full-load heat soak late in the plan | S12, E19 |

The request supplied these signature-to-category examples as the starting heuristics. The six signature rules of the cause analyzer implement them (see [Cause-hint heuristics and their limits](#cause-hint-heuristics-and-their-limits)):

- "A single repeated bit position suggests a voltage or signal-integrity problem (VDD, VDDQ, drive strength or ODT)."
- "Errors that appear only after a long idle gap suggest refresh or retention (tREFI, tRFC, temperature)."
- "Errors that appear only under rapid bank or row switching suggest tRCD/tRP/tRAS/tFAW/tRRD."
- "Errors that appear on read-after-write suggest tWR/tWTR/tRTP."
- "Errors that appear only with all threads busy suggest SoC/IMC voltage or power delivery."
- "Errors that cluster in one address region suggest a specific rank or DIMM."

## Step-to-mechanism map

Every step of every test type, with its attributes exactly as declared in `src/RamQuick.Plans/TestPlans.cs` and its primary and secondary mechanisms; `TestPlanCatalogTests` checks this table against the code.

| ID | Step name | Types | Pattern | Order | Threads | Hold | Primary mechanism | Secondary mechanisms |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Q1 | High-toggle sweep | Q, S, E | `HighToggle` | `SequentialAscending` | `All` | — | `SignalIntegrity` | `VddVddqVoltage`, `RefreshTimings` |
| Q2 | Random bank thrash | Q, S, E | `Random` | `BankStride` | `All` | — | `RowTimings` | `ActivationWindow`, `BankRankChannelSwitching` |
| Q3 | Read-after-write turnaround | Q, S, E | `Random` | `Turnaround` | `All` | — | `ReadWriteTurnaround` | `WriteTimings` |
| Q4 | Moving inversion, walking bits | Q, S, E | `MovingInversionWalking` | `SequentialAscending` | `All` | — | `VddVddqVoltage` | — |
| Q5 | Refresh hold | Q, S, E | `Random` | `RandomBlocks` | `All` | 5 s | `RefreshTimings` | `TemperatureSensitivity` |
| Q6 | Full-load random mix | Q, S, E | `Random` | `RandomBlocks` | `All` | — | `SocImcVoltage` | `BankRankChannelSwitching` |
| S7 | Moving inversion, random | S, E | `MovingInversionRandom` | `SequentialAscending` | `All` | — | `VddVddqVoltage` | — |
| S8 | Checkerboard and inverted | S, E | `Checkerboard` | `SequentialDescending` | `All` | — | `VddVddqVoltage` | — |
| S9 | High-toggle bank thrash | S, E | `HighToggle` | `BankStride` | `All` | — | `BankRankChannelSwitching` | `SignalIntegrity` |
| S10 | Refresh hold, long | S, E | `Random` | `RandomBlocks` | `All` | 30 s | `RefreshTimings` | — |
| S11 | Reduced-load contrast | S, E | `Random` | `RandomBlocks` | `Half` | — | `VddVddqVoltage` | `SocImcVoltage` |
| S12 | Heat soak | S, E | `Random` | `RandomBlocks` | `All` | — | `TemperatureSensitivity` | `SocImcVoltage` |
| E13 | Moving inversion, walking ones and zeros | E | `MovingInversionWalking` | `SequentialAscending` | `All` | — | `VddVddqVoltage` | — |
| E14 | Random turnaround, long | E | `Random` | `Turnaround` | `All` | — | `WriteTimings` | — |
| E15 | Random bank thrash, long | E | `Random` | `BankStride` | `All` | — | `ActivationWindow` | `RowTimings` |
| E16 | Refresh hold, extended | E | `Random` | `RandomBlocks` | `All` | 120 s | `RefreshTimings` | — |
| E17 | Reduced-load contrast, long | E | `Random` | `RandomBlocks` | `Half` | — | `VddVddqVoltage` | `SocImcVoltage` |
| E18 | High-toggle random order | E | `HighToggle` | `RandomBlocks` | `All` | — | `SignalIntegrity` | — |
| E19 | Heat soak, long | E | `Random` | `RandomBlocks` | `All` | — | `TemperatureSensitivity` | `SocImcVoltage` |

Note: the moving-inversion patterns (`MovingInversionWalking`, `MovingInversionRandom`) always sweep ascending, then descending, whatever the Order value; the plan stores `SequentialAscending` for them and the engine ignores it.

Every mechanism the request lists, with the settings it covers, its enum value, the steps that target it and the cause category the analyzer's step prior credits:

| Mechanism | Settings | Enum value | Primary in | Secondary in | Analyzer category |
| --- | --- | --- | --- | --- | --- |
| Insufficient VDD/VDDQ voltage | VDD, VDDQ | `VddVddqVoltage` | Q4, S7, S8, S11, E13, E17 | Q1 | `VoltageSignalIntegrity` |
| Insufficient SoC/IMC voltage, power delivery | SoC, VDDIO, VDDP (AM5); VDD2 (Intel) | `SocImcVoltage` | Q6 | S11, S12, E17, E19 | `ImcPowerDelivery` |
| Row timings | tRCD, tRP, tRAS | `RowTimings` | Q2 | E15 | `RowBankTiming` |
| Activation window | tFAW, tRRD | `ActivationWindow` | E15 | Q2 | `RowBankTiming` |
| Bank, rank and channel switching | — | `BankRankChannelSwitching` | S9 | Q2, Q6 | `RowBankTiming` |
| Refresh timings and retention | tRFC, tREFI | `RefreshTimings` | Q5, S10, E16 | Q1 | `RefreshRetention` |
| Write timings | tWR, tWTR, tRTP, tCWL | `WriteTimings` | E14 | Q3 | `WriteReadTurnaround` |
| Read/write turnaround | — | `ReadWriteTurnaround` | Q3 | — | `WriteReadTurnaround` |
| Signal integrity at high frequency | ODT, RTT, drive strength | `SignalIntegrity` | Q1, E18 | S9 | `VoltageSignalIntegrity` |
| Temperature sensitivity | — | `TemperatureSensitivity` | S12, E19 | Q5 | `RefreshRetention` |

`RankOrDimm` receives no prior, because no step targets a rank or DIMM; that category is evidenced by the address-cluster rule alone.

Mapping caveat: attributions to banks, ranks and channels are exposure heuristics, because the strides and orders act on virtual addresses and the physical mapping is invisible to software (see [Step execution model](#step-execution-model)).


## Per-type step plans and rationale

### Step execution model

**Workers.** The region under test is split evenly across one worker per logical processor, across every processor group (https://learn.microsoft.com/en-us/windows/win32/procthread/processor-groups). Each worker is pinned to its processor and runs at below-normal priority. Workers execute each pass phase on their own slice and meet at a barrier between phases.

**Instruction sets.** Three kernel sets implement the same streaming-store, fence, flush, verify and re-read primitives: SSE2, AVX2 and AVX-512. The AVX-512 set is selected only when the F, CD, BW, DQ and VL subsets, every subset `/arch:AVX512` may emit (https://learn.microsoft.com/en-us/cpp/build/reference/arch-x64), are present and the OS has enabled ZMM state. Otherwise AVX2 is selected, and failing that SSE2, which the UI notes as a reduced instruction set.

**Patterns.** Every value is a stateless function of word index, pass and seed, so verification and re-reads regenerate the expected value without storing it.

- `Checkerboard`: `0x5555…` and `0xAAAA…` alternating per word, inverted every pass, so the step covers both the checkerboard and its inverse.
- `Random`: SplitMix64 of seed, word index and pass.
- `HighToggle`: logical data complemented between consecutive 64-bit words and between the 32-bit halves of each word, alternating per pass. It aims at maximum toggling on 64-bit (DDR4 channel) and 32-bit (DDR5 sub-channel) beats.
- `MovingInversionWalking`: MemTest86-style moving inversion over walking bits. The base pattern is walking ones on even passes and walking zeros on odd passes, one set or cleared bit rotating with word index. Each pass writes the base ascending; then, ascending, verifies it and writes the complement; then, descending, verifies the complement and writes the base.
- `MovingInversionRandom`: the same moving-inversion sweeps over `Random` base data.

These are logical values. Burst ordering, ECC and memory-controller data scrambling decide what the DQ lines actually carry; data scrambling is documented, for example, for Intel 12th-generation processors (https://edc.intel.com/content/www/ca/fr/design/ipla/software-development-platforms/client/platforms/alder-lake-desktop/12th-generation-intel-core-processors-datasheet-volume-1-of-2/009/009/data-scrambling/). No pattern guarantees a particular toggle rate on the bus.

**Access orders.**

- `SequentialAscending` and `SequentialDescending`: ascending or descending addresses, window by window.
- `BankStride`: 64 B lines visited at power-of-two virtual strides of 8 KiB, 64 KiB and 256 KiB, cycling per pass. The start offset advances one line per sweep until every line of the window has been visited exactly once per phase.
- `RandomBlocks`: a seeded permutation of 4 KiB blocks.
- `Turnaround`, per window of *n* 4 KiB blocks. Start-up: blocks 0–3 are each streamed and fenced, with no verification. Steady state: for *i* = 4 … *n* − 1, block *i* is streamed, then block *i* − 4 is flushed, fenced and verified. Drain: after block *n* − 1, the last four blocks are flushed, fenced and verified. Every block is therefore verified exactly once per pass unless a stop intervenes, and each block's stream time is recorded, so its time since write is exact.

**Pass phases.**

- Steps without a hold run windowed: each worker writes a 64 MiB window, then verifies it (`VerifyAfterWrite`), window by window in the step's order, so the time since write stays short however large the slice.
- Steps with a hold write the whole slice in `RandomBlocks` order, wait the hold on the stop event, then verify the whole slice (`VerifyAfterHold`).
- Moving inversion uses whole-slice sweeps (`ReadModifyWrite`). Turnaround verifies interleaved with writing (`TurnaroundRead`).
- `Half` thread mode activates even-indexed workers only, each covering its own slice and its odd partner's, so the same memory is tested at half the thread load.

**Mapping limits.** Strides and orders act on virtual addresses. The physical page mapping, channel and rank interleaving and the memory controller's bank hashing are not visible to software. `BankStride` and `Turnaround` are therefore broad exposure heuristics: they make frequent row, bank and rank changes and write-to-read switches likely, and they do not guarantee any particular physical sequence. This applies wherever this document maps a step to a bank, rank or channel mechanism.

### Shared opening block

Standard and Exhaustive begin with Quick's six steps, identical in parameters and budgets (the very same step definitions), and then extend the plan. The longer types thus receive the same exposure, in the same order and at the same times, as Quick, which makes their failure times comparable to Quick's. On stochastic hardware this does not guarantee equal failure times, so "no later than Quick" is verified empirically by criterion 1.

### Quick

Quick: 300 s, coverage ≥ 200 %.

| ID | Step name | Pattern · order · threads · hold | Targets (primary first) | Time budget (s) | Coverage budget (%) |
| --- | --- | --- | --- | --- | --- |
| Q1 | High-toggle sweep | `HighToggle` · `SequentialAscending` · `All` · — | Signal integrity; VDDQ, ODT/drive strength, tRFC under load | 40 | 25 |
| Q2 | Random bank thrash | `Random` · `BankStride` · `All` · — | tRCD/tRP/tRAS; tFAW/tRRD; bank/rank/channel switching | 50 | 35 |
| Q3 | Read-after-write turnaround | `Random` · `Turnaround` · `All` · — | tWR/tWTR/tRTP/tCWL; read/write turnaround | 50 | 35 |
| Q4 | Moving inversion, walking bits | `MovingInversionWalking` · ascending, then descending · `All` · — | VDD/VDDQ cell margin; bit coupling | 60 | 50 |
| Q5 | Refresh hold | `Random` · `RandomBlocks` · `All` · 5 s | tREFI/tRFC; retention; temperature | 50 | 20 |
| Q6 | Full-load random mix | `Random` · `RandomBlocks` · `All` · — | SoC/IMC voltage; power delivery; channel switching | 50 | 35 |
| **Total** | Six steps | — | — | 300 | 200 |

### Standard

Standard: 1200 s, coverage ≥ 800 %. It runs Q1–Q6 (300 s / 200 %), then:

| ID | Step name | Pattern · order · threads · hold | Targets (primary first) | Time budget (s) | Coverage budget (%) |
| --- | --- | --- | --- | --- | --- |
| S7 | Moving inversion, random | `MovingInversionRandom` · ascending, then descending · `All` · — | VDD/VDDQ; data-dependent margins | 150 | 120 |
| S8 | Checkerboard and inverted | `Checkerboard` (inverted each pass) · `SequentialDescending` · `All` · — | VDD/VDDQ; adjacent-bit coupling | 120 | 80 |
| S9 | High-toggle bank thrash | `HighToggle` · `BankStride` · `All` · — | Bank/rank/channel switching; signal integrity | 120 | 100 |
| S10 | Refresh hold, long | `Random` · `RandomBlocks` · `All` · 30 s | tREFI/tRFC; retention | 150 | 40 |
| S11 | Reduced-load contrast | `Random` · `RandomBlocks` · `Half` · — | VDD/VDDQ at half load; contrast step for the SoC/IMC rule | 120 | 60 |
| S12 | Heat soak | `Random` · `RandomBlocks` · `All` · — | Temperature sensitivity; SoC/IMC | 240 | 200 |
| **Total** | Twelve steps, including Q1–Q6 | — | — | 1200 | 800 |

### Exhaustive

Exhaustive: 3600 s, coverage ≥ 2500 %. It runs Q1–Q6 and S7–S12 (1200 s / 800 %), then:

| ID | Step name | Pattern · order · threads · hold | Targets (primary first) | Time budget (s) | Coverage budget (%) |
| --- | --- | --- | --- | --- | --- |
| E13 | Moving inversion, walking ones and zeros | `MovingInversionWalking` · ascending, then descending · `All` · — | VDD/VDDQ | 300 | 250 |
| E14 | Random turnaround, long | `Random` · `Turnaround` · `All` · — | tWR/tWTR/tRTP/tCWL | 300 | 250 |
| E15 | Random bank thrash, long | `Random` · `BankStride` · `All` · — | tFAW/tRRD; tRCD/tRP/tRAS | 300 | 250 |
| E16 | Refresh hold, extended | `Random` · `RandomBlocks` · `All` · 120 s | tREFI/tRFC; retention | 360 | 50 |
| E17 | Reduced-load contrast, long | `Random` · `RandomBlocks` · `Half` · — | VDD/VDDQ; SoC/IMC contrast | 240 | 150 |
| E18 | High-toggle random order | `HighToggle` · `RandomBlocks` · `All` · — | Signal integrity | 300 | 250 |
| E19 | Heat soak, long | `Random` · `RandomBlocks` · `All` · — | Temperature sensitivity; SoC/IMC | 600 | 500 |
| **Total** | Nineteen steps, including Q1–Q6 and S7–S12 | — | — | 3600 | 2500 |

Every hold step's time budget is at least 2.5 times its hold, as `TestPlanValidator` enforces: Q5 50 s ≥ 12.5 s, S10 150 s ≥ 75 s and E16 360 s ≥ 300 s.

### Ordering rationale

**Design hypothesis H2.** This ordering is a design hypothesis, not a measured result. Criterion 1 tests it on the validation PC. Its premise is that dense traffic from second zero exposes marginal settings soonest, so every Quick step runs at full bandwidth on all threads.

- Q1 leads because signal-integrity and VDDQ margins fail on the first heavy transfers at high frequency, and tRFC shortfalls surface under any dense traffic. High-toggle data maximises both.
- Q2 and Q3 follow with the two access-order stresses, row and bank activation, then write-to-read turnaround, that most often limit tightened primary and secondary timings.
- Q4 adds the data-dependent cell-margin check that needs several sweeps.
- Q5 needs a hold, so it costs idle time and comes after the steps that find failures with no wait.
- Q6 closes with sustained all-thread load, which also warms the DIMMs ahead of the longer types' contrast and heat-soak steps.

Temperature sensitivity is only partly exercised in 5 minutes. Standard (S12) and Exhaustive (E19) add the heat-soak steps for it.

If criterion 1 is missed, step order and budgets are retuned in `src/RamQuick.Plans/TestPlans.cs` only, keeping the shared opening block and Quick's 300 s total. The validation is then repeated, and the change and its reason are recorded in the [Plan change log](#plan-change-log).

## Coverage definition and accounting

**Coverage** means bytes verified divided by bytes under test. 100 % means every byte of the tested region was tested once, and 1000 % means every byte was tested ten times.

- A verified byte is a byte whose 8-byte word was read by a verification primitive and compared with its expected value within the current step.
- Moving inversion verifies twice per pass, so it counts twice.
- Re-reads do not count. Data written but not yet verified when a stop arrives does not count.

**Counting.** Each worker keeps a cache-line-padded thread-local counter and adds it to the session's total and the step's total after every 1 MiB block. An independent counting layer in the native tests checks the reported figure against the bytes actually passed to the verify primitives.

**Region sizing** (`src/RamQuick.App/Services/MemorySizing.cs`). The planned size is `min(0.90 × available, available − 1 GiB)`, rounded down to a multiple of workers × 2 MiB, so the rule always leaves at least 10 % of available memory, and never less than 1 GiB, free.

- With 32 GiB available and 16 workers, the plan is 30,903,631,872 bytes (921 × 32 MiB).
- Start is refused when the planned size is below 1 GiB: 1.5 GiB available gives a plan of 0.5 GiB.
- The engine builds each worker's slice from 256 MiB chunks, each allocated, committed and locked by its own calls. Locking needs the minimum working set raised first (https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsizeex).
- If locking fails at the planned size, the size shrinks 10 % per retry down to 1 GiB; below that, the run ends ABORTED with the low-memory message.
- Large pages are used only if `SeLockMemoryPrivilege` is already held; RAMQuick never assigns it (https://learn.microsoft.com/en-us/windows/win32/memory/large-page-support). Otherwise 4 KiB pages are locked.
- `tested_bytes = slice × workers` is reported at preparation and shown as Tested capacity. It is the coverage denominator, including after a shrink.

**Reporting.** Coverage % = 100 × verified bytes ÷ tested bytes. The UI shows it to one decimal with the type's target, for example "412.6 % (target 200 %)". The summary records coverage per step and in total, with `coverage.targetReached`.

### Pass scheduling and overruns

- After every pass, a step continues while `passes_done < max(1, round(time_budget / mean_pass_duration))`.
- Time governs. The coverage budget never extends a step, so fail-fast ordering and Quick's 5 minutes are never traded for coverage.
- When the first pass is no longer than the budget and pass durations are stable, within ±10 % of their mean, a step ends within about half a mean pass of its budget. When durations vary more, the deviation is at most one longest pass.
- When a single pass exceeds the budget, as on very large or slow machines, the step runs exactly one pass. The excess is recorded as `overrun_ms` in the step result, in the `.log` and as `steps[].overrunSeconds` in the summary. Later steps keep their own budgets.
- `TestPlanValidator` enforces the hold rule: a hold step's time budget is at least 2.5 × its hold.

### Time remaining

- Until step 1 completes its first pass, time remaining reads "calculating…".
- After that, it is the current step's term plus the sum of the time budgets of all later steps, recomputed at every progress tick. Before the current step's first pass completes, its term is `max(0, time_budget − step_elapsed)`; afterwards it is `max(0, passes_planned × mean_pass − step_elapsed)`.
- The estimate is exact only to the conditions of pass scheduling above. Oversized passes make it low until their first pass ends.
- With Stop on error off, error handling only lengthens the measured passes, which feeds into the mean, so the countdown continues.

### Flush-before-verification rule

Between the engine's most recent store or load to a line and that line's next verification load, the kernels execute, in order, an `MFENCE`, a flush of the line (`CLFLUSHOPT`, or `CLFLUSH` where CPUID lacks it), and a second `MFENCE`. The first fence makes earlier streaming stores globally visible before the flush; the second orders the flush before the verification load. The rule assumes nothing about the line's earlier cache residency, so it holds on the first pass and on every later pass alike (Intel SDM, https://cdrdv2-public.intel.com/874240/325462-090-sdm-vol-1-2abcd-3abcd-4.pdf).

- **Windowed steps:** stream the window, fence, flush the window, fence, verify the window, for every window of every pass.
- **Hold steps:** stream the slice, fence, flush the slice, fence, so that no dirty cached copy holds the new data back from memory during the hold. Wait the hold, then fence, flush the slice, fence and verify. The second flush follows the hold, so any copy filled before the hold ended is removed.
- **Moving inversion:** the base write and each verify-and-write sweep are each followed by fence, flush of the slice, fence, so the next sweep's loads never hit a copy left by the previous sweep or the previous pass.
- **Turnaround:** each block is flushed, fenced and verified four blocks after its own fenced write, and the drain does the same for the last four blocks.
- **Re-read:** fence, flush the line, fence, one load.
- **Cost:** one flush per verified line per phase plus two fences per window, sweep, block or re-read. This time is inside the measured pass durations that drive scheduling and inside the coverage targets.

**Prefetch and speculation.** Hardware prefetchers and speculative execution can refill a line after its flush, for example ahead of a sequential verification. Such a fill occurs after the line's most recent store became globally visible, so it carries the memory-side contents at fill time, not a pre-flush cached copy. The fill can precede the verification load by a short interval, so `time_since_write_us`, measured to the load, can overstate the data's age when it was fetched.

- **Established**, under the architectural contract: no verification load is satisfied by a CPU-cache or write-combining copy that existed before that line's flush.
- **Not established**: that each verification load itself causes a DRAM array access. Software cannot observe whether a fill is served by the DRAM array, a memory-side cache, controller-level write-to-read forwarding or another platform buffer. The 64 MiB windows and the hold delays make such service unlikely for all but a small tail of each phase; that is a judgement, not a measurement.
- `MOVNTDQA` gives no non-temporal effect on write-back memory and is not used. Streaming stores (`MOVNTDQ`/`VMOVNTDQ`) are used for writes, and the rule does not rely on how a streaming store treats a destination line that is already cached.

### Address reporting

No documented user-mode API yields physical addresses: `QueryWorkingSetEx` reports no page frame number, and AWE returns frame numbers only for its own allocations, under a non-default privilege. Every error record therefore carries the virtual address of its 8-byte word, with `addressKind: Virtual`, and the UI and logs label it "virtual address".


## Cause-hint heuristics and their limits

**Error confirmation.** A mismatching word is re-read once after a fence, a flush of its line and a second fence. The raw expected, actual and re-read values are always kept and displayed. The comparison yields an outcome with an inference that is displayed and logged as an inference, never as an established cell state:

- `RereadMatchesExpected`: "likely read-path fault".
- `RereadRepeatsActual`: "likely write or storage fault". A repeatable read-path fault produces the same values.
- `RereadDiffers`: "likely transient fault".

Every outcome is a confirmed error, since each one shows instability. At most 10,000 detailed records are stored per run, while the true total is kept from engine counters. Error groups (by 8-byte word), group counts and distinct-address counts are derived from the stored records only and are labelled that way once the cap is reached.

**Signature rules.** The analyzer evaluates six rules over the stored records and every step outcome. Each rule fires only when its evidence minimum is met, and then yields a score from 0 to 1. Shares are fractions of the error records analysed.

| Rule | Fires when | Score | Category | Suggestion |
| --- | --- | --- | --- | --- |
| Bit lane | ≥ 2 distinct words are hit, spread over ≥ 1/16 of the region | The share of errors flipping the dominant bit lane if that share is ≥ 0.8, else 0. The lane is the bit index modulo 64, or modulo 32 on DDR5 | `VoltageSignalIntegrity` | "Errors keep hitting the same data bit. Try slightly more DRAM VDD/VDDQ, revisit drive strength or ODT (RTT), or lower the frequency one step." |
| Hold | ≥ 1 hold step ran | The share of errors with access type `VerifyAfterHold` | `RefreshRetention` | "Errors appear only after memory sat idle. Lower tREFI, raise tRFC, or improve DIMM cooling." |
| Bank switching | ≥ 1 sequential-order step completed a pass with zero errors | The share in `BankStride` steps + 0.5 × the share in `RandomBlocks` steps without a hold | `RowBankTiming` | "Errors appear under rapid row and bank switching. Loosen tRCD, tRP or tRAS, or tFAW/tRRD." |
| Turnaround | ≥ 1 non-turnaround step without a hold completed a pass with zero errors | The share of errors with access type `TurnaroundRead` | `WriteReadTurnaround` | "Errors appear when data is read right after being written. Loosen tWR, tWTR or tRTP (and tCWL)." |
| Load | ≥ 1 `Half` step completed a pass with zero errors | The share of errors in `All`-thread steps without a hold | `ImcPowerDelivery` | "Errors appear only with every core loading memory. Raise SoC/IMC voltage (VDDIO or VDD2) slightly, or check CPU power delivery." |
| Address cluster | ≥ 3 errors, or ≥ 2 at the same word | The share of errors inside the densest window of 1/16 of the region, by region offset | `RankOrDimm` | "Errors concentrate in one part of memory. One rank or DIMM may be marginal; test the DIMMs one at a time or reseat them." |

**Step-mechanism prior.** Each category also gets a prior of `0.3 × share of errors from steps whose PrimaryMechanism maps to the category`:

- `VddVddqVoltage`, `SignalIntegrity` → `VoltageSignalIntegrity`
- `SocImcVoltage` → `ImcPowerDelivery`
- `RowTimings`, `ActivationWindow`, `BankRankChannelSwitching` → `RowBankTiming`
- `RefreshTimings`, `TemperatureSensitivity` → `RefreshRetention`
- `WriteTimings`, `ReadWriteTurnaround` → `WriteReadTurnaround`
- `RankOrDimm` receives no prior.

The prior means that a single error, the usual case with Stop on error on, still ranks the cause its failing step targets.

**Ranking.**

- A category's score is `max(signature score, prior)`, one policy for every category. A signature scoring above 0.3 therefore outranks every prior, while a 0.1 signature loses to a 0.3 prior.
- Categories scoring ≥ 0.05 are kept, and likelihood % = score ÷ the sum of the kept scores. The UI shows the top three; the text and JSON summaries list every kept hint.
- Ties break in the fixed order `RefreshRetention`, `WriteReadTurnaround`, `RowBankTiming`, `ImcPowerDelivery`, `RankOrDimm`, `VoltageSignalIntegrity`, most context-specific first.
- Each hint carries an evidence line, for example "92 % of errors on bit 17 across 41 addresses".
- Every threshold is a named constant in `src/RamQuick.Analysis/AnalyzerThresholds.cs`; retuning a heuristic is an edit there only.

**Expected categories for the injected fault classes** (criterion 4):

| Injected fault class | Expected top-ranked category |
| --- | --- |
| Single-bit flips on a fixed lane at spread addresses | `VoltageSignalIntegrity` |
| Stuck bit at one address | `RankOrDimm` |
| Address-region fault | `RankOrDimm` |
| Delay-dependent fault | `RefreshRetention` |

**Limits.**

- Offsets are virtual, and even physically contiguous memory, such as a 2 MiB large page, is normally interleaved across channels, ranks and banks. An address cluster therefore indicates locality in the tested memory, not a specific rank or DIMM. It stays a weak locality hint: its evidence line reads "errors concentrated in n % of the tested memory (virtual offsets)", and its suggestion speaks of "one part of memory".
- With Stop on error on there is usually a single error, so the step-mechanism prior dominates the ranking.
- The heuristics are validated only against injected faults, not against real hardware faults, until the validation-PC results below exist.

**Hint label.** Every channel carries the label "Hint — not a diagnosis": the UI result panel heads the list "Probable cause (hint)" and prefixes each item with "Hint:", the text summary writes the label above the list, and the JSON summary writes it as `causeHints.label`. No wording anywhere states a cause as certain.

## Provenance

- Karhu RAM Test and TestMem5 are closed-source. They were studied only through public documentation, configuration files and published statistics.
- MemTest86 and HCI MemTest were studied through their public test descriptions and manual.
- No code, binaries or configuration files from any of them enter the repository. RAMQuick's patterns and orders are implemented from the public descriptions cited in [Sources](#sources).
- The tools are used as validation baselines only, never linked or bundled.

## Baseline results

### Procedure

**Run conditions.** For every run of every tool:

- Boot into the profile under test and log in.
- Close other applications and wait 2 minutes idle.
- Run one tool at a time.
- Record the room temperature once per session.

**Timing definitions.**

- **Baseline tools:** time to first error is the tool's own elapsed time when its first error appears, measured from the tool's start, to the nearest 0.1 min.
- **RAMQuick:** time to FAIL is `run.timeToVerdictSeconds`, from `run.startRequestedUtc` (Start accepted) to `run.verdictUtc` (FAIL shown in the result panel). It therefore includes preparation and the stop-on-error halt. "Reports FAIL within 5 minutes" means `timeToVerdictSeconds ≤ 300`. `run.firstErrorSeconds` is recorded alongside it for analysis, but it is not the acceptance measure.

**Censoring.**

- A baseline run with no error after 120 min is right-censored, recorded as "> 120" with Censored 1.
- A RAMQuick run that ends in PASS or ABORTED on the failing profile is right-censored at its `timeToVerdictSeconds`, recorded as "> t". It counts as a miss for the 5-minute requirement.
- The script never assumes an error record exists. It uses `run.verdict` and the `run.*` timing fields only.

**Median intervals.** Censoring times differ between runs: an ABORTED run censored at 5 s shows only that its failure time exceeds 5 s, not that it exceeds an observed 200 s. Censored values are therefore never ranked against observed ones. Each median is reported as an interval [L, U] that must contain the sample median of the true failure times:

- L is the median with every censored run set to its censoring time.
- U is the median with every censored run set to +∞, so U is +∞ whenever a censored run reaches the median position.
- With no censoring, L = U, the ordinary median. An even count averages the two middle values, at each bound separately.
- The Kaplan–Meier median is not used. With 3 to 10 runs and early censoring it is often undefined, and it would need an assumption of non-informative censoring that an ABORTED run does not satisfy.

**Decisions.** Each comparison is **met** when the bounds establish it, **not met** when the bounds establish its opposite, and **indeterminate** otherwise. Indeterminate never counts as met.

- The fastest baseline median B is the minimum over tools of their medians, so B ∈ [min L, min U] across tools.
- Speed ratio ≥ 5: met when min L(baseline) ≥ 5 × U(Quick); not met when min U(baseline) < 5 × L(Quick). The ratio interval is [min L(baseline) ÷ U(Quick), min U(baseline) ÷ L(Quick)].
- Standard or Exhaustive no later than Quick: met when U(type) ≤ L(Quick); not met when L(type) > U(Quick).
- An indeterminate result adds five more interleaved runs of each RAMQuick type, or three more runs of the deciding baseline tool, and the rule is re-applied once. A result still indeterminate is recorded as such, and the criterion is not marked met.

**Tables.** `scripts/Summarize-ValidationRuns.ps1` reads a folder of RAMQuick summaries and `.log` files, the baseline CSV the user writes (`tool,run,minutesToFirstError,censored`) and the HWiNFO64 CSV logs of the stress-bound measurement. Its output replaces the templates below and in [Validation results](#validation-results); its `-SelfTest` switch checks the censoring, interval and decision rules and that its headings and table headers appear in this document.

### Validation PC

| Item | Value |
| --- | --- |
| CPU | _pending_ |
| Motherboard and BIOS version | _pending_ |
| Memory kit (capacity, DIMMs, ranks, ICs if known) | _pending_ |
| Failing profile (speed, primary timings, VDD/VDDQ, SoC/IMC voltage) | _pending_ |
| Known-passing profile | _pending_ |
| JEDEC defaults (XMP/EXPO off) | _pending_ |
| Windows edition and build | _pending_ |
| RAMQuick version | _pending_ |
| PawnIO installed | _pending_ |
| Sensors that read (CPU, RAM) | _pending_ |
| Stress-bound logging tool | _pending_ |

### Sessions

| Session | Date | Profile | Room temperature (°C) | Runs |
| --- | --- | --- | --- | --- |
| _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |

### Baseline runs on the failing profile

At least 3 runs per tool, each with its default or named configuration.

| Tool | Configuration | Run | Minutes to first error | Censored |
| --- | --- | --- | --- | --- |
| Karhu RAM Test | Default settings | 1 | _pending_ | _pending_ |
| Karhu RAM Test | Default settings | 2 | _pending_ | _pending_ |
| Karhu RAM Test | Default settings | 3 | _pending_ | _pending_ |
| TestMem5 | `extreme@anta777` | 1 | _pending_ | _pending_ |
| TestMem5 | `extreme@anta777` | 2 | _pending_ | _pending_ |
| TestMem5 | `extreme@anta777` | 3 | _pending_ | _pending_ |
| User-nominated tool | _pending_ | 1 | _pending_ | _pending_ |
| User-nominated tool | _pending_ | 2 | _pending_ | _pending_ |
| User-nominated tool | _pending_ | 3 | _pending_ | _pending_ |

### Baseline medians

| Tool | Runs | Censored runs | Median interval [L, U] (min) |
| --- | --- | --- | --- |
| Karhu RAM Test | _pending_ | _pending_ | _pending_ |
| TestMem5 | _pending_ | _pending_ | _pending_ |
| User-nominated tool | _pending_ | _pending_ | _pending_ |

Fastest baseline median B: _pending_


## Validation results

Status values are `pending`, `met`, `not met` and `indeterminate`; the stress bound uses `satisfied`, `exceeded` or `unverified`. "Validation PC" means the user-supplied machine with reproducibly passing and failing BIOS profiles. "Automated" means the suites `scripts/build.ps1` runs on any Windows 10/11 x64 machine with at least 8 GB of RAM.

### Criteria summary

| # | Criterion | Threshold | Evidence | Status |
| --- | --- | --- | --- | --- |
| 1 | Fail-fast effectiveness | Quick reports FAIL on the failing profile within 300 s in ≥ 9 of 10 runs and is ≥ 5× faster than the median of the fastest baseline tool (Karhu, TestMem5 and any nominated tool, ≥ 3 runs each), even when a baseline fails in under 25 min; Standard and Exhaustive repeat both checks and fail no later than Quick | Summary JSONs (`run.timeToVerdictSeconds`, `run.firstErrorSeconds`, `run.verdict`) and the baseline CSV | pending |
| 2 | No false failures | Exhaustive PASS in 3 and Quick PASS in 10 consecutive runs on the known-passing profile and on JEDEC defaults; the known-passing profile also passes a long Karhu or TestMem5 run | Summary JSON verdicts; baseline tool screenshots or result text | pending |
| 3 | Detection correctness | 100 % of injected single-bit flips, stuck bits, address-region faults and delay-dependent faults detected, with correct address, bit position and step | `engine.tests` XML and VSTest TRX under `artifacts/test-results/` | pending |
| 4 | Cause hints | For each injected signature the top-ranked cause matches the expected category, and the UI, text and JSON output label it as a hint | TRX results under `artifacts/test-results/` | pending |
| 5 | Coverage accuracy | Reported coverage within 1 % of independently counted verified bytes; each type reaches its target on a clean run | TRX results; `coverage.targetReached` in clean-run summaries | pending |
| 6 | Duration accuracy | A clean run finishes within ±15 % of the stated duration; displayed time remaining within ±10 % of actual after the first step | `run.durationSeconds`; `.log` progress lines (`remaining_ms`) | pending |
| 7 | UI behaviour | Every live field shown and updated ≥ 1 per second; Windows and the UI stay responsive; Stop halts within 2 s; Stop on error on halts on the first error, off continues with a running error count | TRX results; stop-request and Completed timestamps in the `.log` | pending |
| 8 | Edge cases | Low free memory, non-elevated launch, missing sensors, thermal abort and crash recovery (process terminated mid-step, then relaunched) behave as specified | TRX results; result-panel text and the recovered summary JSON | pending |
| 9 | Methodology | This document exists, covers the tool and tuning research, and maps each step to its mechanism | `TestPlanCatalogTests`; review against the outline | pending |

### Criterion 1: fail-fast effectiveness

#### RAMQuick runs on the failing profile

Ten runs each of Quick, Standard and Exhaustive, all with Stop on error on, interleaved (Q, S, E, Q, S, E, …) so that all three types see the same conditions.

| Type | Run | Verdict | Time to verdict (s) | First error (s) | First-error step | Censored |
| --- | --- | --- | --- | --- | --- | --- |
| _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ |

#### Fail-fast checks

Both Quick checks always apply: at least 9 of 10 runs report FAIL within 300 s, and the speed ratio against the fastest baseline median is ≥ 5. A fast baseline does not waive the ratio: with a 10-minute baseline median, a 4-minute Quick median meets the 5-minute check but fails the 5× check. Standard and Exhaustive each repeat the same two checks and must also fail no later than Quick, decided from the median intervals.

| Type | Runs | FAIL within 300 s | Median interval [L, U] (s) | Ratio interval vs fastest baseline | Within 5 min (≥ 9 of 10) | At least 5× faster | No later than Quick |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Quick | _pending_ | _pending_ | _pending_ | _pending_ | pending | pending | — |
| Standard | _pending_ | _pending_ | _pending_ | _pending_ | pending | pending | pending |
| Exhaustive | _pending_ | _pending_ | _pending_ | _pending_ | pending | pending | pending |

Criterion 1 overall: pending

#### Hypotheses

| Hypothesis | Statement | Tested by | Status |
| --- | --- | --- | --- |
| H1 | Dense random traffic from the first second lets a 5-minute run catch a failing profile | Criterion 1: Quick reports FAIL within 300 s in ≥ 9 of 10 runs | pending |
| H2 | The step order of Quick exposes instability soonest | Criterion 1: first-error steps and times of every run; Standard and Exhaustive no later than Quick | pending |

If criterion 1 is missed, the `.log` of each run shows which step found the first error. Step order and budgets are then retuned in `src/RamQuick.Plans/TestPlans.cs` only, keeping the shared opening block and Quick's 300 s, the validation is repeated, and the change is recorded in the [Plan change log](#plan-change-log).

### Criterion 2: no false failures

| Profile | Test | Required | Observed | Status |
| --- | --- | --- | --- | --- |
| Known-passing | Exhaustive | 3 consecutive PASS | _pending_ | pending |
| Known-passing | Quick | 10 consecutive PASS | _pending_ | pending |
| JEDEC defaults | Exhaustive | 3 consecutive PASS | _pending_ | pending |
| JEDEC defaults | Quick | 10 consecutive PASS | _pending_ | pending |
| Known-passing | Long baseline run (Karhu to 10,000 % or three TestMem5 `extreme@anta777` cycles) | zero errors | _pending_ | pending |

JEDEC defaults means the BIOS optimised defaults with XMP/EXPO off.

### Criteria 3 and 4: detection and cause hints

| Criterion | Automated tests | Result files | Status |
| --- | --- | --- | --- |
| 3. Detection correctness | `test_fault_injection.cpp` (engine level), `FaultInjectionEndToEndTests` (through Interop) | `artifacts/test-results/engine.tests.xml` and the TRX files under `artifacts/test-results/` | pending |
| 4. Cause hints | `InjectedSignatureTests`, `FaultInjectionEndToEndTests`, `MainViewModelTests`, `ResultsLogTests` | TRX files under `artifacts/test-results/` | pending |

Every injection that had a verification opportunity must match exactly one reported record by word address, bit mask, step and pass, with no unmatched records. The expected top-ranked categories are listed under [Cause-hint heuristics and their limits](#cause-hint-heuristics-and-their-limits).

### Criteria 5 and 6: coverage and duration at full length

Clean runs on the known-passing profile and on JEDEC defaults. The automated scaled and `Long` tests (`test_coverage.cpp`, `test_step_runner.cpp`, `CoverageAndTimingTests`) back these criteria; the table records the full-length confirmation.

| Type | Planned duration (s) | Duration (s) | Deviation (%) | Within ±15 % | Coverage (%) | Target (%) | Target reached |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Quick | 300 | _pending_ | _pending_ | pending | _pending_ | 200 | pending |
| Standard | 1200 | _pending_ | _pending_ | pending | _pending_ | 800 | pending |
| Exhaustive | 3600 | _pending_ | _pending_ | pending | _pending_ | 2500 | pending |

#### Time remaining

Evaluated from the 10-second progress lines (`remaining_ms`) of each clean run's `.log`, after step 1 ends. Samples whose actual remaining time is ≥ 10 s are gated at ±10 %; samples under 10 s are reported only (see [Open deviations](#open-deviations)).

| Type | Gated samples (actual ≥ 10 s) | Within ±10 % | Worst gated deviation (%) | Ungated samples (< 10 s) | Worst ungated deviation (%) | Status |
| --- | --- | --- | --- | --- | --- | --- |
| Quick | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | pending |
| Standard | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | pending |
| Exhaustive | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | pending |

### Criterion 7: UI behaviour and stop latency

Stop is pressed five times; the stop-request to Completed interval is read from the `.log`, and ≤ 2 s is required.

| Trial | Stop point | Stop request (UTC) | Completed (UTC) | Latency (s) | Within 2 s |
| --- | --- | --- | --- | --- | --- |
| 1 | Mid-step | _pending_ | _pending_ | _pending_ | pending |
| 2 | Mid-step | _pending_ | _pending_ | _pending_ | pending |
| 3 | Mid-step | _pending_ | _pending_ | _pending_ | pending |
| 4 | During a hold | _pending_ | _pending_ | _pending_ | pending |
| 5 | During Preparing | _pending_ | _pending_ | _pending_ | pending |

#### Memory release time

Release time (`memory.releaseSeconds`) at the full region size is reported, not gated.

| Type | Tested bytes | Release (s) |
| --- | --- | --- |
| Quick | _pending_ | _pending_ |
| Standard | _pending_ | _pending_ |
| Exhaustive | _pending_ | _pending_ |

#### Spot checks

| Check | Observation | Status |
| --- | --- | --- |
| Every live field shows and updates at ≥ 1 Hz | _pending_ | pending |
| Stop on error on halts at the first error, with FAIL and the cause hints | _pending_ | pending |
| Stop on error off completes every step, with a running error count | _pending_ | pending |
| Windows stays responsive (Task Manager during Exhaustive) | _pending_ | pending |

### Criterion 8: edge cases

| Case | Automated evidence | Validation-PC check | Status |
| --- | --- | --- | --- |
| Low free memory | `RunControllerTests`: 1.5 GiB available plans 0.5 GiB, so Start is refused with the state unchanged; a lock shortfall ends Aborted | — | pending |
| Non-elevated launch | `RunControllerTests`: the prompt appears at launch and on Start, no transition occurs, and Yes restarts elevated | Launch `RamQuick.exe` without elevation and check the dialog | pending |
| Missing sensors, wholly or partly | `SensorMonitorTests`: both readings unavailable, CPU-only and RAM-only availability, a throwing source and a hung source | Record which readings show N/A and the notice text | pending |
| Thermal abort | `ThermalLimitsTests`, `RunControllerTests` with substitute sensors | Substitute sensors only: driving real hardware to 95 °C contradicts the stress constraint | pending |
| Crash recovery | `CrashDetectorTests`, `ProgressJournalTests`, `CrashRecoveryProcessTests` (kills `RamQuick.TestHost` mid-step) | End `RamQuick.exe` with `Stop-Process -Force` during step 2, relaunch, and check the FAIL message and the partial summary | pending |
| Real-sensor presence | `LibreHardwareMonitorSmokeTests` (`Requires=Hardware`; skipped without elevation, PawnIO and a sensor) | Check that the CPU and RAM temperatures read | pending |

### Criterion 9: methodology

| Check | Evidence | Status |
| --- | --- | --- |
| Every step in `src/RamQuick.Plans/TestPlans.cs` appears in the step-to-mechanism map with its attributes and primary mechanism, and every mechanism is mapped | `TestPlanCatalogTests` | pending |
| The headings and table headers that `scripts/Summarize-ValidationRuns.ps1` emits appear in this document | `Summarize-ValidationRuns.ps1 -SelfTest` | pending |
| The document covers the methodology outline: purpose and stress bound, reference tools, tuning practice, mechanisms, step map, per-type plans, coverage, cause hints, provenance, baseline and validation results, sources | Review of this document | pending |

### Stress-bound measurement

Status: pending

Allowed values: `satisfied` (every metric within the bound), `exceeded` (at least one metric above it) and `unverified` (a metric could not be logged; the missing metric is named).

Logging: HWiNFO64 at 1 s polling, with the same tool and sensors for the two references and the RAMQuick Exhaustive run: DRAM read and write bandwidth, CPU package power and the DIMM (SPD) temperature sensors. Another logger may supply a metric HWiNFO64 lacks, provided it samples both sides the same way. Tool, sensors and log files used: _pending_

| Metric | Statistic | llama.cpp inference | 7-Zip benchmark | Bound (larger reference) | RAMQuick Exhaustive | Within bound |
| --- | --- | --- | --- | --- | --- | --- |
| DRAM read+write bandwidth (GB/s) | Peak 10-s rolling mean | _pending_ | _pending_ | _pending_ | _pending_ | pending |
| CPU package power (W) | Peak 10-s rolling mean | _pending_ | _pending_ | _pending_ | _pending_ | pending |
| DIMM temperature (°C) | Peak sample | _pending_ | _pending_ | _pending_ | _pending_ | pending |

#### Per-step statistics

| ID | Step name | Bandwidth (GB/s) | Package power (W) | DIMM temperature (°C) | Within bound |
| --- | --- | --- | --- | --- | --- |
| _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | pending |

If any of the three metrics cannot be logged, the bound is recorded as `unverified`, naming the missing metric; it is never judged on the remaining metrics. A step that exceeds the bound is retuned in `src/RamQuick.Plans/TestPlans.cs` only and re-measured.

### Product constraint checks

| Check | Procedure | Result |
| --- | --- | --- |
| Portable and offline | Run the published folder on clean Windows 10 and Windows 11 x64 machines with no .NET or VC++ runtime installed and with networking disabled | _pending_ |
| No network activity, registry writes or stray file writes | A Sysinternals Process Monitor capture during a full Exhaustive run shows no TCP or UDP activity by `RamQuick.exe`, no registry writes and no file writes outside the data folder | _pending_ |
| Memory released | Task Manager shows committed memory back to its pre-run level after Released | _pending_ |

### Window layout review

Manual review that the fixed rows are unclipped and the result detail area scrolls. The expected layouts assume a 48-DIP taskbar and a non-client height N ≈ 39 DIPs; the regular layout needs a client height of at least 380 DIPs (340 for the fixed rows plus 40 of detail), and the compact one at least 280.

| Screen | Scaling | Expected layout | Fixed rows unclipped | Detail area scrolls | Notes |
| --- | --- | --- | --- | --- | --- |
| 1920 × 1080 | 100 % | Regular, opening at 560 × 680 | _pending_ | _pending_ | _pending_ |
| 1920 × 1080 | 150 % | Regular, opening at 560 × 672 | _pending_ | _pending_ | _pending_ |
| 1920 × 1080 | 200 % | Regular, opening at 560 × 492 | _pending_ | _pending_ | _pending_ |
| 1366 × 768 | 100 % | Regular, opening at 560 × 680 | _pending_ | _pending_ | _pending_ |
| 1366 × 768 | 150 % | Regular, opening at 560 × 464; the detail row scrolls | _pending_ | _pending_ | _pending_ |
| 1366 × 768 | 200 % | Compact, with `MinHeight` 319 | _pending_ | _pending_ | _pending_ |

### Open deviations

| Deviation | Value built | Status | If rejected |
| --- | --- | --- | --- |
| Time remaining in the final 10 seconds | ±10 % is gated on every 1 Hz sample whose actual remaining time is ≥ 10 s. Samples under 10 s are measured and reported but not gated, because the mm:ss display's 1-second resolution and normal pass-to-pass jitter make ±10 % unattainable there | Open, awaiting the user's acceptance | The display gains tenths of a second for the final 10 s, and the gate extends to every nonzero sample |

### Assumptions and known risks

| Assumption | Value built | Confirming evidence | Status |
| --- | --- | --- | --- |
| Platform and elevation | Windows 10 and 11 on x64. Testing needs an elevated process; the manifest is `asInvoker`, so a non-elevated launch is detected and a restart as Administrator is offered. `PlatformCheck` refuses a non-x64 OS architecture and OS builds below 14393 | `PlatformCheckTests` and the edge-case tests | pending |
| Engine | Native C++20 DLL with SSE2, AVX2 and AVX-512 kernels, cache-line flushes before every verification and re-read, and pinned workers. C++ exceptions become result codes at the boundary; SEH faults end the process and are reported at the next launch (source 1) | Kernel-equivalence and fault-injection tests | pending |
| UI framework | .NET 8 WPF; .NET 8 support ends 10 Nov 2026 (source 7). The target framework is the single `RamQuickTargetFramework` property | Retuned by setting that property to `net10.0-windows` and `global.json` to a .NET 10 SDK; only .NET 8 APIs are used | pending |
| Memory under test | `min(0.90 × available, available − 1 GiB)`, rounded down to workers × 2 MiB; Start refused below 1 GiB; locked; large pages only when `SeLockMemoryPrivilege` is already held | `MemorySizingTests`; Task Manager stays responsive during Exhaustive on the validation PC | pending |
| Durations | Quick 300 s, Standard 1200 s, Exhaustive 3600 s, each the sum of its step time budgets | `run.durationSeconds` within ±15 % on the 32 GB-class validation PC | pending |
| Coverage targets | Quick ≥ 200 %, Standard ≥ 800 %, Exhaustive ≥ 2500 %, each the sum of its step coverage budgets | `coverage.targetReached` in clean-run summaries | pending |
| Sensors | LibreHardwareMonitorLib 0.9.6, which uses the PawnIO driver rather than the Defender-flagged WinRing0 (sources 2 and 8). CPU package temperature, and RAM temperature as the maximum across DIMM sensors. Without a PawnIO driver the user has installed, the readings show N/A | Which sensors read on the validation PC; the missing-sensor tests | pending |
| Thermal thresholds | Abort on any single fresh 1 Hz sample of CPU ≥ 95 °C or RAM (SPD) ≥ 85 °C | `ThermalLimitsTests`; the risks below | pending |

Known thermal-threshold risks:

- AMD Ryzen 7000 and 9000 processors may sit at 95 °C by design under sustained all-core load, so the CPU limit may abort healthy runs on those parts.
- SPD-hub readings run below the DRAM die and module temperature (about 12 °C below the module surface in Igor's Lab measurements), so the RAM limit may trip late.
- Retuning means editing the constants in `src/RamQuick.Sensors/ThermalLimits.cs` only.

### Plan change log

| Date | Change in TestPlans.cs | Reason and evidence | Validation repeated |
| --- | --- | --- | --- |
| — | No changes yet | — | — |

## Sources

1. /EH (exception handling model), Microsoft Learn: https://learn.microsoft.com/en-us/cpp/build/reference/eh-exception-handling-model
2. Microsoft Defender antimalware definition release notes 1.423.270.0 (WinRing0 detection): https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes?requestVersion=1.423.270.0
3. TestMem5 `extreme@anta777` configuration, integralfx MemTestHelper: https://github.com/integralfx/MemTestHelper/blob/oc-guide/TM5-Configs/extreme%40anta777.cfg
4. HCI MemTest manual: https://hcidesign.com/memtest/manual.html
5. MemTest86 individual test descriptions: https://www.memtest86.com/tech_individual-test-descr.html (archived copy: https://web.archive.org/web/20261002110228/https://www.memtest86.com/tech_individual-test-descr.html)
6. Karhu RAM Test, Karhu Software: https://www.karhusoftware.com/
7. .NET and .NET Core support policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
8. LibreHardwareMonitor discussion 1904, PawnIO in place of WinRing0: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/discussions/1904
9. TestMem5 to OCCT conversion guide: https://ocbase.com/resources/TM5-to-OCCT-conversion-guide.pdf
10. integralfx DDR4 OC Guide: https://github.com/integralfx/MemTestHelper/blob/oc-guide/DDR4%20OC%20Guide.md
11. goraxyz DDR5 OC Guide: https://github.com/goraxyz/DDR5-OC/blob/main/OC%20Guide.md
12. AMD Ryzen Master user guide, RAM: https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/RAM
13. ASUS ROG forum, DRAM temperatures and stability: https://rog-forum.asus.com/t5/push-the-limits/dram-temperatures-and-stability/bc-p/1150839
14. Igor's Lab, how hot DDR5 memory really is (SPD reading against module measurement): https://www.igorslab.de/en/how-hot-is-ddr5-memory-really-reading-value-from-spd-stroke-vs-real-measurement-at-module-investigative/2/ (archived copy: https://web.archive.org/web/20261011044454/https://www.igorslab.de/en/how-hot-is-ddr5-memory-really-reading-value-from-spd-stroke-vs-real-measurement-at-module-investigative/2/)
15. JESD79-5 DDR5 SDRAM specification: https://faculty-web.msoe.edu/johnsontimoj/ELE4142/files4142/DDR5_Spec.pdf
16. SetProcessWorkingSetSizeEx, Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsizeex
17. Large-page support, Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/memory/large-page-support
18. Processor groups, Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/procthread/processor-groups
19. /arch (x64), Microsoft Learn: https://learn.microsoft.com/en-us/cpp/build/reference/arch-x64
20. Intel 64 and IA-32 Architectures Software Developer's Manual, combined volumes: https://cdrdv2-public.intel.com/874240/325462-090-sdm-vol-1-2abcd-3abcd-4.pdf
21. 12th Generation Intel Core processors datasheet, volume 1 of 2, data scrambling: https://edc.intel.com/content/www/ca/fr/design/ipla/software-development-platforms/client/platforms/alder-lake-desktop/12th-generation-intel-core-processors-datasheet-volume-1-of-2/009/009/data-scrambling/

Sources 5 and 14 refuse automated retrieval: on 30 October 2026 both returned HTTP 403 to HEAD and GET requests from a command-line client. MemTest86 returned the page when the request carried a full browser user agent, while Igor's Lab still answered HTTP 403 with a Cloudflare challenge. A browser may still open them. Their Internet Archive copies, listed with them, returned HTTP 200 on the same date and carry the content cited here: MemTest86's descriptions of tests 0–14, and Igor's Lab's 12-degree difference between the SPD-hub reading and the module hotspot.

Cited by name, without a URL:

- Intel Core Ultra 200S series processors datasheet, cited for VDD2 on Intel platforms.
- TestMem5 `1usmus_v3` configuration.
