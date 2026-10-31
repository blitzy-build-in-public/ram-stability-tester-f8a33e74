using System.Runtime.InteropServices;

namespace RamQuick.Sensors;

/// <summary>
/// Read-only access to the raw SMBIOS table through the Windows firmware table provider API.
/// </summary>
/// <remarks>
/// <c>GetSystemFirmwareTable</c> with the <c>'RSMB'</c> provider needs neither a driver nor
/// elevation, and it reads firmware data without changing anything. It is the only Win32 call this
/// class makes.
/// </remarks>
internal static unsafe partial class NativeFirmware
{
    /// <summary>
    /// The firmware table provider signature of the raw SMBIOS table, <c>'RSMB'</c>.
    /// </summary>
    /// <remarks>
    /// The Win32 documentation writes the signature as the C multi-character constant
    /// <c>'RSMB'</c>. MSVC packs such a constant with its first character in the most significant
    /// byte, so <c>'R'</c> (0x52) is the high byte and <c>'B'</c> (0x42) the low byte. Building the
    /// value from the string's bytes in memory order would give the reversed 0x424D5352, which
    /// Windows rejects.
    /// </remarks>
    internal const uint RsmbProviderSignature = 0x52534D42;

    /// <summary>
    /// An initial read plus one retry, for a table that changes size between the size query and the
    /// read.
    /// </summary>
    private const int MaxReadAttempts = 2;

    /// <summary>
    /// Retrieves a firmware table from the specified firmware table provider.
    /// </summary>
    /// <param name="firmwareTableProviderSignature">The provider signature, for example <see cref="RsmbProviderSignature"/>.</param>
    /// <param name="firmwareTableId">The table identifier; 0 for the raw SMBIOS table.</param>
    /// <param name="firmwareTableBuffer">The buffer that receives the table, or null to query the required size.</param>
    /// <param name="bufferSize">The size of <paramref name="firmwareTableBuffer"/> in bytes; 0 with a null buffer.</param>
    /// <returns>
    /// The number of bytes written on success. When the buffer is null or too small, the required
    /// size in bytes, with nothing written. 0 on failure; <see cref="Marshal.GetLastPInvokeError"/>
    /// then holds the Windows error code.
    /// </returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial uint GetSystemFirmwareTable(
        uint firmwareTableProviderSignature, uint firmwareTableId, byte* firmwareTableBuffer, uint bufferSize);

    /// <summary>
    /// Reads the complete raw SMBIOS table.
    /// </summary>
    /// <returns>
    /// The full <c>'RSMB'</c> blob: the 8-byte <c>RawSMBIOSData</c> header
    /// (<c>Used20CallingMethod</c>, <c>SMBIOSMajorVersion</c>, <c>SMBIOSMinorVersion</c>,
    /// <c>DmiRevision</c>, a 32-bit <c>Length</c>) followed by the SMBIOS structure table, which is
    /// the input format of <c>SmbiosParser.Parse</c>. Null when the firmware table cannot be
    /// read; API failures never throw.
    /// </returns>
    internal static byte[]? ReadRawSmbiosTable()
    {
        uint requiredSize = GetSystemFirmwareTable(RsmbProviderSignature, 0, null, 0);

        for (int attempt = 1; requiredSize != 0; attempt++)
        {
            byte[] buffer = new byte[requiredSize];
            uint written;
            fixed (byte* bufferPointer = buffer)
            {
                written = GetSystemFirmwareTable(RsmbProviderSignature, 0, bufferPointer, requiredSize);
            }

            if (written != 0 && written <= requiredSize)
            {
                return written == requiredSize ? buffer : buffer[..(int)written];
            }

            if (attempt == MaxReadAttempts)
            {
                break;
            }

            // A result larger than the buffer is the new size of a table that grew after the size
            // query; a failed read gets a fresh size query before the retry.
            requiredSize = written != 0
                ? written
                : GetSystemFirmwareTable(RsmbProviderSignature, 0, null, 0);
        }

        return null;
    }
}
