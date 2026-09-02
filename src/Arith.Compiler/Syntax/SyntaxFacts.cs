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
}
