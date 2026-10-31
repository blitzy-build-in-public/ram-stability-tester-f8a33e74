# RAMQuick

RAMQuick is a portable, offline memory stability checker for Windows 10 and Windows 11 on x64 PCs. It judges whether the memory configuration currently set in the BIOS (speed, timings and voltages) is stable enough for daily use. It reports PASS, FAIL or ABORTED within minutes, with ranked, clearly labelled hints at the probable cause. It is meant for the tuning loop: change one setting, boot, run Quick, read the result, adjust.

RAMQuick does not force failures, search for the exact failure point, auto-tune or run row-hammer-style tests. It never changes BIOS, driver or system settings, and it does not read or write memory timings or voltages.

## Test types

| Type | Duration | Coverage target | Steps |
|------|----------|-----------------|-------|
| Quick | about 5 min (300 s) | ≥ 200 % | 6 |
| Standard | about 20 min (1200 s) | ≥ 800 % | 12 |
| Exhaustive | about 60 min (3600 s) | ≥ 2500 % | 19 |

- Standard and Exhaustive begin with Quick's six steps, unchanged, and then add longer steps, such as moving inversion over random data, a checkerboard, longer refresh holds, a half-load contrast step and a heat soak.
- **Coverage** means bytes verified divided by bytes under test. 100 % means every byte of the tested region was tested once, and 1000 % means every byte was tested ten times.
- The durations are for a 32 GB-class PC. On a very large or slow machine, a single pass of a step can take longer than that step's time budget. The step then runs exactly one pass, the run takes longer than stated, and the overrun is recorded in the log and the summary.
- Steps are ordered to fail fast: the steps expected to expose a marginal setting soonest run first. Time budgets govern each step, and the coverage targets are floors that never lengthen a step. [docs/METHODOLOGY.md](docs/METHODOLOGY.md) explains each step and the instability it targets.

## Running RAMQuick

1. Extract `RamQuick-win-x64.zip`, or copy the `RamQuick-win-x64` folder, to any location. Besides the application, the folder holds this README, `THIRD-PARTY-NOTICES.txt` and [docs/METHODOLOGY.md](docs/METHODOLOGY.md).
2. Close other applications, then run `RamQuick.exe` from that folder.

Nothing is installed. No .NET or Visual C++ runtime is needed, and RAMQuick needs no network access. Only one instance runs at a time; a second one shows "RAMQuick is already running" and exits.

The single window has three controls:

- **Test type**: Quick (default), Standard or Exhaustive.
- **Stop on error** (checked by default): stop at the first confirmed error. When unchecked, the test runs every step and counts all errors.
- **Start / Stop**: one button that starts a test and becomes Stop while it runs. Stop takes effect within 2 seconds.

While a test runs, the window shows RAM and CPU temperature, the current step ("name (n of N)"), elapsed time, time remaining ("calculating…" until the first step has completed a pass), coverage against the type's target, and the running error count. The detected-memory summary shows total memory, tested capacity, speed, channel count and DIMM count; speed and channels read "Unknown" when the firmware does not report them.

RAMQuick tests up to 90 % of the memory that is free when you press Start, always leaving at least 1 GiB free. The test region is locked in memory and split evenly across one test thread per logical processor. If less than 1 GiB can be tested, Start is refused with "Not enough free memory to test: at least 1 GB must be lockable. Close other applications and try again." If locking fails at the planned size, RAMQuick retries 10 % smaller, down to 1 GiB, and reports the size it actually tested; below 1 GiB the test ends as ABORTED with the same message.

After the verdict, Start stays unavailable until the tested memory is released and the results are saved; meanwhile the current step reads "Releasing memory…" and then "Saving results…". The 2-second bound for Stop ends at the verdict; releasing the memory comes after it, is not covered by that bound and grows with the tested size, so it takes longest on PCs with a lot of memory. If the release is not confirmed within 30 seconds of the verdict, the results are saved anyway with the release time recorded as not confirmed (`memory.releaseSeconds: null` in the JSON summary, "memory release not confirmed" in the `.log`). If it is still not confirmed once they are saved, the current step reads "Releasing memory…" again and the notice line reads "Memory release not confirmed yet. Start becomes available once it is; if it never is, restart RAMQuick."

Closing the window during a test stops the test; the results are saved as ABORTED before RAMQuick exits.

## Administrator rights

RAMQuick starts with the rights of the user who launched it (its manifest requests `asInvoker`). Locking memory for testing and reading temperature sensors need Administrator rights, so a non-elevated launch shows this dialog:

> RAMQuick needs Administrator rights to lock memory for testing and to read temperature sensors. Restart as Administrator?

- **Yes** restarts RAMQuick through the Windows UAC prompt.
- **No**, Esc, closing the dialog, or declining the UAC prompt keeps the window open without elevation. The notice line then reads "Not running as Administrator — press Start to restart as Administrator", and pressing Start shows the same dialog instead of starting a test.

Elevation happens only through UAC consent. RAMQuick never grants a privilege to any account. It uses large pages only when the "Lock pages in memory" privilege (`SeLockMemoryPrivilege`) is already held, which Windows does not grant by default; otherwise it tests standard 4 KiB pages locked in memory. The summary records which page mode was used.

## Temperature sensors and PawnIO

CPU and RAM temperatures are read with LibreHardwareMonitorLib 0.9.6, which needs the PawnIO driver to be already installed. RAMQuick never installs, bundles or prompts you to install PawnIO or any other driver.

- With Administrator rights but without PawnIO, both readings show "N/A". When the memory modules have no thermal sensor, only the RAM reading shows "N/A". Each missing reading leaves only its own thermal limit inactive. The test still runs, and the notice line names the inactive limit: "CPU temperature unavailable — CPU thermal limit inactive", "RAM temperature unavailable — RAM thermal limit inactive", or "Temperature sensors unavailable — thermal safety inactive" when both are missing.
- Without Administrator rights, both readings also show "N/A", because reading the sensors needs elevation too, and no test runs: pressing Start shows the restart dialog described under Administrator rights above.
- The RAM temperature is the highest reading across the DIMMs' thermal sensors, which on DDR5 sit in the SPD hub.
- **Thermal safety:** a test stops as ABORTED when a fresh reading reaches 95 °C for the CPU or 85 °C for the RAM, for example "Stopped for safety: CPU temperature reached 95 °C".

SPD-hub readings run below the DRAM die temperature, so the RAM limit trips late rather than early. Ryzen 7000/9000 CPUs may sit at 95 °C by design under all-core load, so the CPU limit can stop a test on those parts.

## Reading the results

| Result | Meaning |
|--------|---------|
| PASS | Every step completed with zero errors |
| FAIL | At least one error was confirmed, or the previous test did not finish |
| ABORTED | The test stopped before a verdict: you pressed Stop, a thermal limit was reached, memory could not be locked or test threads could not be pinned, the progress journal could not be written, or the engine reported an error. Errors found before the stop are still shown, as in "3 errors were detected before the test stopped" |

**Error details.** The first error is shown with its step name, time into the test, error count, address, and the expected, actual and re-read values, for example:

```text
Step 'Random bank thrash' at 01:12 — 3 errors — virtual address 0x0000023A4F7C1D48 — expected 0x5A3C…, actual 0x5A3E…, re-read 0x5A3E… (repeated wrong value: likely write or storage fault)
```

- Addresses are **virtual addresses**. Windows offers no documented way for an application to obtain physical addresses, so they are labelled as virtual everywhere.
- Every mismatch is re-read once after flushing it from the CPU cache. The re-read classification is an inference, never an established fault location: a re-read that matches the expected value suggests a "likely read-path fault", one that repeats the wrong value a "likely write or storage fault", and one that differs from both a "likely transient fault".
- With Stop on error off, errors are grouped by 8-byte word address and the 100 largest groups are listed. At most the first 10,000 errors are stored in detail; the true total is always counted and shown. Once that cap is reached, the list header reads "Grouped from the first 10,000 errors".

**Cause hints.** When errors were found, the window lists the three highest-ranked probable causes, each with a likelihood and a plain-language suggestion, such as loosening a named timing or adding a little voltage. The hints say which instability the errors most resemble; they are not a diagnosis. The window heads them "Probable cause (hint)" and prefixes each with "Hint:", and the text and JSON summaries carry the label "Hint — not a diagnosis".

**Unfinished previous test.** If a test never reached its verdict, because Windows hung, the PC rebooted or the process was killed, the next launch shows FAIL with:

> The previous test did not finish at step n of N (name), which likely indicates severe instability.

A partial summary of that test is saved. If the process ended after the verdict but before the results were saved, they are rebuilt from the progress journal and the notice reads "Results of the previous test were recovered and saved."

## Data and log files

RAMQuick stores its files in a data folder, `<data>`:

- `<app folder>\data`, the `data` folder beside `RamQuick.exe`, when it is writable;
- otherwise `%LOCALAPPDATA%\RamQuick`, for example when the portable folder sits on read-only media.

If neither is writable, Start is refused with "RAMQuick cannot save its progress journal and logs. Move it to a writable folder and try again."

| File | Content |
|------|---------|
| `<data>\journal\active-run.jsonl` | Progress journal, written durably throughout a test and deleted once its results are saved |
| `<data>\logs\RamQuick-<yyyyMMdd-HHmmss>-<TestType>-<runId>.log` | Timestamped event log of one test: configuration, steps, progress every 10 s, errors, temperatures, journal and callback failures, stop and verdict |
| `<data>\logs\RamQuick-<yyyyMMdd-HHmmss>-<TestType>-<runId>-summary.json` | Machine-readable summary (`schemaVersion: 1`): configuration, steps, coverage, temperatures, errors, cause hints and verdict |
| `<data>\logs\RamQuick-<yyyyMMdd-HHmmss>-<TestType>-<runId>-summary.txt` | The same summary as readable text |
| `<data>\logs\…-recovered-summary.json`, `…-recovered-summary.txt` | A summary rebuilt from the journal, used when a file under the original name already exists but cannot be read or belongs to another test |
| `<data>\logs\app-errors.log` | Unexpected application errors, such as exceptions raised while handling engine events: one `APP_ERROR` header per error, with the test's `run_id` while a test is running, followed by the exception text and stack trace, each line indented by two spaces |

The timestamp is the test's local start time, and `<runId>` is a 32-digit hexadecimal run identifier.

The JSON and text summaries are the files meant for sharing, for example to compare attempts: they hold memory-test data and hardware descriptors. The `.log` and `app-errors.log` are local diagnostic logs that can also hold exception messages and stack traces. In them, the data folder is written `<data>`, your folders `%LOCALAPPDATA%`, `%APPDATA%` and `%USERPROFILE%`, and your Windows user name, where it is part of a path, `%USERNAME%`. Inspect them before you share them. Nothing leaves the PC.

**Retention and erasure.** Results and logs do not expire. RAMQuick never deletes, rotates or expires the logs and summaries it saves in `<data>\logs`: they accumulate, one set per test, until you remove them. To erase the test data stored on this PC, close RAMQuick and delete its data folder; RAMQuick creates a new, empty one at the next launch. If RAMQuick has run from more than one place, check both locations: `<app folder>\data` beside each copy you ran, and `%LOCALAPPDATA%\RamQuick`. Copies you saved or shared elsewhere are not affected; delete them separately.

## Privacy and safety

- No telemetry, no update checks and no network activity initiated by RAMQuick.
- No registry writes and no driver or service installation; privileges are enabled only when the account already holds them.
- Locked memory is released on every exit path: completion, Stop, errors, thermal stops and engine faults. If the process is terminated, Windows reclaims the memory.
- The test is sized to leave at least 10 % of the available memory free, and never less than 1 GiB. Locking the test memory costs Windows a little memory of its own, so free memory during a test can sit slightly below that share. Test threads run below normal priority, so Windows stays usable.
- The test is designed to stay within the stress of heavy daily workloads: it changes no voltage, frequency or timing and never loops over a small set of addresses. [docs/METHODOLOGY.md](docs/METHODOLOGY.md) defines that bound and how it is measured.

## Supported systems

RAMQuick supports Windows 10 and Windows 11 on x64 PCs only. At startup it refuses to run, with the message "RAMQuick supports Windows 10 and Windows 11 on x64 PCs only, so it cannot run on this system.", when:

- the operating system is not x64, which includes x64 emulation on Windows on Arm, where the OS reports Arm64; or
- the OS build is below 14393 (Windows 10 version 1607).

RAMQuick runs on .NET 8. The [.NET 8 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/8.0/supported-os.md) lists:

| Windows | Versions |
|---------|----------|
| Windows 11 | 23H2 (E), 24H2, 24H2 (E), 24H2 (IoT), 25H2, 26H1 |
| Windows 10 | 21H2 (E), 21H2 (IoT), 1809 (E), 1607 (E) |

Other Windows 10 builds from 14393 onward can run RAMQuick but sit outside that matrix.

A CPU without AVX2 runs the SSE2 code path; the notice line reads "Reduced instruction set (SSE2): this test runs slower".

**.NET 8 support ends on 10 November 2026.** Retargeting is done in the RAMQuick source checkout; the files linked in this paragraph and the next are part of the checkout, not of the portable folder. To retarget, change `RamQuickTargetFramework` in [Directory.Build.props](Directory.Build.props) to `net10.0-windows` and the SDK in [global.json](global.json) to a .NET 10 SDK. No source change is expected, because only .NET 8 APIs are used.

A .NET 10 SDK also needs a newer toolchain. From the 10.0.2xx feature band on, .NET 10 SDKs require MSBuild 18.0 or later (Visual Studio 2026); the MSBuild of Visual Studio 2022 17.14 refuses to load them ("requires at least version 18.0.0 of MSBuild"), and Microsoft does not support targeting `net10.0` from 17.14 even with a 10.0.1xx SDK ([.NET SDK, MSBuild, and Visual Studio versioning](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs)). The scripts select only a 17.14 installation (`$VisualStudioVersionRange` is `[17.14,18.0)` in [scripts/build.ps1](scripts/build.ps1) and [scripts/publish-portable.ps1](scripts/publish-portable.ps1)), so building the retargeted solution also needs Visual Studio 2026 Build Tools, with the v143 toolset that [src/engine/engine.props](src/engine/engine.props) pins, and that range raised. Visual Studio 2022 17.14 stays the pinned toolchain for .NET 8.

## Building from source

This section applies to the RAMQuick source checkout. The scripts, project and configuration files it links to are part of the checkout and are not in the portable folder.

### Prerequisites

| Tool | Version |
|------|---------|
| Visual Studio 2022 Build Tools (MSBuild 17.14) | 17.14.42, with workloads `VCTools` and `ManagedDesktopBuildTools` and component `Vcpkg`; `scripts\build.ps1` also needs component `TestTools.BuildTools` for `vstest.console.exe`. The scripts select a 17.14 installation, never Visual Studio 2026 (18.x) |
| MSVC toolset | v143 14.44.35211 |
| Windows SDK | 10.0.26100.0 |
| .NET SDK | 8.0.427, pinned in [global.json](global.json) with `rollForward: latestPatch` |
| clang-format | The build bundled with Visual Studio 2022 17.14.42 (component `VC.Llvm.Clang`) |
| Windows PowerShell | 5.1 |

GoogleTest 1.18.0 is restored automatically in vcpkg manifest mode from [vcpkg.json](vcpkg.json), for the `x64-windows-static` triplet so it matches the engine's static C runtime. NuGet package versions are pinned in [Directory.Packages.props](Directory.Packages.props) and frozen by each project's `packages.lock.json`.

The native test projects rely on vcpkg's user-wide MSBuild integration and import no vcpkg file themselves. Without it they cannot restore GoogleTest or find its headers and libraries, so the build fails. Enable it once per Windows user with the vcpkg bundled with Visual Studio. The first run needs Administrator rights, so run it in Windows PowerShell started with **Run as administrator** under the account that builds RAMQuick:

```powershell
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -version '[17.14,18.0)' -requires Microsoft.VisualStudio.Component.Vcpkg -property installationPath
& "$vs\VC\vcpkg\vcpkg.exe" integrate install
```

In a Developer PowerShell for VS 2022, `vcpkg integrate install` does the same. See [vcpkg in MSBuild projects](https://learn.microsoft.com/en-us/vcpkg/users/buildsystems/msbuild-integration).

### Build

One command builds the solution and runs every check:

```powershell
.\scripts\build.ps1
```

[scripts/build.ps1](scripts/build.ps1) runs, in order: the constraint scan ([scripts/check-constraints.ps1](scripts/check-constraints.ps1)), the `clang-format` style check of the native sources, the self-test of [scripts/Summarize-ValidationRuns.ps1](scripts/Summarize-ValidationRuns.ps1), the Release build, the third-party notices check, the native tests and the six managed test suites. It closes with a summary table of every step.

- `-Coverage` also collects line coverage and fails below 90 % for `RamQuick.Core`, `RamQuick.Plans` and `RamQuick.Analysis`, and below 80 % for `RamQuick.Logging`, `RamQuick.Interop`, `RamQuick.Sensors` and the app's services and view models.
- `-Configuration Debug` builds and tests the Debug configuration, and `-IncludeLong` adds the full-length tests. See `Get-Help .\scripts\build.ps1 -Full` for details.

The build step restores NuGet packages in locked mode, so it fails with `NU1004` when a committed `packages.lock.json` is out of date, instead of rewriting it.

To build without the checks, use Visual Studio's MSBuild, for example from a Developer PowerShell for VS 2022:

```powershell
msbuild RamQuick.sln -restore -p:Configuration=Release -p:Platform=x64
```

This command restores without locked mode and updates the lock files to match the pins. After deliberately changing a version in [Directory.Packages.props](Directory.Packages.props) or a package reference in a project, run it to refresh the lock files and commit them with the change.

`dotnet build` cannot build the C++ projects in the solution, so always build with Visual Studio's MSBuild.

### Tests

- **Native:** GoogleTest tests in `engine.tests`, built to `artifacts\bin\native\<Configuration>\engine.tests.exe`. They link the engine core directly and replace the memory-access layer and the clock with test substitutes, including a fault-injecting layer.
- **Managed:** xUnit v3 tests in the six managed test projects under `tests\`.
- Results are written under `artifacts/test-results/`: `engine.tests.xml` and one `.trx` per managed test project.

Some tests run only where their preconditions hold:

- Tests in the `Long` category run the full-length Quick, Standard and Exhaustive plans on a 1 GiB region and take about 85 minutes. `scripts\build.ps1` excludes them unless `-IncludeLong` is given.
- The `Requires=Hardware` sensor smoke test needs an elevated process, an installed PawnIO driver and a real sensor, and is skipped otherwise.
- The large-page test is skipped without `SeLockMemoryPrivilege`.
- The AVX2 and AVX-512 kernel-equivalence tests are skipped on CPUs that lack those instructions.

### Publish the portable folder

```powershell
.\scripts\publish-portable.ps1
```

[scripts/publish-portable.ps1](scripts/publish-portable.ps1) locates MSBuild with `vswhere` and publishes `src\RamQuick.App` with the `Portable` profile ([Portable.pubxml](src/RamQuick.App/Properties/PublishProfiles/Portable.pubxml)): self-contained `win-x64` with ReadyToRun, neither trimmed nor single-file. It verifies that the folder holds no test-only files and that the engine needs no Visual C++ runtime DLL, copies `THIRD-PARTY-NOTICES.txt`, this README and `docs/METHODOLOGY.md` into it at their repository-relative paths, and zips it:

- `artifacts/publish/RamQuick-win-x64/`: the portable folder;
- `artifacts/publish/RamQuick-win-x64.zip`: the same folder, zipped.

### Repository layout

```text
RamQuick.sln                  one solution for the native and managed projects
docs/                         METHODOLOGY.md: research, step plans, validation procedure and results
scripts/                      build, publish, constraint scan, validation-run summary
src/
  engine/                     native C++ test engine; public C API in include/ramquick_engine.h
  RamQuick.Core/              run state machine and shared records
  RamQuick.Interop/           managed wrapper over the engine API
  RamQuick.Analysis/          ErrorAnalyzer and its cause-signature rules
  RamQuick.Plans/             Quick, Standard and Exhaustive step definitions
  RamQuick.Sensors/           temperature monitor, thermal limits, SMBIOS memory inventory
  RamQuick.Logging/           progress journal, crash detection, results log and summaries
  RamQuick.App/               WPF application: window, view model, run controller
tests/
  engine.tests/               GoogleTest native tests; test_dll/ builds ramquick_engine_test.dll, which never ships
  RamQuick.Analysis.Tests/    cause-signature tests
  RamQuick.Plans.Tests/       plan catalog and methodology-map tests
  RamQuick.Sensors.Tests/     sensor monitor, thermal limit and SMBIOS tests
  RamQuick.Logging.Tests/     journal, recovery and results-log tests
  RamQuick.App.Tests/         view model, run state, controller and platform tests
  RamQuick.IntegrationTests/  wrapper, fault injection, coverage, timing and crash-recovery tests
  RamQuick.TestHost/          headless run host for process-level tests
```

### Validation on hardware

Fail-fast speed and the absence of false failures can only be shown on a real PC with known passing and failing BIOS profiles. [docs/METHODOLOGY.md](docs/METHODOLOGY.md) holds the procedure, including the baseline runs of other tools, and the baseline and validation result tables. [scripts/Summarize-ValidationRuns.ps1](scripts/Summarize-ValidationRuns.ps1) reads the RAMQuick summaries and logs, a baseline CSV and the sensor logs of the stress-bound measurement, and emits the filled-in tables; see `Get-Help .\scripts\Summarize-ValidationRuns.ps1 -Full`.

## Methodology and provenance

[docs/METHODOLOGY.md](docs/METHODOLOGY.md) records the study of the reference tools and of DRAM tuning practice, maps every step to the instability mechanism it targets, and states the limits of the cause hints. Karhu RAM Test, TestMem5, MemTest86 and HCI MemTest were studied for methodology only, through their public documentation, configurations and published statistics. No code, binaries or configuration files from any of them are included.

## Third-party software

RAMQuick redistributes unmodified third-party binaries. Their licences and source locations are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
