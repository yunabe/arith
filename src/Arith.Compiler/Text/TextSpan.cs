namespace Arith.Compiler.Text;

/// <summary>A half-open range [Start, End) into a source text, in UTF-16 code units.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    public override string ToString() => $"[{Start}..{End})";
}
