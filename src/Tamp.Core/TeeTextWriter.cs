using System.Text;

namespace Tamp;

/// <summary>
/// Fans every write to several inner <see cref="TextWriter"/>s (#23). Used to send a
/// target's output to both the console writer and its per-target log file, each of which
/// applies its own redaction. Single-threaded by contract (the executor serializes writes).
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter[] _writers;

    public TeeTextWriter(params TextWriter[] writers)
        => _writers = (writers ?? Array.Empty<TextWriter>()).Where(w => w is not null).ToArray();

    public override Encoding Encoding => _writers.Length > 0 ? _writers[0].Encoding : Encoding.UTF8;

    public override void Write(char value) { foreach (var w in _writers) w.Write(value); }
    public override void Write(string? value) { foreach (var w in _writers) w.Write(value); }
    public override void Write(char[] buffer, int index, int count) { foreach (var w in _writers) w.Write(buffer, index, count); }
    public override void WriteLine(string? value) { foreach (var w in _writers) w.WriteLine(value); }
    public override void WriteLine() { foreach (var w in _writers) w.WriteLine(); }
    public override void Flush() { foreach (var w in _writers) w.Flush(); }
}
