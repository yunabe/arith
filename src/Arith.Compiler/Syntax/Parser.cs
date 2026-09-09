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
    private readonly SourceText _text;
    private readonly ImmutableArray<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _position;

    /// <summary>End of the last consumed token; node spans run from their first token to here.</summary>
    private int _lastEnd;

    /// <summary>
    /// How many statements, expressions, and index wraps are currently
    /// open, capped at <see cref="SyntaxFacts.MaxNestingDepth"/>. The parser
    /// recurses once per level (a precedence-climbing level, a parenthesis,
    /// a call argument, a body block, …), and so do the binder and emitter
    /// over the tree it builds, so bounding it here keeps every stage inside
    /// the stack on pathological input (design §4.3).
    /// </summary>
    private int _nestingDepth;

    /// <summary>
    /// Whether ARITH2005 was already reported inside the current top-level
    /// statement. Everything nested in a too-deep construct is too deep as
    /// well (the condition, the body, the next `else if`), so one report
    /// per statement says it all; the skips still happen silently.
    /// </summary>
    private bool _nestingTooDeepReported;

    private Parser(SourceText text, ImmutableArray<Token> tokens, DiagnosticBag diagnostics)
    {
        _text = text;
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public static CompilationUnitSyntax Parse(
        SourceText text, ImmutableArray<Token> tokens, DiagnosticBag diagnostics) =>
        new Parser(text, tokens, diagnostics).ParseCompilationUnit();

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

        // Bad tokens were already reported by the lexer: drop the whole run
        // without a second diagnostic (cascade suppression) and let the
        // expected token match if it sits right behind, as in `let @x = 1;`.
        if (Current.Kind == SyntaxKind.BadToken)
        {
            while (Current.Kind == SyntaxKind.BadToken)
            {
                Consume();
            }

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
    // type = { "[]" } , primitive-type ;
    private TypeSyntax ParseType()
    {
        int start = Current.Span.Start;
        int arrayDepth = 0;
        while (Current.Kind == SyntaxKind.OpenBracketToken
            && Peek(1).Kind == SyntaxKind.CloseBracketToken)
        {
            Consume();
            Consume();
            arrayDepth++;
        }

        if (SyntaxFacts.IsTypeKeyword(Current.Kind))
        {
            Token keyword = Consume();
            return new TypeSyntax(keyword, arrayDepth, SpanFrom(start));
        }

        ReportUnexpected("a type name");
        Token missing = MissingToken(SyntaxKind.BadToken);
        return new TypeSyntax(missing, arrayDepth, SpanFrom(start));
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
        if (_nestingDepth >= SyntaxFacts.MaxNestingDepth)
        {
            return SkipTooDeeplyNestedStatement();
        }

        _nestingDepth++;
        StatementSyntax statement = ParseStatementCore();
        _nestingDepth--;
        if (_nestingDepth == 0)
        {
            _nestingTooDeepReported = false;
        }

        return statement;
    }

    private StatementSyntax ParseStatementCore()
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

            default:
                // Assignments are recognized after parsing the leading
                // expression (the target may be an index chain, spec §8.4);
                // see ParseExpressionStatementOrSkip.
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

    // return-statement = "return" , [ expression ] , ";" ;
    private ReturnStatementSyntax ParseReturnStatement()
    {
        int start = Consume().Span.Start;

        // Anything that cannot start an expression — ';', '}', EOF, a
        // statement keyword — selects the valueless form; the MatchToken
        // below then reports the missing ';' as the only diagnostic instead
        // of forcing a value error.
        ExpressionSyntax? value = CanStartExpression(Current.Kind) ? ParseExpression() : null;
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
            // An `else if` nests one statement deeper (design §4.3: the
            // bound tree is a nested if too), so it goes through
            // ParseStatement to count toward the nesting limit.
            elseClause = Current.Kind == SyntaxKind.IfKeyword ? ParseStatement() : ParseBlock();
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

    // for-statement = "for" , identifier , "in" , expression ,
    //                 [ ( ".." | "..=" ) , expression ] , block ;
    private StatementSyntax ParseForStatement()
    {
        int start = Consume().Span.Start;
        Token identifier = MatchToken(SyntaxKind.IdentifierToken);
        MatchToken(SyntaxKind.InKeyword);
        ExpressionSyntax startExpression = ParseExpression();

        // Without a range operator the expression is an array to iterate
        // (spec §9.3).
        if (Current.Kind is not (SyntaxKind.DotDotToken or SyntaxKind.DotDotEqualsToken))
        {
            BlockSyntax iterationBody = ParseBlock();
            return new ForEachStatementSyntax(identifier, startExpression, iterationBody, SpanFrom(start));
        }

        Token rangeOperator = Consume();
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

        // assignment-statement = assignment-target , op , expression , ";" —
        // recognized here because the target may be an index chain.
        if (IsAssignmentOperator(Current.Kind))
        {
            Token operatorToken = Consume();
            ExpressionSyntax value = ParseExpression();
            MatchToken(SyntaxKind.SemicolonToken);
            if (!IsAssignmentTarget(expression))
            {
                _diagnostics.Report(ErrorCodes.InvalidAssignmentTarget, expression.Span);
                return new ErrorStatementSyntax(SpanFrom(start));
            }

            return new AssignmentStatementSyntax(expression, operatorToken, value, SpanFrom(start));
        }

        if (expression is not (CallExpressionSyntax or ErrorExpressionSyntax))
        {
            _diagnostics.Report(ErrorCodes.NonCallExpressionStatement, expression.Span);
        }

        MatchToken(SyntaxKind.SemicolonToken);
        return new ExpressionStatementSyntax(expression, SpanFrom(start));
    }

    /// <summary>
    /// Spec §8.4: an assignment target is a variable name or an index
    /// expression — any index expression, so a chain may be rooted at a
    /// call or a fresh array literal (`f(x)[0] = 9;` writes through the
    /// returned reference). An error target was already diagnosed and
    /// passes silently.
    /// </summary>
    private static bool IsAssignmentTarget(ExpressionSyntax expression) =>
        expression is NameExpressionSyntax or IndexExpressionSyntax or ErrorExpressionSyntax;

    private static bool CanStartExpression(SyntaxKind kind) =>
        SyntaxFacts.IsTypeKeyword(kind) || kind is
            SyntaxKind.IdentifierToken or SyntaxKind.IntegerLiteralToken or
            SyntaxKind.FloatLiteralToken or SyntaxKind.StringLiteralToken or
            SyntaxKind.InterpolatedStringToken or
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword or
            SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or
            SyntaxKind.MinusToken or SyntaxKind.BangToken;

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

    private void ReportNestingTooDeep(TextSpan span)
    {
        if (_nestingTooDeepReported)
        {
            return;
        }

        _nestingTooDeepReported = true;
        _diagnostics.Report(ErrorCodes.NestingTooDeep, span, SyntaxFacts.MaxNestingDepth);
    }

    /// <summary>
    /// Recovery for a statement that would nest past the limit: reports
    /// ARITH2005 at its first token, then skips it iteratively — through
    /// its `;`, or for a compound statement up to the next statement
    /// keyword (an `if` after `else` belongs to the chain being skipped) or
    /// the `}` that closes the enclosing block. Brackets are tracked so a
    /// body block is consumed whole. The enclosing block then continues at
    /// a token it can parse, so the one diagnostic stands alone.
    /// </summary>
    private ErrorStatementSyntax SkipTooDeeplyNestedStatement()
    {
        int start = Current.Span.Start;
        ReportNestingTooDeep(Current.Span);
        int depth = 0;
        SyntaxKind previous = SyntaxKind.EndOfFileToken;
        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            SyntaxKind kind = Current.Kind;
            if (depth == 0)
            {
                if (kind is SyntaxKind.CloseBraceToken or SyntaxKind.FnKeyword)
                {
                    break;
                }

                if (previous != SyntaxKind.EndOfFileToken && IsStatementKeyword(kind)
                    && !(kind == SyntaxKind.IfKeyword && previous == SyntaxKind.ElseKeyword))
                {
                    break;
                }

                if (kind == SyntaxKind.SemicolonToken)
                {
                    Consume();
                    break;
                }
            }

            if (kind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (depth > 0
                && kind is (SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken))
            {
                depth--;
            }

            previous = kind;
            Consume();
        }

        return new ErrorStatementSyntax(SpanFrom(start));
    }

    private static bool IsStatementKeyword(SyntaxKind kind) => kind is
        SyntaxKind.LetKeyword or SyntaxKind.ReturnKeyword or SyntaxKind.IfKeyword or
        SyntaxKind.WhileKeyword or SyntaxKind.ForKeyword or SyntaxKind.BreakKeyword or
        SyntaxKind.ContinueKeyword;

    /// <summary>
    /// Recovery for an expression that would nest past the limit: reports
    /// ARITH2005 at its first token, then skips the rest of the enclosing
    /// expression iteratively — up to a closer, `;`, `,`, `{`, or range
    /// operator not matched by an opener consumed here. That leaves the
    /// enclosing production at the token it expects (the `)` of the
    /// parenthesis that went too deep, the `,` before the next argument),
    /// so the one diagnostic stands alone.
    /// </summary>
    private ErrorExpressionSyntax SkipTooDeeplyNestedExpression()
    {
        int start = Current.Span.Start;
        ReportNestingTooDeep(Current.Span);
        int depth = 0;
        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            SyntaxKind kind = Current.Kind;
            if (kind is SyntaxKind.OpenParenToken or SyntaxKind.OpenBracketToken or SyntaxKind.OpenBraceToken)
            {
                if (depth == 0 && kind == SyntaxKind.OpenBraceToken)
                {
                    break;
                }

                depth++;
            }
            else if (kind is SyntaxKind.CloseParenToken or SyntaxKind.CloseBracketToken or SyntaxKind.CloseBraceToken)
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (depth == 0 && kind is (SyntaxKind.SemicolonToken or SyntaxKind.CommaToken or
                SyntaxKind.DotDotToken or SyntaxKind.DotDotEqualsToken))
            {
                break;
            }

            Consume();
        }

        return new ErrorExpressionSyntax(SpanFrom(start));
    }

    /// <summary>
    /// Precedence-climbing expression parser implementing the table in spec
    /// §8.5. Binary operators are left-associative; unary `-`/`!` bind
    /// tighter than every binary operator.
    /// </summary>
    private ExpressionSyntax ParseExpression(int parentPrecedence = 0)
    {
        if (_nestingDepth >= SyntaxFacts.MaxNestingDepth)
        {
            return SkipTooDeeplyNestedExpression();
        }

        _nestingDepth++;
        ExpressionSyntax expression = ParseExpressionCore(parentPrecedence);
        _nestingDepth--;
        return expression;
    }

    private ExpressionSyntax ParseExpressionCore(int parentPrecedence)
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
            left = ParsePostfixExpression();
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

    // postfix = primary , { "[" , expression , "]" } ;
    private ExpressionSyntax ParsePostfixExpression()
    {
        int start = Current.Span.Start;
        ExpressionSyntax expression = ParsePrimaryExpression();
        int wraps = 0;
        while (Current.Kind == SyntaxKind.OpenBracketToken)
        {
            // The loop builds `a[i][j]…` left-deep without recursing, but
            // every later stage recurses once per `[`, so each wrap counts
            // as a nesting level; past the limit the rest of the chain is
            // skipped as one error expression.
            if (_nestingDepth >= SyntaxFacts.MaxNestingDepth)
            {
                SkipTooDeeplyNestedExpression();
                expression = new ErrorExpressionSyntax(SpanFrom(start));
                break;
            }

            _nestingDepth++;
            wraps++;
            Consume();
            ExpressionSyntax index = ParseExpression();
            MatchToken(SyntaxKind.CloseBracketToken);
            expression = new IndexExpressionSyntax(expression, index, SpanFrom(start));
        }

        _nestingDepth -= wraps;
        return expression;
    }

    // primary = literal | array-expression | call-expression | identifier | "(" , expression , ")" ;
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

            case SyntaxKind.InterpolatedStringToken:
                return ParseInterpolatedString();

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

            case SyntaxKind.OpenBracketToken:
                return ParseArrayExpression();

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
            SyntaxKind.OpenBraceToken or SyntaxKind.DotDotToken or SyntaxKind.DotDotEqualsToken or
            SyntaxKind.CloseBracketToken))
        {
            span = Consume().Span;
        }

        return new ErrorExpressionSyntax(span);
    }

    // interpolated-string = 'f"' , { text | escape | "${" , expression , "}" } , '"' ;
    //
    // Desugared right here to the equivalent concatenation of text segments
    // and string(hole) conversions (spec §4.6: f"x = ${x}" is exactly
    // "x = " + string(x)), so the binder and emitter see no new node kind
    // (design §7). The operator and callee tokens are synthesized with
    // zero-width spans; the segment spans keep real source positions.
    private ExpressionSyntax ParseInterpolatedString()
    {
        Token token = Consume();
        ExpressionSyntax? result = null;
        foreach (InterpolatedSegment segment in token.Segments)
        {
            ExpressionSyntax piece;
            if (segment.IsHole)
            {
                ExpressionSyntax hole = ParseHoleExpression(segment.Span);
                Token callee = new(SyntaxKind.StringKeyword, new TextSpan(segment.Span.Start, 0), "string");
                piece = new CallExpressionSyntax(callee, [hole], segment.Span);
            }
            else
            {
                // A synthetic quoted literal; the raw text keeps its
                // escapes for the binder's unescaping (\$ included).
                Token literal = new(
                    SyntaxKind.StringLiteralToken, segment.Span,
                    "\"" + _text.ToString(segment.Span) + "\"");
                piece = new LiteralExpressionSyntax(literal, segment.Span);
            }

            result = result is null
                ? piece
                : new BinaryExpressionSyntax(
                    result,
                    new Token(SyntaxKind.PlusToken, new TextSpan(piece.Span.Start, 0), "+"),
                    piece,
                    TextSpan.FromBounds(token.Span.Start, segment.Span.End));
        }

        // f"" is the empty string.
        return result ?? new LiteralExpressionSyntax(
            new Token(SyntaxKind.StringLiteralToken, token.Span, "\"\""), token.Span);
    }

    /// <summary>
    /// Re-lexes and parses one `${…}` hole in place, so tokens and
    /// diagnostics carry real source positions; trailing tokens inside the
    /// hole are a syntax error.
    /// </summary>
    private ExpressionSyntax ParseHoleExpression(TextSpan span)
    {
        // The hole is one level deeper than the literal. Check before
        // re-lexing: past the limit neither stage may recurse into it. The
        // re-lex is told that depth, so a literal nested in the hole whose
        // own hole would be past the limit is refused by the lexer — the
        // re-lex scans every nested literal at once, so that refusal turns
        // this whole hole into one Bad token, reported once by the lexer
        // and passed over silently here.
        if (_nestingDepth >= SyntaxFacts.MaxNestingDepth)
        {
            ReportNestingTooDeep(span);
            return new ErrorExpressionSyntax(span);
        }

        ImmutableArray<Token> tokens = Lexer.LexRange(_text, span, _diagnostics, holeDepth: _nestingDepth + 1);
        Parser parser = new(_text, tokens, _diagnostics)
        {
            _nestingDepth = _nestingDepth,
            _nestingTooDeepReported = _nestingTooDeepReported,
        };
        ExpressionSyntax expression = parser.ParseExpression();
        _nestingTooDeepReported = parser._nestingTooDeepReported;

        // A trailing Bad token was already reported by the lexer; drop the
        // run without a second diagnostic (cascade suppression), as
        // MatchToken does.
        while (parser.Current.Kind == SyntaxKind.BadToken)
        {
            parser.Consume();
        }

        if (parser.Current.Kind != SyntaxKind.EndOfFileToken)
        {
            parser.ReportUnexpected("the end of the interpolation hole");
        }

        return expression;
    }

    // array-expression = "[" , [ expression , { "," , expression } ] , "]"
    //                  | "[" , expression , ";" , expression , "]" ;
    private ExpressionSyntax ParseArrayExpression()
    {
        int start = Consume().Span.Start;
        if (Current.Kind == SyntaxKind.CloseBracketToken)
        {
            Consume();
            return new ArrayLiteralExpressionSyntax([], SpanFrom(start));
        }

        ExpressionSyntax first = ParseExpression();
        if (Current.Kind == SyntaxKind.SemicolonToken)
        {
            Consume();
            ExpressionSyntax count = ParseExpression();
            MatchToken(SyntaxKind.CloseBracketToken);
            return new ArrayRepeatExpressionSyntax(first, count, SpanFrom(start));
        }

        ImmutableArray<ExpressionSyntax>.Builder elements = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        elements.Add(first);
        while (Current.Kind == SyntaxKind.CommaToken)
        {
            Token comma = Consume();
            if (Current.Kind == SyntaxKind.CloseBracketToken)
            {
                _diagnostics.Report(ErrorCodes.TrailingComma, comma.Span);
                break;
            }

            elements.Add(ParseExpression());
        }

        MatchToken(SyntaxKind.CloseBracketToken);
        return new ArrayLiteralExpressionSyntax(elements.ToImmutable(), SpanFrom(start));
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
