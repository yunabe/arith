namespace Arith.Compiler.Syntax;

public static class SyntaxFacts
{
    /// <summary>Maps an identifier-shaped word to its keyword kind, or IdentifierToken if it is not a keyword.</summary>
    public static SyntaxKind GetKeywordOrIdentifierKind(string text) => text switch
    {
        "fn" => SyntaxKind.FnKeyword,
        "let" => SyntaxKind.LetKeyword,
        "return" => SyntaxKind.ReturnKeyword,
        "if" => SyntaxKind.IfKeyword,
        "else" => SyntaxKind.ElseKeyword,
        "while" => SyntaxKind.WhileKeyword,
        "for" => SyntaxKind.ForKeyword,
        "in" => SyntaxKind.InKeyword,
        "break" => SyntaxKind.BreakKeyword,
        "continue" => SyntaxKind.ContinueKeyword,
        "true" => SyntaxKind.TrueKeyword,
        "false" => SyntaxKind.FalseKeyword,
        "bool" => SyntaxKind.BoolKeyword,
        "i32" => SyntaxKind.I32Keyword,
        "i64" => SyntaxKind.I64Keyword,
        "f32" => SyntaxKind.F32Keyword,
        "f64" => SyntaxKind.F64Keyword,
        "string" => SyntaxKind.StringKeyword,
        _ => SyntaxKind.IdentifierToken,
    };

    /// <summary>
    /// The fixed source text of a keyword, punctuation, or operator kind, or
    /// null for kinds whose text varies (identifiers, literals, Bad, EOF).
    /// </summary>
    public static string? GetText(SyntaxKind kind) => kind switch
    {
        SyntaxKind.FnKeyword => "fn",
        SyntaxKind.LetKeyword => "let",
        SyntaxKind.ReturnKeyword => "return",
        SyntaxKind.IfKeyword => "if",
        SyntaxKind.ElseKeyword => "else",
        SyntaxKind.WhileKeyword => "while",
        SyntaxKind.ForKeyword => "for",
        SyntaxKind.InKeyword => "in",
        SyntaxKind.BreakKeyword => "break",
        SyntaxKind.ContinueKeyword => "continue",
        SyntaxKind.TrueKeyword => "true",
        SyntaxKind.FalseKeyword => "false",
        SyntaxKind.BoolKeyword => "bool",
        SyntaxKind.I32Keyword => "i32",
        SyntaxKind.I64Keyword => "i64",
        SyntaxKind.F32Keyword => "f32",
        SyntaxKind.F64Keyword => "f64",
        SyntaxKind.StringKeyword => "string",
        SyntaxKind.OpenParenToken => "(",
        SyntaxKind.CloseParenToken => ")",
        SyntaxKind.OpenBraceToken => "{",
        SyntaxKind.CloseBraceToken => "}",
        SyntaxKind.CommaToken => ",",
        SyntaxKind.ColonToken => ":",
        SyntaxKind.SemicolonToken => ";",
        SyntaxKind.ArrowToken => "->",
        SyntaxKind.DotDotToken => "..",
        SyntaxKind.DotDotEqualsToken => "..=",
        SyntaxKind.PlusToken => "+",
        SyntaxKind.MinusToken => "-",
        SyntaxKind.StarToken => "*",
        SyntaxKind.SlashToken => "/",
        SyntaxKind.PercentToken => "%",
        SyntaxKind.BangToken => "!",
        SyntaxKind.EqualsToken => "=",
        SyntaxKind.PlusEqualsToken => "+=",
        SyntaxKind.MinusEqualsToken => "-=",
        SyntaxKind.StarEqualsToken => "*=",
        SyntaxKind.SlashEqualsToken => "/=",
        SyntaxKind.PercentEqualsToken => "%=",
        SyntaxKind.EqualsEqualsToken => "==",
        SyntaxKind.BangEqualsToken => "!=",
        SyntaxKind.LessToken => "<",
        SyntaxKind.LessEqualsToken => "<=",
        SyntaxKind.GreaterToken => ">",
        SyntaxKind.GreaterEqualsToken => ">=",
        SyntaxKind.AmpersandAmpersandToken => "&&",
        SyntaxKind.PipePipeToken => "||",
        _ => null,
    };

    /// <summary>True for the six type-name keywords, which double as conversion callees (spec §7).</summary>
    public static bool IsTypeKeyword(SyntaxKind kind) => kind is
        SyntaxKind.BoolKeyword or SyntaxKind.I32Keyword or SyntaxKind.I64Keyword or
        SyntaxKind.F32Keyword or SyntaxKind.F64Keyword or SyntaxKind.StringKeyword;
}
