namespace Arith.Compiler.Text;

/// <summary>A 1-based line and column, as rendered in diagnostics.</summary>
public readonly record struct LinePosition(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}
