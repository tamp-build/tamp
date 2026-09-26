using System.Text;

namespace Tamp;

/// <summary>
/// Agent-facing NDJSON sink for the canonical <see cref="BuildEvent"/> stream
/// (`#0b`, ADR 0019). Writes one JSON object per line to a dedicated channel —
/// a filesystem path from <c>--events</c> / <c>TAMP_EVENTS</c> — <b>separate</b>
/// from the human console. Purely additive: the banner, <c>==&gt;</c> lines, and
/// build summary on stdout are untouched.
/// </summary>
/// <remarks>
/// <para>
/// Lines are UTF-8 (no BOM) with <c>\n</c> endings (LF even on Windows, for
/// agent-parser portability) and flushed per event, so a consumer can
/// <c>tail -f</c> the file live or read it after the run. The file is truncated
/// per run — one build → one event stream; <see cref="BuildEvent.RunId"/> is on
/// every line regardless.
/// </para>
/// <para>
/// The events channel must never fail a build (additive &amp; opt-in): use
/// <see cref="TryCreate"/>, which warns and returns <see langword="null"/> if the
/// target can't be opened.
/// </para>
/// </remarks>
public sealed class NdjsonEventSink : IBuildEventSink, IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Construct over a caller-owned writer (used by tests). The writer is not disposed here.</summary>
    public NdjsonEventSink(TextWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _writer.NewLine = "\n";
        _ownsWriter = false;
    }

    private NdjsonEventSink(StreamWriter writer, bool ownsWriter)
    {
        _writer = writer;
        _writer.NewLine = "\n";
        _ownsWriter = ownsWriter;
    }

    /// <summary>
    /// Open <paramref name="path"/> for the NDJSON stream (truncate). On failure,
    /// write one warning to <paramref name="warnTo"/> and return <see langword="null"/>
    /// so the build proceeds without the channel.
    /// </summary>
    public static NdjsonEventSink? TryCreate(string path, TextWriter warnTo)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read);
            var writer = new StreamWriter(stream, Utf8NoBom);
            return new NdjsonEventSink(writer, ownsWriter: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try { warnTo?.WriteLine($"tamp: could not open event stream '{path}' ({ex.GetType().Name}: {ex.Message}); continuing without it."); }
            catch { /* warning is best-effort */ }
            return null;
        }
    }

    public void Emit(BuildEvent e)
    {
        if (_disposed) return;
        var line = BuildEventJson.Serialize(e);
        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer.Flush(); } catch { /* best-effort */ }
            if (_ownsWriter)
            {
                try { _writer.Dispose(); } catch { /* best-effort */ }
            }
        }
    }
}
