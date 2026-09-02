namespace Arith.Compiler.Text;

/// <summary>
/// An immutable source document: its text, the path it was loaded from, and a
/// lazily built line map used to render diagnostic positions.
/// </summary>
public sealed class SourceText
{
    private readonly string _text;
    private int[]? _lineStarts;

    private SourceText(string text, string filePath)
    {
        _text = text;
        FilePath = filePath;
    }

    public string FilePath { get; }

    public int Length => _text.Length;

    public char this[int index] => _text[index];

    public static SourceText From(string text, string filePath = "") => new(text, filePath);

    public string ToString(TextSpan span) => _text.Substring(span.Start, span.Length);

    public override string ToString() => _text;

    /// <summary>Maps a position in [0, Length] to its 1-based line and column.</summary>
    public LinePosition GetLinePosition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Length);

        int[] lineStarts = _lineStarts ??= ComputeLineStarts(_text);
        int index = Array.BinarySearch(lineStarts, position);
        int line = index >= 0 ? index : ~index - 1;
        return new LinePosition(line + 1, position - lineStarts[line] + 1);
    }

    private static int[] ComputeLineStarts(string text)
    {
        List<int> lineStarts = [0];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            if (c is '\r' or '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        return [.. lineStarts];
    }
}
