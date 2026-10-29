using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RamQuick.Logging;

/// <summary>
/// The production <see cref="IJournalStorage"/>: the progress journal as a UTF-8 JSON Lines file,
/// normally <c>&lt;data&gt;\journal\active-run.jsonl</c>, appended one durable record at a time.
/// </summary>
/// <remarks>
/// <para>
/// Durability: each record is one write through a <see cref="FileStream"/> opened with
/// <see cref="FileOptions.WriteThrough"/> and no managed buffering, followed by
/// <see cref="FileStream.Flush(bool)"/> with <c>flushToDisk: true</c> (<c>FlushFileBuffers</c>).
/// A record whose append has returned survives process termination, a hang followed by a hard
/// reset, and power loss, to the extent the drive honours flush. A crash during an append can leave
/// a torn final line; <see cref="ReadLines"/> returns it as stored and the journal parser ignores it.
/// </para>
/// <para>
/// Sharing: while this instance holds the journal open for appending, other instances and other
/// processes can still open it for reading, and the file can be deleted. No second writer can open
/// it, so records are never interleaved.
/// </para>
/// <para>
/// Threading: as <see cref="IJournalStorage"/> requires, one thread uses an instance at a time.
/// A private lock nevertheless guards the open handle, so a <see cref="Dispose"/> issued from
/// another thread cannot close the stream underneath an append in progress.
/// </para>
/// </remarks>
public sealed class FileJournalStorage : IJournalStorage
{
    private const byte LineTerminator = (byte)'\n';

    // No byte-order mark, and invalid input never throws: unpaired surrogates are written and
    // malformed bytes are read as U+FFFD, so a damaged line reaches the parser, which rejects it.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly string _path;
    private readonly string? _directory;
    private readonly object _gate = new();

    private FileStream? _stream;

    // Set when the stream was opened on a journal whose last byte is not a terminator, as after an
    // append cut short by a failed write. The next record then starts with a terminator, so the
    // fragment stays a line of its own that the parser skips instead of swallowing the new record.
    private bool _startNextRecordOnNewLine;

    private volatile bool _disposed;

    /// <summary>
    /// Initializes a storage for the journal file at <paramref name="path"/>. Nothing is opened or
    /// created until the first <see cref="AppendDurable"/>.
    /// </summary>
    /// <param name="path">
    /// The journal file, normally <c>DataDirectory.JournalPath</c>. A relative path is resolved
    /// against the current directory once, here.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, white space or not a valid path.
    /// </exception>
    public FileJournalStorage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path);
    }

    /// <summary>
    /// Gets the full path of the journal file.
    /// </summary>
    /// <remarks>Never changes for an instance and may be read from any thread.</remarks>
    public string Location => _path;

    /// <summary>
    /// Returns whether the journal file exists.
    /// </summary>
    /// <returns><see langword="true"/> when the file exists, even an empty or torn one.</returns>
    public bool Exists() => File.Exists(_path);

    /// <summary>
    /// Appends <paramref name="line"/> and a <c>\n</c> terminator as UTF-8 without a byte-order mark,
    /// and returns once the record is written through and flushed to the storage device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The journal's folder and file are created when missing, including after <see cref="Delete"/>.
    /// The file stays open between appends, because reopening it for every record would add latency
    /// to the acknowledged records that a run waits on.
    /// </para>
    /// <para>
    /// The record is durable once this method returns, to the extent the drive honours flush. When
    /// it throws, the open handle is closed and the next call reopens the journal, so the instance
    /// stays usable once the cause is removed. If the failed write left part of its record behind,
    /// the next record starts on a new line.
    /// </para>
    /// </remarks>
    /// <param name="line">The serialized record, without <c>\r</c> or <c>\n</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="line"/> contains <c>\r</c> or <c>\n</c>.</exception>
    /// <exception cref="IOException">
    /// The record could not be written or made durable, or the instance has been disposed.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies write access.</exception>
    public void AppendDurable(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("A journal record must be a single line without '\\r' or '\\n'.", nameof(line));
        }

        try
        {
            lock (_gate)
            {
                // A disposed instance reports an ordinary write failure, which the journal writer
                // records like any other, rather than an exception from a closed stream.
                if (_disposed)
                {
                    throw new IOException($"The progress journal '{_path}' has been closed.");
                }

                try
                {
                    FileStream stream = _stream ?? OpenForAppend();
                    byte[] record = EncodeRecord(line, _startNextRecordOnNewLine);
                    stream.Write(record, 0, record.Length);
                    stream.Flush(flushToDisk: true);
                    _startNextRecordOnNewLine = false;
                }
                catch
                {
                    CloseStream();
                    throw;
                }
            }
        }
        finally
        {
            // A Dispose that found the lock held by this append left the stream for it to close.
            if (_disposed)
            {
                CloseStreamIfUncontended();
            }
        }
    }

    /// <summary>
    /// Reads every line of the journal in the order written, without terminators.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is opened for reading with read, write and delete sharing, so an instance or another
    /// process appending to it is unaffected. The content is decoded as UTF-8, with malformed bytes
    /// replaced by U+FFFD rather than throwing, and split on <c>\n</c>; one trailing <c>\r</c> is
    /// removed from each line.
    /// </para>
    /// <para>
    /// Only the empty segment after the last terminator is dropped. A final line without a
    /// terminator is returned as stored even though it may be torn; rejecting it is the parser's job.
    /// </para>
    /// </remarks>
    /// <returns>The journal's lines, or an empty list when the journal or its folder does not exist.</returns>
    /// <exception cref="IOException">An existing journal could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies read access.</exception>
    public IReadOnlyList<string> ReadLines()
    {
        string content;
        try
        {
            using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, _utf8, detectEncodingFromByteOrderMarks: false);
            content = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }

        string[] segments = content.Split('\n');
        int count = segments[^1].Length == 0 ? segments.Length - 1 : segments.Length;

        string[] lines = new string[count];
        for (int index = 0; index < count; index++)
        {
            string segment = segments[index];
            lines[index] = segment.EndsWith('\r') ? segment[..^1] : segment;
        }

        return lines;
    }

    /// <summary>
    /// Closes the handle this instance holds open on the journal, then deletes the journal file.
    /// Does nothing when the file does not exist.
    /// </summary>
    /// <remarks>
    /// A later <see cref="AppendDurable"/> creates a new journal. When this method throws, the
    /// journal stays in place, so the next launch recovers it again; the caller decides whether that
    /// is fatal.
    /// </remarks>
    /// <exception cref="IOException">The journal exists but could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies deletion.</exception>
    public void Delete()
    {
        lock (_gate)
        {
            CloseStream();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }

    /// <summary>
    /// Closes the handle this instance holds open on the journal and leaves the journal in place,
    /// so a run that ends without its <c>end</c> record stays recoverable. Idempotent.
    /// </summary>
    /// <remarks>
    /// Never waits behind an append in progress, which may be stalled on the device: when called from
    /// another thread during an append, that append closes the handle as it returns. After this call,
    /// <see cref="AppendDurable"/> throws <see cref="IOException"/>.
    /// </remarks>
    public void Dispose()
    {
        _disposed = true;
        CloseStreamIfUncontended();
    }

    private static byte[] EncodeRecord(string line, bool startOnNewLine)
    {
        int prefixLength = startOnNewLine ? 1 : 0;
        byte[] record = new byte[prefixLength + _utf8.GetByteCount(line) + 1];
        if (startOnNewLine)
        {
            record[0] = LineTerminator;
        }

        _utf8.GetBytes(line, 0, line.Length, record, prefixLength);
        record[^1] = LineTerminator;
        return record;
    }

    // Called with _gate held.
    private FileStream OpenForAppend()
    {
        if (!string.IsNullOrEmpty(_directory))
        {
            Directory.CreateDirectory(_directory);
        }

        // bufferSize 1 disables FileStream's own buffer, so each Write reaches the OS at once and
        // WriteThrough carries it past the OS cache.
        FileStream stream = new(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            bufferSize: 1,
            FileOptions.WriteThrough);
        try
        {
            _startNextRecordOnNewLine = stream.Length > 0 && !EndsWithTerminator(stream.Length);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        _stream = stream;
        return stream;
    }

    // The append handle is write-only, so the last byte is read through a second, shared handle.
    private bool EndsWithTerminator(long length)
    {
        using SafeFileHandle handle = File.OpenHandle(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Span<byte> lastByte = stackalloc byte[1];
        return RandomAccess.Read(handle, lastByte, length - 1) == 1 && lastByte[0] == LineTerminator;
    }

    // Called with _gate held.
    private void CloseStream()
    {
        FileStream? stream = _stream;
        _stream = null;
        stream?.Dispose();
    }

    // Dispose and an append on another thread each call this after setting or seeing _disposed, so
    // whichever of them does not hold the lock at that moment closes the stream; neither blocks.
    private void CloseStreamIfUncontended()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            CloseStream();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }
}
