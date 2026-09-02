namespace Arith.Compiler.Syntax;

/// <summary>Token kinds produced by the lexer. Syntax-node kinds join this enum with the parser.</summary>
public enum SyntaxKind
{
    // Special tokens.
    BadToken,
    EndOfFileToken,

    // Literals and identifiers.
    IntegerLiteralToken,
    FloatLiteralToken,
    StringLiteralToken,
    IdentifierToken,

    // Keywords (spec §2.2).
    FnKeyword,
    LetKeyword,
    ReturnKeyword,
    IfKeyword,
    ElseKeyword,
    WhileKeyword,
    ForKeyword,
    InKeyword,
    BreakKeyword,
    ContinueKeyword,
    TrueKeyword,
    FalseKeyword,
    BoolKeyword,
    I32Keyword,
    I64Keyword,
    F32Keyword,
    F64Keyword,
    StringKeyword,

    // Punctuation.
    OpenParenToken,
    CloseParenToken,
    OpenBraceToken,
    CloseBraceToken,
    CommaToken,
    ColonToken,
    SemicolonToken,
    ArrowToken,
    DotDotToken,
    DotDotEqualsToken,

    // Operators.
    PlusToken,
    MinusToken,
    StarToken,
    SlashToken,
    PercentToken,
    BangToken,
    EqualsToken,
    PlusEqualsToken,
    MinusEqualsToken,
    StarEqualsToken,
    SlashEqualsToken,
    PercentEqualsToken,
    EqualsEqualsToken,
    BangEqualsToken,
    LessToken,
    LessEqualsToken,
    GreaterToken,
    GreaterEqualsToken,
    AmpersandAmpersandToken,
    PipePipeToken,
}
