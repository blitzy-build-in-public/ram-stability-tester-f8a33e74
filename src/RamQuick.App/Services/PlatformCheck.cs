using System.Runtime.InteropServices;

namespace RamQuick.App.Services;

/// <summary>
/// Why <see cref="PlatformCheck"/> refused the platform, or <see cref="None"/> when it is supported.
/// </summary>
public enum PlatformRejection
{
    /// <summary>
    /// The platform is supported: an x64 operating system at build <see cref="PlatformCheck.MinimumBuild"/>
    /// or later.
    /// </summary>
    None = 0,

    /// <summary>
    /// The operating system is not x64. This includes Windows on Arm running RAMQuick under x64
    /// emulation, where the operating system architecture is Arm64.
    /// </summary>
    NotX64 = 1,

    /// <summary>
    /// The operating system build is below <see cref="PlatformCheck.MinimumBuild"/>, or is not defined.
    /// </summary>
    BuildTooOld = 2,
}

/// <summary>
/// The outcome of a platform check. <see cref="IsSupported"/> is <see langword="true"/> exactly when
/// <see cref="Rejection"/> is <see cref="PlatformRejection.None"/>; the constructor enforces this.
/// </summary>
/// <param name="IsSupported">Whether RAMQuick may run on the platform.</param>
/// <param name="Rejection">
/// The reason the platform was refused, or <see cref="PlatformRejection.None"/> when it is supported.
/// </param>
/// <exception cref="ArgumentException">
/// <paramref name="IsSupported"/> does not agree with <paramref name="Rejection"/>.
/// </exception>
/// <exception cref="ArgumentOutOfRangeException">
/// <paramref name="Rejection"/> is not a defined <see cref="PlatformRejection"/> value.
/// </exception>
public sealed record PlatformCheckResult(bool IsSupported, PlatformRejection Rejection)
{
    /// <summary>
    /// The result for a supported platform.
    /// </summary>
    public static PlatformCheckResult Supported { get; } = new(true, PlatformRejection.None);

    // Both properties are get-only, so a with-expression cannot pair a verdict with a reason that
    // contradicts it; the validation below runs before either property is assigned.

    /// <summary>
    /// Whether RAMQuick may run on the platform.
    /// </summary>
    public bool IsSupported { get; } = RequireConsistent(IsSupported, Rejection);

    /// <summary>
    /// The reason the platform was refused, or <see cref="PlatformRejection.None"/> when it is supported.
    /// </summary>
    public PlatformRejection Rejection { get; } = Rejection;

    private static bool RequireConsistent(bool isSupported, PlatformRejection rejection)
    {
        bool refused = rejection switch
        {
            PlatformRejection.None => false,
            PlatformRejection.NotX64 or PlatformRejection.BuildTooOld => true,
            _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection, "Undefined platform rejection."),
        };

        if (isSupported == refused)
        {
            throw new ArgumentException(
                "A supported result must have no rejection, and a refused result must name one.",
                nameof(isSupported));
        }

        return isSupported;
    }
}

/// <summary>
/// The platform support policy, enforced once at startup: RAMQuick runs on Windows 10 and Windows 11
/// on x64 PCs only.
/// </summary>
/// <remarks>
/// <para>
/// Neither the application manifest nor the <c>win-x64</c> runtime identifier enforces this. The
/// manifest's single Windows 10/11 <c>supportedOS</c> entry only declares compatibility, and the
/// runtime identifier only selects assets. This check is the one place the policy is applied.
/// </para>
/// <para>
/// The minimum build is the earliest Windows 10 build in the .NET 8 supported-OS matrix. That matrix
/// lists Windows 10 only for Enterprise, LTSC and IoT editions, so other Windows 10 editions at or
/// above the minimum build run RAMQuick outside it. <c>README.md</c> states this; it is not enforced.
/// </para>
/// <para>
/// The check is pure and stateless: it performs no I/O, shows nothing and logs nothing. On a refusal
/// the application host shows the unsupported-platform message and exits.
/// </para>
/// </remarks>
public static class PlatformCheck
{
    /// <summary>
    /// The earliest supported operating system build: 14393, Windows 10 version 1607. Windows 10 and
    /// Windows 11 both report major version 10, so only the build number is compared; Windows 11
    /// builds start at 22000.
    /// </summary>
    public const int MinimumBuild = 14393;

    /// <summary>
    /// Applies the support policy to an operating system architecture and version.
    /// </summary>
    /// <param name="osArchitecture">
    /// The operating system architecture, with <see cref="RuntimeInformation.OSArchitecture"/>
    /// semantics, never the process architecture: an x64 process emulated on Windows on Arm still
    /// reports <see cref="Architecture.Arm64"/> and is refused.
    /// </param>
    /// <param name="osVersion">
    /// The operating system version. Only <see cref="Version.Build"/> is compared; an undefined build
    /// (-1) is treated as below the minimum.
    /// </param>
    /// <returns>
    /// <see cref="PlatformRejection.NotX64"/> when the architecture is not
    /// <see cref="Architecture.X64"/>, whatever the build, since architecture is checked first;
    /// otherwise <see cref="PlatformRejection.BuildTooOld"/> when the build is below
    /// <see cref="MinimumBuild"/>; otherwise <see cref="PlatformCheckResult.Supported"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="osVersion"/> is <see langword="null"/>.</exception>
    public static PlatformCheckResult Evaluate(Architecture osArchitecture, Version osVersion)
    {
        ArgumentNullException.ThrowIfNull(osVersion);

        if (osArchitecture != Architecture.X64)
        {
            return new PlatformCheckResult(false, PlatformRejection.NotX64);
        }

        if (osVersion.Build < MinimumBuild)
        {
            return new PlatformCheckResult(false, PlatformRejection.BuildTooOld);
        }

        return PlatformCheckResult.Supported;
    }

    /// <summary>
    /// Applies the support policy to the operating system this process runs on.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.OSVersion"/> reports the true operating system version on .NET 5 and
    /// later, independent of manifest compatibility shims.
    /// </remarks>
    /// <returns>The result of <see cref="Evaluate"/> for the current operating system.</returns>
    public static PlatformCheckResult EvaluateCurrent() =>
        Evaluate(RuntimeInformation.OSArchitecture, Environment.OSVersion.Version);
}
