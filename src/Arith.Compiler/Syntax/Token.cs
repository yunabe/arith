using Arith.Compiler.Text;

namespace Arith.Compiler.Syntax;

/// <summary>
/// One lexed token: its kind, source span, and raw text. Literal values are
/// deliberately not parsed here — an unsuffixed numeric literal's type (and
/// therefore its valid range) is only known once the binder has an expected
/// type, so the raw text travels with the token until then.
/// </summary>
public readonly record struct Token(SyntaxKind Kind, TextSpan Span, string Text)
{
    public override string ToString() => $"{Kind} {Span} \"{Text}\"";
}
