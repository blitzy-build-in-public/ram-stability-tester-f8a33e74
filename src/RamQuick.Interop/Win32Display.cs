using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

/// <summary>
/// Reports the path Windows resolves for an open file, so that code which opened a file by name can verify which
/// folder the name reached, and opens a folder so that such code can hold it in place while it works in it.
/// RamQuick.Logging receives the two as <c>DataDirectory.FinalPathQuery</c> and <c>DataDirectory.FolderOpener</c>.
/// </summary>
/// <remarks>
/// Declared in this file, beside <see cref="Win32Display"/>, because this file holds the wrapper's imports from
/// Windows system DLLs, which search System32 only and bypass the engine resolver; <c>NativeMethods.cs</c> declares
/// the engine's exports alone.
/// </remarks>
public static unsafe partial class Win32FilePath
{
    // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS, both zero: long names in their on-disk case under a drive letter or UNC
    // share, with every junction and symbolic link on the way resolved.
    private const uint NormalizedDosName = 0;

    // MAX_PATH. Longer paths cost one more call, which reports the size it needs.
    private const int InitialBufferLength = 260;

    // FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES. Windows applies no sharing check to a handle that asks for no data
    // access, so FILE_READ_ATTRIBUTES alone would hold nothing in place; FILE_LIST_DIRECTORY is a folder's data access.
    private const uint FolderAccess = 0x00000001 | 0x00000080;

    // FILE_SHARE_READ | FILE_SHARE_WRITE, withholding FILE_SHARE_DELETE so that the folder cannot be renamed or deleted
    // while the handle is open.
    private const uint FolderShare = 0x00000001 | 0x00000002;

    // OPEN_EXISTING: a missing folder is reported, never created.
    private const uint OpenExisting = 3;

    // FILE_FLAG_BACKUP_SEMANTICS, without which CreateFileW opens no folder. FILE_FLAG_OPEN_REPARSE_POINT is absent on
    // purpose: a link is followed, and the final path of the handle then reports where it led.
    private const uint BackupSemantics = 0x02000000;

    // MAX_PATH: a longer path opens only with the extended-length prefix, which RAMQuick's manifest does not make
    // implicit by declaring itself long-path aware.
    private const int MaxPathLength = 260;

    // A file renamed to a longer path between two calls asks for more again; one that keeps growing is reported as a
    // failure rather than chased.
    private const int MaxAttempts = 4;

    private const string DevicePrefix = @"\\?\";
    private const string UncDevicePrefix = @"\\?\UNC\";
    private const string LocalDevicePrefix = @"\\.\";
    private const string UncPrefix = @"\\";

    /// <summary>
    /// Returns the final path of the file or directory open in <paramref name="handle"/>, as
    /// <c>GetFinalPathNameByHandleW</c> reports it with <c>FILE_NAME_NORMALIZED | VOLUME_NAME_DOS</c>.
    /// </summary>
    /// <param name="handle">An open handle; it may have been opened with any access.</param>
    /// <returns>
    /// The path without its <c>\\?\</c> prefix, so <c>C:\folder\file</c> for a local file and
    /// <c>\\server\share\file</c> for one reached through <c>\\?\UNC\</c>; or <see langword="null"/> when the handle is
    /// invalid or closed, or Windows cannot report the path, for example for a handle that is not a file's. The method
    /// never throws for a failure Windows reports.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is <see langword="null"/>.</exception>
    public static string? GetFinalPath(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            return null;
        }

        char[] buffer = new char[InitialBufferLength];
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            uint length;
            fixed (char* path = buffer)
            {
                length = GetFinalPathNameByHandle(handle, path, (uint)buffer.Length, NormalizedDosName);
            }

            if (length == 0)
            {
                return null;
            }

            // A length below the buffer's is the path's, without its terminator; any other is the size the path
            // needs, terminator included.
            if (length < buffer.Length)
            {
                return WithoutDevicePrefix(new string(buffer, 0, (int)length));
            }

            buffer = new char[length];
        }

        return null;
    }

    /// <summary>
    /// Opens the existing folder at <paramref name="path"/> and holds it in place for as long as the returned handle
    /// stays open, so that code which verifies the folder through <see cref="GetFinalPath"/> can rely on the path
    /// naming that folder while it creates or deletes entries in it.
    /// </summary>
    /// <param name="path">
    /// The folder's full path. A junction or symbolic link on the way, the folder's own name included, is followed.
    /// </param>
    /// <returns>
    /// The open handle, which the caller disposes; or <see langword="null"/> when nothing exists at the path, the path
    /// names a file, the path holds a null character, or Windows refuses the open for any other reason, such as denied
    /// access or an open handle that does not share reading and writing. The method never throws for a failure Windows
    /// reports.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The handle asks for <c>FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES</c> and shares reading and writing, not
    /// deletion. <c>FILE_LIST_DIRECTORY</c> is required because Windows checks share modes only against handles that ask
    /// for data access, and listing is a folder's data access: a handle with <c>FILE_READ_ATTRIBUTES</c> alone would
    /// hold nothing in place. <c>FILE_SHARE_DELETE</c> is withheld because it is what lets Windows rename or delete an
    /// open folder: without it, the folder can be neither renamed nor deleted while the handle is open, and no folder
    /// above it can be moved, because Windows refuses to move a folder with an open handle below it. Entries inside the
    /// folder can still be created, renamed and deleted, and other handles that share reading and writing still open.
    /// </para>
    /// <para>
    /// A link is followed rather than opened itself, so a folder swapped for a junction opens the junction's target, and
    /// <see cref="GetFinalPath"/> on the handle reports that target: that is how a caller detects the swap. A path of
    /// <c>MAX_PATH</c> characters or more is opened through the <c>\\?\</c> prefix.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public static SafeFileHandle? OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Windows would read the name only up to the null character, and so open another folder.
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        string openPath;
        try
        {
            openPath = WithLongPathPrefix(path);
        }
        catch (IOException)
        {
            // Windows could not normalize the path, so it cannot open it either.
            return null;
        }

        SafeFileHandle handle = CreateFile(openPath, FolderAccess, FolderShare, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        try
        {
            // FILE_FLAG_BACKUP_SEMANTICS opens a file as readily as a folder, and a file holds no entries in place.
            if ((File.GetAttributes(handle) & FileAttributes.Directory) != 0)
            {
                return handle;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Windows could not report what the handle opened, so it is not known to be a folder.
        }

        handle.Dispose();
        return null;
    }

    // The extended-length prefix turns off Windows' own normalization, so the path is normalized first, and a path the
    // prefix cannot carry, such as a relative one, is passed on unchanged.
    private static string WithLongPathPrefix(string path)
    {
        if (path.Length < MaxPathLength
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith(DevicePrefix, StringComparison.Ordinal)
            || path.StartsWith(LocalDevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        string fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(UncPrefix, StringComparison.Ordinal)
            ? UncDevicePrefix + fullPath[UncPrefix.Length..]
            : DevicePrefix + fullPath;
    }

    private static string WithoutDevicePrefix(string path)
    {
        if (path.StartsWith(UncDevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[UncDevicePrefix.Length..];
        }

        return path.StartsWith(DevicePrefix, StringComparison.Ordinal) ? path[DevicePrefix.Length..] : path;
    }

    // As for Win32Display: the engine resolver leaves kernel32.dll to default probing, so this import works whether
    // or not EngineLibrary.Bind has run. The SafeFileHandle is reference-counted for the duration of the call.
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* filePath, uint filePathLength, uint flags);

    // No security attributes and no template file. A failed open returns an invalid handle, which the caller disposes.
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}
