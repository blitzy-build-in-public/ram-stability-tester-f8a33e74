using System.Globalization;
using System.Text;

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
/// Links: the journal is never opened, created or deleted through a link. Each of those operations
/// first pins the journal's folder and the data folder above it (<see cref="DataFolderPin"/>),
/// which must be plain folders and, for a data folder an elevated process resolved, protected
/// ones, and a journal file that is itself a link is refused, so an elevated run cannot be steered
/// into writing or deleting a file elsewhere.
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

    private const string DamagedNameInfix = ".damaged-";
    private const string DamagedStampFormat = "yyyyMMdd-HHmmss";

    // Bounds the search for a free damaged-journal name, which a folder full of planted names could
    // otherwise prolong without end.
    private const int MaxDamagedNameAttempts = 1_000;

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
    /// The journal's folder and file are created when missing, including after <see cref="Delete"/>;
    /// the folder above the journal's folder must exist. The file stays open between appends, because
    /// reopening it for every record would add latency to the acknowledged records that a run waits
    /// on, and while it is open it keeps both folders in place.
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
    /// The record could not be written or made durable, the instance has been disposed, or the
    /// journal, its folder or the data folder is a link or, where required, not protected.
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
    /// Reads the journal's lines in the order written, without terminators: the lines of
    /// <see cref="ReadContent"/>, so no more of the journal than <see cref="JournalReadLimits"/> allows.
    /// </summary>
    /// <returns>The journal's lines, or an empty list when the journal or its folder does not exist.</returns>
    /// <exception cref="IOException">An existing journal could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies read access.</exception>
    public IReadOnlyList<string> ReadLines() => ReadContent().Lines;

    /// <summary>
    /// Reads the journal's lines in the order written, without terminators, holding no more of the file
    /// than <see cref="JournalReadLimits"/> allows, and reports what it left unread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is opened for reading with read, write and delete sharing, so an instance or another
    /// process appending to it is unaffected, and is read in chunks. A UTF-8 byte-order mark at its start
    /// is skipped. The bytes are split on <c>\n</c>, one trailing <c>\r</c> is removed from each line, and
    /// each line is decoded as UTF-8, with malformed bytes replaced by U+FFFD rather than throwing.
    /// </para>
    /// <para>
    /// Bounds: a line longer than <see cref="JournalReadLimits.MaxLineBytes"/> is left out, and no more of
    /// it than that bound is ever buffered. Reading stops after <see cref="JournalReadLimits.MaxJournalBytes"/>
    /// bytes, leaving out the line that bound cuts short, or once <see cref="JournalReadLimits.MaxLines"/>
    /// lines are held and another follows. <see cref="JournalContent.Damage"/> names the first bound reached.
    /// </para>
    /// <para>
    /// Only the empty segment after the last terminator is dropped. A final line without a terminator, at
    /// the end of the file, is returned as stored even though it may be torn, and
    /// <see cref="JournalContent.LastLineUnterminated"/> is then set; rejecting the line is the parser's job.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The lines read and the reason any part of the journal was left unread; no lines and no damage when the
    /// journal or its folder does not exist.
    /// </returns>
    /// <exception cref="IOException">
    /// An existing journal could not be read, or the journal, its folder or the data folder is a link.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies read access.</exception>
    public JournalContent ReadContent()
    {
        // A journal reached through a link is not this data folder's journal: recovering it would make
        // an elevated launch read, and act on, a file a non-elevated user pointed it to.
        if (!DataFolderPin.RequirePlainLocation(_path))
        {
            return new JournalContent(Array.Empty<string>(), Damage: null);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new JournalContent(Array.Empty<string>(), Damage: null);
        }

        using (stream)
        {
            return new BoundedLineReader().Read(stream);
        }
    }

    /// <summary>
    /// Closes the handle this instance holds open on the journal, then deletes the journal file.
    /// Does nothing when the file does not exist.
    /// </summary>
    /// <remarks>
    /// A later <see cref="AppendDurable"/> creates a new journal. When this method throws, the
    /// journal stays in place, so the next launch recovers it again; the caller decides whether that
    /// is fatal. The deletion runs while the journal's folder and the data folder are pinned, and
    /// removes a journal that is a link without touching what it points to.
    /// </remarks>
    /// <exception cref="IOException">
    /// The journal exists but could not be deleted, or its folder or the data folder is a link or,
    /// where required, not protected.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies deletion.</exception>
    public void Delete()
    {
        lock (_gate)
        {
            CloseStream();

            // A missing folder holds no journal to delete, and pinning it would need it created.
            if (string.IsNullOrEmpty(_directory) || DataFolderPin.TryGetAttributes(_directory) is null)
            {
                return;
            }

            using (DataFolderPin.ForFolder(_directory, createFolder: false))
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
        }
    }

    /// <summary>
    /// Closes the handle this instance holds open on the journal, then renames the journal file, in its
    /// folder, to <c>&lt;name&gt;.damaged-&lt;yyyyMMdd-HHmmss&gt;&lt;extension&gt;</c>, normally
    /// <c>active-run.damaged-20261028-150500.jsonl</c>, stamped in UTC.
    /// </summary>
    /// <remarks>
    /// When that name is taken, by a file or a folder, <c>-2</c>, <c>-3</c> and so on are added after the
    /// stamp until a free name is found. The rename never overwrites: should the chosen name be taken
    /// between the check and the rename, the rename fails and the journal stays in place. A later
    /// <see cref="AppendDurable"/> creates a new journal. The rename runs while the journal's folder and the data
    /// folder are pinned, and moves a journal that is a link without touching what it points to.
    /// </remarks>
    /// <param name="utc">The time the journal is retired, which the new name carries in UTC.</param>
    /// <returns>The full path the journal was moved to.</returns>
    /// <exception cref="IOException">
    /// The journal does not exist, no free name was found among the first 1,000, the rename failed, or the
    /// journal's folder or the data folder is a link or, where required, not protected.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The journal location denies the rename.</exception>
    public string MoveAside(DateTimeOffset utc)
    {
        lock (_gate)
        {
            CloseStream();

            // Pinned as for Delete, so the name checked and the rename both stay in this data folder's journal
            // folder: an elevated recovery must not rename a file in a folder a junction swapped in leads to.
            using (string.IsNullOrEmpty(_directory) ? null : DataFolderPin.ForFolder(_directory, createFolder: false))
            {
                string target = FreeDamagedName(utc);
                File.Move(_path, target, overwrite: false);
                return target;
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

    // Called with _gate held. The folders stay pinned while the journal is opened; once open, the
    // journal itself keeps them in place.
    private FileStream OpenForAppend()
    {
        using DataFolderPin? pin = string.IsNullOrEmpty(_directory)
            ? null
            : DataFolderPin.ForFolder(_directory, createFolder: true);

        FileStream stream = OpenJournalFile();
        try
        {
            // Checked again once open: an entry that became a link before the open was followed.
            if (DataFolderPin.IsLink(_path))
            {
                throw DataFolderPin.LinkedFile(_path);
            }

            long length = stream.Length;
            _startNextRecordOnNewLine = length > 0 && !EndsWithTerminator(stream, length);
            stream.Seek(0, SeekOrigin.End);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        _stream = stream;
        return stream;
    }

    // An existing journal is opened and a missing one created exclusively, so the open never follows
    // a link standing in the journal's place. Read access lets the tail be inspected through this
    // handle. bufferSize 1 disables FileStream's own buffer, so each Write reaches the OS at once and
    // WriteThrough carries it past the OS cache.
    private FileStream OpenJournalFile()
    {
        for (int attempt = 0; ; attempt++)
        {
            FileAttributes? existing = DataFolderPin.TryGetAttributes(_path);
            if (existing is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw DataFolderPin.LinkedFile(_path);
            }

            try
            {
                return new FileStream(
                    _path,
                    existing is null ? FileMode.CreateNew : FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (attempt == 0 && existing is null && DataFolderPin.TryGetAttributes(_path) is not null)
            {
                // Created by another instance since the check: open it as an existing journal.
            }
        }
    }

    // The last byte is read through the append handle itself, so the file inspected is the file the
    // records go to; a second open by path could reach another one.
    private static bool EndsWithTerminator(FileStream stream, long length)
    {
        stream.Seek(length - 1, SeekOrigin.Begin);
        return stream.ReadByte() == LineTerminator;
    }

    // The first damaged-journal name beside the journal that holds neither a file nor a folder.
    private string FreeDamagedName(DateTimeOffset utc)
    {
        string directory = _directory ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(_path) + DamagedNameInfix
            + utc.UtcDateTime.ToString(DamagedStampFormat, CultureInfo.InvariantCulture);
        string extension = Path.GetExtension(_path);
        for (int attempt = 1; attempt <= MaxDamagedNameAttempts; attempt++)
        {
            string suffix = attempt == 1 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture);
            string candidate = Path.Combine(directory, stem + suffix + extension);
            if (!Path.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"No free name was found beside the progress journal '{_path}' to keep it as damaged.");
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

    // Splits a stream into lines within JournalReadLimits, holding at most one line of at most the line bound
    // beside one read chunk, so a journal of any size or shape costs bounded memory. Single use.
    private sealed class BoundedLineReader
    {
        private const int ChunkBytes = 64 * 1024;
        private const int InitialLineBytes = 4 * 1024;
        private const int Mebibyte = 1024 * 1024;
        private const byte CarriageReturn = (byte)'\r';

        // A line within the bound may still carry the '\r' that precedes its terminator.
        private const int LineBufferLimit = JournalReadLimits.MaxLineBytes + 1;

        private static readonly byte[] _byteOrderMark = { 0xEF, 0xBB, 0xBF };

        private readonly List<string> _lines = new();
        private byte[] _line = new byte[InitialLineBytes];
        private int _lineLength;
        private bool _skippingLongLine;
        private bool _lastLineUnterminated;
        private string? _damage;

        public JournalContent Read(Stream stream)
        {
            byte[] chunk = new byte[ChunkBytes];
            long remaining = JournalReadLimits.MaxJournalBytes;

            // The journal is written without a byte-order mark; one an editor added is skipped, not read as text.
            int head = stream.ReadAtLeast(chunk.AsSpan(0, _byteOrderMark.Length), _byteOrderMark.Length, throwOnEndOfStream: false);
            remaining -= head;
            // Three bytes complete at most three lines, so the line bound cannot stop the read here.
            if (!chunk.AsSpan(0, head).SequenceEqual(_byteOrderMark))
            {
                _ = Process(chunk.AsSpan(0, head));
            }

            while (true)
            {
                if (remaining == 0)
                {
                    if (stream.ReadByte() >= 0)
                    {
                        // The line the bound cuts short is incomplete, so it is left out rather than returned torn.
                        NoteDamage(string.Create(
                            CultureInfo.InvariantCulture,
                            $"the journal is larger than {JournalReadLimits.MaxJournalBytes / Mebibyte} MiB, so the rest of it was left unread"));
                        return Content();
                    }

                    break;
                }

                int read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                if (read == 0)
                {
                    break;
                }

                remaining -= read;
                if (!Process(chunk.AsSpan(0, read)))
                {
                    return Content();
                }
            }

            // The final line has no terminator, as after an append cut short by a crash; it is kept as stored, and
            // reported as unterminated unless the line bound left it out.
            if (_lineLength > 0 || _skippingLongLine)
            {
                int held = _lines.Count;
                _ = EndLine();
                _lastLineUnterminated = _lines.Count > held;
            }

            return Content();
        }

        // Appends the data to the pending line and completes a line at each terminator. Returns false once the
        // line bound stops the read.
        private bool Process(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int terminator = data.IndexOf(LineTerminator);
                Append(terminator < 0 ? data : data[..terminator]);
                if (terminator < 0)
                {
                    return true;
                }

                if (!EndLine())
                {
                    return false;
                }

                data = data[(terminator + 1)..];
            }

            return true;
        }

        private void Append(ReadOnlySpan<byte> part)
        {
            if (_skippingLongLine || part.IsEmpty)
            {
                return;
            }

            int length = _lineLength + part.Length;
            if (length > LineBufferLimit)
            {
                // Too long to keep: the rest of the line is skipped up to its terminator, never buffered.
                _skippingLongLine = true;
                _lineLength = 0;
                return;
            }

            if (length > _line.Length)
            {
                Array.Resize(ref _line, Math.Min(Math.Max(length, _line.Length * 2), LineBufferLimit));
            }

            part.CopyTo(_line.AsSpan(_lineLength));
            _lineLength = length;
        }

        // Completes the pending line. Returns false, keeping nothing more, when the line bound is reached.
        private bool EndLine()
        {
            int length = _lineLength > 0 && _line[_lineLength - 1] == CarriageReturn ? _lineLength - 1 : _lineLength;
            bool tooLong = _skippingLongLine || length > JournalReadLimits.MaxLineBytes;
            _lineLength = 0;
            _skippingLongLine = false;
            if (tooLong)
            {
                NoteDamage(string.Create(
                    CultureInfo.InvariantCulture,
                    $"a line longer than {JournalReadLimits.MaxLineBytes / Mebibyte} MiB was left unread"));
                return true;
            }

            if (_lines.Count == JournalReadLimits.MaxLines)
            {
                NoteDamage(string.Create(
                    CultureInfo.InvariantCulture,
                    $"the journal holds more than {JournalReadLimits.MaxLines:N0} lines, so the rest of them were left unread"));
                return false;
            }

            _lines.Add(_utf8.GetString(_line, 0, length));
            return true;
        }

        private void NoteDamage(string damage) => _damage ??= damage;

        private JournalContent Content() => new(_lines.AsReadOnly(), _damage, _lastLineUnterminated);
    }
}
