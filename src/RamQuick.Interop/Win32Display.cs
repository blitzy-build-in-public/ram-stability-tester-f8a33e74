using System.Runtime.InteropServices;

namespace RamQuick.Interop;

/// <summary>
/// The work area of one display monitor: its bounds less the taskbar and any docked application bars.
/// </summary>
/// <remarks>
/// Coordinates are physical pixels in virtual-screen coordinates, so a monitor left of or above the primary
/// monitor has negative <see cref="Left"/> or <see cref="Top"/>. They are physical only for a per-monitor
/// DPI-aware caller, as RAMQuick's manifest declares; Windows scales them for a DPI-unaware one.
/// Converting them to device-independent units with the window's DPI is the caller's job.
/// </remarks>
/// <param name="Monitor">
/// The monitor's <c>HMONITOR</c>. Results with the same handle describe the same monitor, so comparing it after
/// the window moves detects a move to another monitor.
/// </param>
/// <param name="Left">Left edge of the work area, in physical pixels.</param>
/// <param name="Top">Top edge of the work area, in physical pixels.</param>
/// <param name="Width">Width of the work area, in physical pixels.</param>
/// <param name="Height">Height of the work area, in physical pixels.</param>
public readonly record struct MonitorWorkArea(nint Monitor, int Left, int Top, int Width, int Height);

/// <summary>
/// Queries the work area of the monitor that holds a window. Window sizing uses it rather than
/// <c>SystemParameters.WorkArea</c>, which describes only the primary monitor.
/// </summary>
public static partial class Win32Display
{
    // MONITOR_DEFAULTTONEAREST: a window that lies off every monitor still maps to the nearest one.
    private const uint MonitorDefaultToNearest = 0x00000002;

    /// <summary>
    /// Returns the work area of the monitor that holds the window, or the nearest monitor when the window lies
    /// off every monitor.
    /// </summary>
    /// <param name="hwnd">The window's <c>HWND</c>.</param>
    /// <returns>
    /// The monitor's work area in physical pixels, or <see langword="null"/> when <paramref name="hwnd"/> is zero,
    /// no monitor can be found for it, or Windows cannot report the monitor's information. The method never throws
    /// for an invalid or destroyed window, so a caller that gets <see langword="null"/> keeps its current size.
    /// </returns>
    public static MonitorWorkArea? GetWorkArea(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        nint monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return null;
        }

        var info = new MonitorInfo { CbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        Rect work = info.RcWork;
        return new MonitorWorkArea(monitor, work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    // The assembly's DllImportResolver (EngineLibrary) resolves only the engine and leaves user32.dll to default
    // probing, so these imports work whether or not EngineLibrary.Bind has run.
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    // Plain MONITORINFO carries no strings, so the W entry point needs no string marshalling.
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    // RECT: 16 bytes, blittable.
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // MONITORINFO: 40 bytes, blittable. CbSize must hold the struct size on input.
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint CbSize;
        public Rect RcMonitor;
        public Rect RcWork;
        public uint DwFlags;
    }
}
