using System.Collections.Immutable;

using Arith.Compiler.Diagnostics;
using Arith.Compiler.Text;

namespace Arith.Compiler.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar in spec §12, one method per
/// production (binary operators use precedence climbing instead of the
/// grammar's cascaded productions).
///
/// Error handling follows the multi-diagnostic policy: the parser always
/// returns a complete tree. A missing required token is fabricated
/// (Token.IsMissing) and reported; source that cannot start a statement is
/// skipped to a statement boundary and replaced by an ErrorStatementSyntax;
/// an impossible expression becomes an ErrorExpressionSyntax. Bad tokens are
/// consumed silently — the lexer already reported them.
/// </summary>
public sealed class Parser
{
    private readonly ImmutableArray<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _position;

    /// <summary>End of the last consumed token; node spans run from their first token to here.</summary>
    private int _lastEnd;

    private Parser(ImmutableArray<Token> tokens, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public static CompilationUnitSyntax Parse(ImmutableArray<Token> tokens, DiagnosticBag diagnostics) =>
        new Parser(tokens, diagnostics).ParseCompilationUnit();

    private Token Current => Peek(0);

    private Token Peek(int offset)
    {
        int index = _position + offset;
        return index < _tokens.Length ? _tokens[index] : _tokens[^1];
    }

    private Token Consume()
    {
        Token token = Current;
        if (_position < _tokens.Length - 1)
        {
            _position++;
        }

        _lastEnd = token.Span.End;
        return token;
    }

    /// <summary>
    /// Consumes the expected token, or reports ARITH2001 and fabricates a
    /// missing one without consuming — the actual token often belongs to the
    /// enclosing production (e.g. the `;` after a broken expression).
    /// </summary>
    private Token MatchToken(SyntaxKind kind)
    {
        if (Current.Kind == kind)
        {
            return Consume();
        }

        // A Bad token was already reported by the lexer: drop it without a
        // second diagnostic (cascade suppression) and let the expected token
        // match if it sits right behind, as in `let @x = 1;`.
        if (Current.Kind == SyntaxKind.BadToken)
        {
            Consume();
            return Current.Kind == kind ? Consume() : MissingToken(kind);
        }

        ReportUnexpected(Describe(kind));
        return MissingToken(kind);
    }

    private Token MissingToken(SyntaxKind kind) =>
        new(kind, new TextSpan(Current.Span.Start, 0), "", IsMissing: true);

    private void ReportUnexpected(string expected)
    {
        string actual = Current.Kind == SyntaxKind.EndOfFileToken
            ? "end of file"
            : $"'{Current.Text}'";
        _diagnostics.Report(ErrorCodes.UnexpectedToken, Current.Span, actual, expected);
    }

    private static string Describe(SyntaxKind kind) => SyntaxFacts.GetText(kind) is { } text
        ? $"'{text}'"
        : kind == SyntaxKind.IdentifierToken ? "an identifier" : kind.ToString();

    private TextSpan SpanFrom(int start) => TextSpan.FromBounds(start, Math.Max(start, _lastEnd));

    // program = { function-declaration } , EOF ;
    private CompilationUnitSyntax ParseCompilationUnit()
    {
        ImmutableArray<FunctionDeclarationSyntax>.Builder functions =
            ImmutableArray.CreateBuilder<FunctionDeclarationSyntax>();
        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            if (Current.Kind == SyntaxKind.FnKeyword)
            {
                functions.Add(ParseFunctionDeclaration());
                continue;
            }

            // Only function declarations may appear at the top level (spec
            // §1). Report once, then skip silently to the next `fn`.
            if (Current.Kind != SyntaxKind.BadToken)
            {
                ReportUnexpected("'fn'");
            }

            while (Current.Kind is not (SyntaxKind.FnKeyword or SyntaxKind.EndOfFileToken))
            {
                Consume();
            }
        }

        return new CompilationUnitSyntax(functions.ToImmutable(), SpanFrom(0));
    }

    // function-declaration = "fn" , identifier , "(" , [ parameter-list ] , ")" , [ "->" , type ] , block ;
    private FunctionDeclarationSyntax ParseFunctionDeclaration()
    {
        int start = Current.Span.Start;
        MatchToken(SyntaxKind.FnKeyword);
        Token identifier = MatchToken(SyntaxKind.IdentifierToken);
        MatchToken(SyntaxKind.OpenParenToken);
        ImmutableArray<ParameterSyntax>.Builder parameters = ImmutableArray.CreateBuilder<ParameterSyntax>();
        if (Current.Kind is not (SyntaxKind.CloseParenToken or SyntaxKind.EndOfFileToken))
        {
            while (true)
            {
                parameters.Add(ParseParameter());
                if (Current.Kind != SyntaxKind.CommaToken)
                {
                    break;
                }

                Token comma = Consume();
                if (Current.Kind == SyntaxKind.CloseParenToken)
                {
                    _diagnostics.Report(ErrorCodes.TrailingComma, comma.Span);
                    break;
                }
            }
        }

        MatchToken(SyntaxKind.CloseParenToken);
        TypeSyntax? returnType = null;
        if (Current.Kind == SyntaxKind.ArrowToken)
        {
            Consume();
            returnType = ParseType();
        }

        BlockSyntax body = ParseBlock();
        return new FunctionDeclarationSyntax(
            identifier, parameters.ToImmutable(), returnType, body, SpanFrom(start));
    }

    // parameter = identifier , ":" , type ;
    private ParameterSyntax ParseParameter()
    {
        int start = Current.Span.Start;
        Token identifier = MatchToken(SyntaxKind.IdentifierToken);
        MatchToken(SyntaxKind.ColonToken);
        TypeSyntax type = ParseType();
        return new ParameterSyntax(identifier, type, SpanFrom(start));
    }

    // type = "bool" | "i32" | "i64" | "f32" | "f64" | "string" ;
    private TypeSyntax ParseType()
    {
        if (SyntaxFacts.IsTypeKeyword(Current.Kind))
        {
            Token keyword = Consume();
            return new TypeSyntax(keyword, keyword.Span);
        }

        ReportUnexpected("a type name");
        Token missing = MissingToken(SyntaxKind.BadToken);
        return new TypeSyntax(missing, missing.Span);
    }

    // block = "{" , { statement } , "}" ;
    private BlockSyntax ParseBlock()
    {
        int start = Current.Span.Start;
        MatchToken(SyntaxKind.OpenBraceToken);
        ImmutableArray<StatementSyntax>.Builder statements = ImmutableArray.CreateBuilder<StatementSyntax>();
        while (Current.Kind is not (SyntaxKind.CloseBraceToken or SyntaxKind.EndOfFileToken))
        {
            int before = _position;
            statements.Add(ParseStatement());
            if (_position == before)
            {
                // Defensive progress guarantee: a statement that consumed
                // nothing would loop forever, so drop one token.
                Consume();
            }
        }

        MatchToken(SyntaxKind.CloseBraceToken);
        return new BlockSyntax(statements.ToImmutable(), SpanFrom(start));
    }

    private StatementSyntax ParseStatement()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.LetKeyword:
                return ParseLetStatement();
            case SyntaxKind.ReturnKeyword:
                return ParseReturnStatement();
            case SyntaxKind.IfKeyword:
                return ParseIfStatement();
            case SyntaxKind.WhileKeyword:
                return ParseWhileStatement();
            case SyntaxKind.ForKeyword:
                return ParseForStatement();
            case SyntaxKind.BreakKeyword:
            {
                int start = Consume().Span.Start;
                MatchToken(SyntaxKind.SemicolonToken);
                return new BreakStatementSyntax(SpanFrom(start));
            }

            case SyntaxKind.ContinueKeyword:
            {
                int start = Consume().Span.Start;
                MatchToken(SyntaxKind.SemicolonToken);
                return new ContinueStatementSyntax(SpanFrom(start));
            }

            case SyntaxKind.IdentifierToken when IsAssignmentOperator(Peek(1).Kind):
                return ParseAssignmentStatement();
            default:
                return ParseExpressionStatementOrSkip();
        }
    }

    private static bool IsAssignmentOperator(SyntaxKind kind) => kind is
        SyntaxKind.EqualsToken or SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken or
        SyntaxKind.StarEqualsToken or SyntaxKind.SlashEqualsToken or SyntaxKind.PercentEqualsToken;

    // let-statement = "let" , identifier , [ ":" , type ] , "=" , expression , ";" ;
    private LetStatementSyntax ParseLetStatement()
    {
        int start = Consume().Span.Start;
        Token identifier = MatchToken(SyntaxKind.IdentifierToken);
        TypeSyntax? type = null;
        if (Current.Kind == SyntaxKind.ColonToken)
        {
            Consume();
            type = ParseType();
        }

        MatchToken(SyntaxKind.EqualsToken);
        ExpressionSyntax initializer = ParseExpression();
        MatchToken(SyntaxKind.SemicolonToken);
        return new LetStatementSyntax(identifier, type, initializer, SpanFrom(start));
    }

    // assignment-statement = identifier , ( "=" | "+=" | "-=" | "*=" | "/=" | "%=" ) , expression , ";" ;
    private AssignmentStatementSyntax ParseAssignmentStatement()
    {
        int start = Current.Span.Start;
        Token identifier = Consume();
        Token operatorToken = Consume();
        ExpressionSyntax value = ParseExpression();
        MatchToken(SyntaxKind.SemicolonToken);
        return new AssignmentStatementSyntax(identifier, operatorToken, value, SpanFrom(start));
    }

    // return-statement = "return" , [ expression ] , ";" ;
    private ReturnStatementSyntax ParseReturnStatement()
    {
        int start = Consume().Span.Start;

        // A '}' right after `return` means the valueless form with only its
        // ';' missing — MatchToken reports that; don't force a value error.
        ExpressionSyntax? value =
            Current.Kind is SyntaxKind.SemicolonToken or SyntaxKind.CloseBraceToken
                ? null
                : ParseExpression();
        MatchToken(SyntaxKind.SemicolonToken);
        return new ReturnStatementSyntax(value, SpanFrom(start));
    }

    // if-statement = "if" , expression , block , [ "else" , ( if-statement | block ) ] ;
    private IfStatementSyntax ParseIfStatement()
    {
        int start = Consume().Span.Start;
        ExpressionSyntax condition = ParseExpression();
        BlockSyntax then = ParseBlock();
        StatementSyntax? elseClause = null;
        if (Current.Kind == SyntaxKind.ElseKeyword)
        {
            Consume();
            elseClause = Current.Kind == SyntaxKind.IfKeyword ? ParseIfStatement() : ParseBlock();
        }

        return new IfStatementSyntax(condition, then, elseClause, SpanFrom(start));
    }

    // while-statement = "while" , expression , block ;
    private WhileStatementSyntax ParseWhileStatement()
    {
        int start = Consume().Span.Start;
        ExpressionSyntax condition = ParseExpression();
        BlockSyntax body = ParseBlock();
        return new WhileStatementSyntax(condition, body, SpanFrom(start));
    }

    // for-statement = "for" , identifier , "in" , expression , ( ".." | "..=" ) , expression , block ;
    private ForStatementSyntax ParseForStatement()
    {
        int start = Consume().Span.Start;
        Token identifier = MatchToken(SyntaxKind.IdentifierToken);
        MatchToken(SyntaxKind.InKeyword);
        ExpressionSyntax startExpression = ParseExpression();
        Token rangeOperator = Current.Kind == SyntaxKind.DotDotEqualsToken
            ? Consume()
            : MatchToken(SyntaxKind.DotDotToken);
        ExpressionSyntax endExpression = ParseExpression();
        BlockSyntax body = ParseBlock();
        return new ForStatementSyntax(
            identifier, startExpression, rangeOperator, endExpression, body, SpanFrom(start));
    }

    // expression-statement = call-expression , ";" ;
    private StatementSyntax ParseExpressionStatementOrSkip()
    {
        // The lexer already reported Bad tokens; drop them (and a trailing
        // `;`) without a second diagnostic (cascade suppression).
        if (Current.Kind == SyntaxKind.BadToken)
        {
            int badStart = Consume().Span.Start;
            if (Current.Kind == SyntaxKind.SemicolonToken)
            {
                Consume();
            }

            return new ErrorStatementSyntax(SpanFrom(badStart));
        }

        if (!CanStartExpression(Current.Kind))
        {
            ReportUnexpected("a statement");
            int skipStart = Current.Span.Start;
            SkipToStatementBoundary();
            return new ErrorStatementSyntax(SpanFrom(skipStart));
        }

        int start = Current.Span.Start;
        ExpressionSyntax expression = ParseExpression();
        if (expression is not (CallExpressionSyntax or ErrorExpressionSyntax))
        {
            _diagnostics.Report(ErrorCodes.NonCallExpressionStatement, expression.Span);
        }

        MatchToken(SyntaxKind.SemicolonToken);
        return new ExpressionStatementSyntax(expression, SpanFrom(start));
    }

    private static bool CanStartExpression(SyntaxKind kind) =>
        SyntaxFacts.IsTypeKeyword(kind) || kind is
            SyntaxKind.IdentifierToken or SyntaxKind.IntegerLiteralToken or
            SyntaxKind.FloatLiteralToken or SyntaxKind.StringLiteralToken or
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or
            SyntaxKind.OpenParenToken or SyntaxKind.MinusToken or SyntaxKind.BangToken;

    /// <summary>Skips past a `;` or up to (not past) a token that can begin the next statement.</summary>
    private void SkipToStatementBoundary()
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case SyntaxKind.SemicolonToken:
                    Consume();
                    return;
                case SyntaxKind.EndOfFileToken or SyntaxKind.CloseBraceToken or
                     SyntaxKind.LetKeyword or SyntaxKind.ReturnKeyword or SyntaxKind.IfKeyword or
                     SyntaxKind.WhileKeyword or SyntaxKind.ForKeyword or SyntaxKind.BreakKeyword or
                     SyntaxKind.ContinueKeyword or SyntaxKind.FnKeyword:
                    return;
                default:
                    Consume();
                    break;
            }
        }
    }

    /// <summary>
    /// Precedence-climbing expression parser implementing the table in spec
    /// §8.5. Binary operators are left-associative; unary `-`/`!` bind
    /// tighter than every binary operator.
    /// </summary>
    private ExpressionSyntax ParseExpression(int parentPrecedence = 0)
    {
        int start = Current.Span.Start;
        ExpressionSyntax left;
        if (Current.Kind is SyntaxKind.MinusToken or SyntaxKind.BangToken)
        {
            Token operatorToken = Consume();
            ExpressionSyntax operand = ParseExpression(UnaryPrecedence);
            left = new UnaryExpressionSyntax(operatorToken, operand, SpanFrom(start));
        }
        else
        {
            left = ParsePrimaryExpression();
        }

        while (true)
        {
            int precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
            {
                return left;
            }

            Token operatorToken = Consume();
            ExpressionSyntax right = ParseExpression(precedence);
            left = new BinaryExpressionSyntax(left, operatorToken, right, SpanFrom(start));
        }
    }

    private const int UnaryPrecedence = 7;

    private static int GetBinaryPrecedence(SyntaxKind kind) => kind switch
    {
        SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken => 6,
        SyntaxKind.PlusToken or SyntaxKind.MinusToken => 5,
        SyntaxKind.LessToken or SyntaxKind.LessEqualsToken or
        SyntaxKind.GreaterToken or SyntaxKind.GreaterEqualsToken => 4,
        SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken => 3,
        SyntaxKind.AmpersandAmpersandToken => 2,
        SyntaxKind.PipePipeToken => 1,
        _ => 0,
    };

    // primary = literal | call-expression | identifier | "(" , expression , ")" ;
    private ExpressionSyntax ParsePrimaryExpression()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken or
                 SyntaxKind.StringLiteralToken or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword:
            {
                Token literal = Consume();
                return new LiteralExpressionSyntax(literal, literal.Span);
            }

            case SyntaxKind.IdentifierToken when Peek(1).Kind == SyntaxKind.OpenParenToken:
                return ParseCallExpression();
            case SyntaxKind.IdentifierToken:
            {
                Token identifier = Consume();
                return new NameExpressionSyntax(identifier, identifier.Span);
            }

            // A type keyword in expression position must be a conversion
            // call like `i64(x)` (spec §7); MatchToken reports it otherwise.
            case var kind when SyntaxFacts.IsTypeKeyword(kind):
                return ParseCallExpression();
            case SyntaxKind.OpenParenToken:
            {
                int start = Consume().Span.Start;
                ExpressionSyntax expression = ParseExpression();
                MatchToken(SyntaxKind.CloseParenToken);
                return new ParenthesizedExpressionSyntax(expression, SpanFrom(start));
            }

            default:
                return ParseErrorExpression();
        }
    }

    /// <summary>
    /// Reports the token that cannot start an expression. Tokens that likely
    /// belong to the enclosing production — closers (`;` `)` `}` `,` EOF),
    /// a body-opening `{`, and the range operators — stay put for it to
    /// consume; anything else is dropped to guarantee progress.
    /// </summary>
    private ErrorExpressionSyntax ParseErrorExpression()
    {
        TextSpan span = new(Current.Span.Start, 0);
        if (Current.Kind != SyntaxKind.BadToken)
        {
            ReportUnexpected("an expression");
        }

        if (Current.Kind is not (SyntaxKind.SemicolonToken or SyntaxKind.CloseParenToken or
            SyntaxKind.CloseBraceToken or SyntaxKind.CommaToken or SyntaxKind.EndOfFileToken or
            SyntaxKind.OpenBraceToken or SyntaxKind.DotDotToken or SyntaxKind.DotDotEqualsToken))
        {
            span = Consume().Span;
        }

        return new ErrorExpressionSyntax(span);
    }

    // call-expression = ( identifier | type ) , "(" , [ argument-list ] , ")" ;
    private CallExpressionSyntax ParseCallExpression()
    {
        int start = Current.Span.Start;
        Token callee = Consume();
        MatchToken(SyntaxKind.OpenParenToken);
        ImmutableArray<ExpressionSyntax>.Builder arguments = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        if (Current.Kind is not (SyntaxKind.CloseParenToken or SyntaxKind.EndOfFileToken))
        {
            while (true)
            {
                arguments.Add(ParseExpression());
                if (Current.Kind != SyntaxKind.CommaToken)
                {
                    break;
                }

                Token comma = Consume();
                if (Current.Kind == SyntaxKind.CloseParenToken)
                {
                    _diagnostics.Report(ErrorCodes.TrailingComma, comma.Span);
                    break;
                }
            }
        }

        MatchToken(SyntaxKind.CloseParenToken);
        return new CallExpressionSyntax(callee, arguments.ToImmutable(), SpanFrom(start));
    }
}
