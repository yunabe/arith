using Arith.Compiler.Text;

namespace Arith.Compiler.Syntax;

/// <summary>
/// One lexed token: its kind, source span, and raw text. Literal values are
/// deliberately not parsed here — an unsuffixed numeric literal's type (and
/// therefore its valid range) is only known once the binder has an expected
/// type, so the raw text travels with the token until then.
/// A missing token is one the parser fabricated (with empty text and a
/// zero-length span) where the grammar required a token that was not there;
/// the surrounding syntax node exists, but later stages must not trust the
/// token's text.
/// </summary>
public readonly record struct Token(SyntaxKind Kind, TextSpan Span, string Text, bool IsMissing = false)
{
    public override string ToString() => $"{Kind} {Span} \"{Text}\"{(IsMissing ? " (missing)" : "")}";
}
