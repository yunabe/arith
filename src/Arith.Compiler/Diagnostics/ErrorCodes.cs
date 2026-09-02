namespace Arith.Compiler.Diagnostics;

/// <summary>
/// The registry of every diagnostic the compiler can report. Codes are grouped
/// by stage — ARITH1xxx lexical, ARITH2xxx syntactic, ARITH3xxx semantic — and
/// stay stable once released.
/// </summary>
public static class ErrorCodes
{
    // Lexical errors (ARITH1xxx).
    public static readonly DiagnosticDescriptor UnexpectedCharacter =
        new("ARITH1001", "unexpected character '{0}'");

    public static readonly DiagnosticDescriptor UnterminatedStringLiteral =
        new("ARITH1002", "unterminated string literal");

    public static readonly DiagnosticDescriptor InvalidEscapeSequence =
        new("ARITH1003", "invalid escape sequence '{0}'");

    public static readonly DiagnosticDescriptor UnterminatedBlockComment =
        new("ARITH1004", "unterminated block comment");

    public static readonly DiagnosticDescriptor InvalidNumericSuffix =
        new("ARITH1005", "invalid suffix '{0}' on numeric literal");
}
