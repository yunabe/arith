using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;

using Arith.Compiler.Diagnostics;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Binding;

/// <summary>
/// Name resolution and type checking (design §4.4). Binding runs in two
/// passes — a declaration pass building the global function table, then a
/// body pass — because declaration order is insignificant and functions may
/// be mutually recursive (spec §1). Binding always runs to completion:
/// unresolvable syntax binds to Error-typed nodes that suppress cascading
/// diagnostics, and error placeholder syntax binds silently.
///
/// Staging (design §6, step 4): this binder covers functions, locals,
/// numeric arithmetic, let/assignment/return/call/print, and the full
/// pending-literal machinery. Control flow, comparison/logical operators,
/// and explicit conversions report the temporary ARITH3901 until steps 6–7.
/// </summary>
public sealed class Binder
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, FunctionSymbol> _functions = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, VariableSymbol>> _scopes = [];
    private FunctionSymbol? _currentFunction;

    private Binder(DiagnosticBag diagnostics) => _diagnostics = diagnostics;

    public static BoundProgram Bind(CompilationUnitSyntax root, DiagnosticBag diagnostics) =>
        new Binder(diagnostics).BindCompilationUnit(root);

    private BoundProgram BindCompilationUnit(CompilationUnitSyntax root)
    {
        // Declaration pass: collect every signature before binding any body.
        List<(FunctionDeclarationSyntax Syntax, FunctionSymbol Symbol)> declarations = [];
        foreach (FunctionDeclarationSyntax syntax in root.Functions)
        {
            FunctionSymbol symbol = BindFunctionSignature(syntax);
            declarations.Add((syntax, symbol));
            if (syntax.Identifier.IsMissing)
            {
                continue;
            }

            if (symbol.Name == "print")
            {
                _diagnostics.Report(ErrorCodes.PrintRedeclared, syntax.Identifier.Span);
            }
            else if (!_functions.TryAdd(symbol.Name, symbol))
            {
                _diagnostics.Report(ErrorCodes.DuplicateFunction, syntax.Identifier.Span, symbol.Name);
            }
        }

        ValidateEntryPoint(declarations);

        // Body pass. Bodies of duplicate declarations are still bound (for
        // their own diagnostics) but only the declared symbol's function is
        // part of the program.
        ImmutableArray<BoundFunction>.Builder functions = ImmutableArray.CreateBuilder<BoundFunction>();
        foreach ((FunctionDeclarationSyntax syntax, FunctionSymbol symbol) in declarations)
        {
            BoundBlock body = BindFunctionBody(syntax, symbol);
            if (_functions.TryGetValue(symbol.Name, out FunctionSymbol? declared) && ReferenceEquals(declared, symbol))
            {
                functions.Add(new BoundFunction(symbol, body));
            }
        }

        return new BoundProgram(functions.ToImmutable(), _functions.GetValueOrDefault("main"));
    }

    private static FunctionSymbol BindFunctionSignature(FunctionDeclarationSyntax syntax)
    {
        ImmutableArray<ParameterSymbol>.Builder parameters =
            ImmutableArray.CreateBuilder<ParameterSymbol>(syntax.Parameters.Length);
        for (int i = 0; i < syntax.Parameters.Length; i++)
        {
            ParameterSyntax parameter = syntax.Parameters[i];
            parameters.Add(new ParameterSymbol(parameter.Identifier.Text, BindType(parameter.Type), i));
        }

        ArithType returnType = syntax.ReturnType is null ? ArithType.Void : BindType(syntax.ReturnType);
        return new FunctionSymbol(syntax.Identifier.Text, parameters.MoveToImmutable(), returnType);
    }

    private static ArithType BindType(TypeSyntax syntax) => syntax.Keyword.Kind switch
    {
        SyntaxKind.BoolKeyword => ArithType.Bool,
        SyntaxKind.I32Keyword => ArithType.I32,
        SyntaxKind.I64Keyword => ArithType.I64,
        SyntaxKind.F32Keyword => ArithType.F32,
        SyntaxKind.F64Keyword => ArithType.F64,
        SyntaxKind.StringKeyword => ArithType.String,
        _ => ArithType.Error, // The parser already reported the missing type.
    };

    private void ValidateEntryPoint(List<(FunctionDeclarationSyntax Syntax, FunctionSymbol Symbol)> declarations)
    {
        if (!_functions.TryGetValue("main", out FunctionSymbol? main))
        {
            _diagnostics.Report(ErrorCodes.MissingEntryPoint, new TextSpan(0, 0));
            return;
        }

        // Spec §5.1: no parameters; returns i32 or nothing. An Error return
        // type was already reported by the parser.
        bool validReturn = main.ReturnType == ArithType.Void
            || main.ReturnType == ArithType.I32
            || main.ReturnType.IsError;
        if (main.Parameters.Length > 0 || !validReturn)
        {
            FunctionDeclarationSyntax syntax = declarations.First(d => ReferenceEquals(d.Symbol, main)).Syntax;
            _diagnostics.Report(ErrorCodes.InvalidEntryPointSignature, syntax.Identifier.Span);
        }
    }

    private BoundBlock BindFunctionBody(FunctionDeclarationSyntax syntax, FunctionSymbol symbol)
    {
        _currentFunction = symbol;

        // Spec §6: parameters are variables in the scope of the function
        // body, so the body block shares their scope rather than nesting.
        PushScope();
        for (int i = 0; i < symbol.Parameters.Length; i++)
        {
            DeclareVariable(symbol.Parameters[i], syntax.Parameters[i].Identifier);
        }

        BoundBlock body = BindBlock(syntax.Body, pushScope: false);
        PopScope();
        _currentFunction = null;

        // Minimal definite-return check for the linear step-4/5 subset: with
        // no control flow, "every reachable path returns" (spec §5) means
        // the body contains a return at all. Step 6 replaces this with the
        // full analysis over branches and loops (design §4.4).
        if (symbol.ReturnType != ArithType.Void && !symbol.ReturnType.IsError && !ContainsReturn(body))
        {
            _diagnostics.Report(ErrorCodes.NotAllPathsReturn, syntax.Identifier.Span, symbol.Name);
        }

        return body;
    }

    private static bool ContainsReturn(BoundBlock block) =>
        block.Statements.Any(s => s is BoundReturnStatement || (s is BoundBlock nested && ContainsReturn(nested)));

    private BoundBlock BindBlock(BlockSyntax syntax, bool pushScope = true)
    {
        if (pushScope)
        {
            PushScope();
        }

        ImmutableArray<BoundStatement>.Builder statements =
            ImmutableArray.CreateBuilder<BoundStatement>(syntax.Statements.Length);
        foreach (StatementSyntax statement in syntax.Statements)
        {
            statements.Add(BindStatement(statement));
        }

        if (pushScope)
        {
            PopScope();
        }

        return new BoundBlock(statements.MoveToImmutable());
    }

    private BoundStatement BindStatement(StatementSyntax syntax)
    {
        switch (syntax)
        {
            case LetStatementSyntax let:
                return BindLetStatement(let);
            case AssignmentStatementSyntax assignment:
                return BindAssignmentStatement(assignment);
            case ExpressionStatementSyntax statement:
                return BindExpressionStatement(statement);
            case ReturnStatementSyntax ret:
                return BindReturnStatement(ret);
            case BlockSyntax block:
                return BindBlock(block);
            case IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax or
                 BreakStatementSyntax or ContinueStatementSyntax:
                // Design §6 steps 6: control flow arrives with the emitter
                // support for branches; report once and keep binding.
                _diagnostics.Report(ErrorCodes.NotYetImplemented, syntax.Span, StatementDescription(syntax));
                return new BoundErrorStatement();
            case ErrorStatementSyntax:
                return new BoundErrorStatement(); // Already diagnosed by the parser.
            default:
                throw new UnreachableException($"unhandled statement type {syntax.GetType().Name}");
        }
    }

    private static string StatementDescription(StatementSyntax syntax) => syntax switch
    {
        IfStatementSyntax => "'if'",
        WhileStatementSyntax => "'while'",
        ForStatementSyntax => "'for'",
        BreakStatementSyntax => "'break'",
        ContinueStatementSyntax => "'continue'",
        _ => throw new UnreachableException($"unhandled statement type {syntax.GetType().Name}"),
    };

    private BoundLetStatement BindLetStatement(LetStatementSyntax syntax)
    {
        ArithType? annotated = syntax.Type is null ? null : BindType(syntax.Type);
        BoundExpression initializer;
        ArithType localType;
        if (annotated is null)
        {
            // No annotation: the initializer's own type is the local's type,
            // with pending literals resolving to their category default.
            initializer = ResolveToDefault(BindExpression(syntax.Initializer, expected: null));
            localType = initializer.Type;
            if (localType == ArithType.Void)
            {
                _diagnostics.Report(ErrorCodes.ExpressionHasNoValue, syntax.Initializer.Span);
                localType = ArithType.Error;
            }
        }
        else
        {
            initializer = BindExpressionWithType(syntax.Initializer, annotated);
            localType = annotated;
        }

        LocalSymbol local = new(syntax.Identifier.Text, localType);
        DeclareVariable(local, syntax.Identifier);
        return new BoundLetStatement(local, initializer);
    }

    private BoundStatement BindAssignmentStatement(AssignmentStatementSyntax syntax)
    {
        VariableSymbol? variable = LookupVariable(syntax.Identifier);
        if (variable is null)
        {
            ResolveToDefault(BindExpression(syntax.Value, expected: null));
            return new BoundErrorStatement();
        }

        BoundExpression value = BindExpressionWithType(syntax.Value, variable.Type);
        BoundBinaryOperatorKind? compound = syntax.OperatorToken.Kind switch
        {
            SyntaxKind.EqualsToken => null,
            SyntaxKind.PlusEqualsToken => BoundBinaryOperatorKind.Addition,
            SyntaxKind.MinusEqualsToken => BoundBinaryOperatorKind.Subtraction,
            SyntaxKind.StarEqualsToken => BoundBinaryOperatorKind.Multiplication,
            SyntaxKind.SlashEqualsToken => BoundBinaryOperatorKind.Division,
            SyntaxKind.PercentEqualsToken => BoundBinaryOperatorKind.Remainder,
            _ => throw new UnreachableException($"unhandled assignment operator {syntax.OperatorToken.Kind}"),
        };
        if (compound is { } kind && !variable.Type.IsError
            && !IsArithmeticOperandType(kind, variable.Type))
        {
            _diagnostics.Report(
                ErrorCodes.InvalidBinaryOperator, syntax.OperatorToken.Span,
                syntax.OperatorToken.Text, variable.Type, variable.Type);
            return new BoundErrorStatement();
        }

        return new BoundAssignmentStatement(variable, compound, value);
    }

    private BoundStatement BindExpressionStatement(ExpressionStatementSyntax syntax)
    {
        // `print` is a compiler-recognized built-in usable only as a
        // statement (spec §10.1); intercept it before ordinary call binding.
        if (syntax.Expression is CallExpressionSyntax { Callee.Kind: SyntaxKind.IdentifierToken } call
            && call.Callee.Text == "print")
        {
            return BindPrintStatement(call);
        }

        BoundExpression expression = ResolveToDefault(BindExpression(syntax.Expression, expected: null));
        return new BoundExpressionStatement(expression);
    }

    private BoundStatement BindPrintStatement(CallExpressionSyntax syntax)
    {
        if (syntax.Arguments.Length != 1)
        {
            foreach (ExpressionSyntax argument in syntax.Arguments)
            {
                ResolveToDefault(BindExpression(argument, expected: null));
            }

            _diagnostics.Report(ErrorCodes.WrongArgumentCount, syntax.Span, "print", 1, syntax.Arguments.Length);
            return new BoundErrorStatement();
        }

        // The argument has no expected type, so unsuffixed literals take
        // their defaults: print(1 + 2) prints an i64 (design §4.4).
        BoundExpression bound = ResolveToDefault(BindExpression(syntax.Arguments[0], expected: null));
        if (bound.Type == ArithType.Void)
        {
            _diagnostics.Report(ErrorCodes.ExpressionHasNoValue, syntax.Arguments[0].Span);
            return new BoundErrorStatement();
        }

        return new BoundPrintStatement(bound);
    }

    private BoundReturnStatement BindReturnStatement(ReturnStatementSyntax syntax)
    {
        ArithType returnType = _currentFunction?.ReturnType ?? ArithType.Error;
        if (syntax.Value is null)
        {
            if (returnType != ArithType.Void && !returnType.IsError)
            {
                _diagnostics.Report(ErrorCodes.MissingReturnValue, syntax.Span, returnType);
            }

            return new BoundReturnStatement(null);
        }

        if (returnType == ArithType.Void)
        {
            ResolveToDefault(BindExpression(syntax.Value, expected: null));
            _diagnostics.Report(ErrorCodes.ReturnValueInVoidFunction, syntax.Value.Span);
            return new BoundReturnStatement(null);
        }

        return new BoundReturnStatement(BindExpressionWithType(syntax.Value, returnType));
    }

    // ---- Expressions ----------------------------------------------------

    /// <summary>
    /// Binds an expression in a forcing context (design §4.4): a still-
    /// pending result resolves to the target when the categories agree and
    /// to the category default otherwise, and the final type must equal the
    /// target exactly — Arith has no implicit conversions (spec §7).
    /// </summary>
    private BoundExpression BindExpressionWithType(ExpressionSyntax syntax, ArithType target)
    {
        BoundExpression bound = BindExpression(syntax, target.IsError ? null : target);
        if (bound.Type.IsPending)
        {
            bound = ResolvePending(
                bound, bound.Type.CanResolveTo(target) ? target : bound.Type.DefaultForPending);
        }

        // Void is not a denotable value type: a void-producing call misused
        // as a value is "no value here" (ARITH3017), not a type mismatch
        // against whatever the context happened to expect.
        if (bound.Type == ArithType.Void)
        {
            _diagnostics.Report(ErrorCodes.ExpressionHasNoValue, syntax.Span);
            return new BoundErrorExpression();
        }

        if (!bound.Type.IsError && !target.IsError && bound.Type != target)
        {
            _diagnostics.Report(ErrorCodes.TypeMismatch, syntax.Span, target, bound.Type);
            return new BoundErrorExpression();
        }

        return bound;
    }

    /// <summary>Resolves a still-pending expression to its category default (i64 / f64).</summary>
    private BoundExpression ResolveToDefault(BoundExpression bound) =>
        bound.Type.IsPending ? ResolvePending(bound, bound.Type.DefaultForPending) : bound;

    private BoundExpression BindExpression(ExpressionSyntax syntax, ArithType? expected)
    {
        switch (syntax)
        {
            case LiteralExpressionSyntax literal:
                return BindLiteral(literal.LiteralToken, expected, negated: false, literal.Span);
            case NameExpressionSyntax name:
            {
                VariableSymbol? variable = LookupVariable(name.Identifier);
                return variable is null ? new BoundErrorExpression() : new BoundVariableExpression(variable);
            }

            case ParenthesizedExpressionSyntax paren:
                return BindExpression(paren.Expression, expected);
            case UnaryExpressionSyntax unary:
                return BindUnaryExpression(unary, expected);
            case BinaryExpressionSyntax binary:
                return BindBinaryExpression(binary, expected);
            case CallExpressionSyntax call:
                return BindCallExpression(call);
            case ErrorExpressionSyntax:
                return new BoundErrorExpression(); // Already diagnosed by the parser.
            default:
                throw new UnreachableException($"unhandled expression type {syntax.GetType().Name}");
        }
    }

    private BoundExpression BindUnaryExpression(UnaryExpressionSyntax syntax, ArithType? expected)
    {
        if (syntax.OperatorToken.Kind == SyntaxKind.BangToken)
        {
            // Logical operators arrive with control flow (design §6 step 6).
            ResolveToDefault(BindExpression(syntax.Operand, expected: null));
            _diagnostics.Report(ErrorCodes.NotYetImplemented, syntax.OperatorToken.Span, "operator '!'");
            return new BoundErrorExpression();
        }

        // Spec §4.2: an integer literal directly beneath unary `-` is
        // checked as an unsigned magnitude, so -9223372036854775808 is a
        // valid i64. Fold the sign into the literal here.
        if (syntax.Operand is LiteralExpressionSyntax
            {
                LiteralToken: { Kind: SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken } token
            })
        {
            return BindLiteral(token, expected, negated: true, syntax.Span);
        }

        BoundExpression operand = BindExpression(syntax.Operand, expected);
        if (operand.Type.IsError)
        {
            return new BoundErrorExpression();
        }

        if (operand.Type == ArithType.Void)
        {
            _diagnostics.Report(ErrorCodes.ExpressionHasNoValue, syntax.Operand.Span);
            return new BoundErrorExpression();
        }

        if (!operand.Type.IsNumeric)
        {
            _diagnostics.Report(
                ErrorCodes.InvalidUnaryOperator, syntax.OperatorToken.Span, "-", operand.Type);
            return new BoundErrorExpression();
        }

        // A pending operand keeps the whole node pending for later resolution.
        return new BoundUnaryExpression(BoundUnaryOperatorKind.Negation, operand, operand.Type);
    }

    private BoundExpression BindBinaryExpression(BinaryExpressionSyntax syntax, ArithType? expected)
    {
        BoundBinaryOperatorKind? kind = syntax.OperatorToken.Kind switch
        {
            SyntaxKind.PlusToken => BoundBinaryOperatorKind.Addition,
            SyntaxKind.MinusToken => BoundBinaryOperatorKind.Subtraction,
            SyntaxKind.StarToken => BoundBinaryOperatorKind.Multiplication,
            SyntaxKind.SlashToken => BoundBinaryOperatorKind.Division,
            SyntaxKind.PercentToken => BoundBinaryOperatorKind.Remainder,
            _ => null,
        };
        if (kind is null)
        {
            // Comparison, equality, and logical operators arrive with
            // control flow (design §6 steps 6–7).
            ResolveToDefault(BindExpression(syntax.Left, expected: null));
            ResolveToDefault(BindExpression(syntax.Right, expected: null));
            _diagnostics.Report(
                ErrorCodes.NotYetImplemented, syntax.OperatorToken.Span,
                $"operator '{syntax.OperatorToken.Text}'");
            return new BoundErrorExpression();
        }

        BoundExpression left = BindExpression(syntax.Left, expected);
        BoundExpression right = BindExpression(syntax.Right, expected);

        // A void operand's primary problem is the missing value, not the
        // operator; report it at the operand, like every other value
        // context — and before the Error short-circuit, so an unrelated
        // Error on the other side cannot hide it.
        bool hasVoidOperand = false;
        if (left.Type == ArithType.Void)
        {
            _diagnostics.Report(ErrorCodes.ExpressionHasNoValue, syntax.Left.Span);
            hasVoidOperand = true;
        }

        if (right.Type == ArithType.Void)
        {
            _diagnostics.Report(ErrorCodes.ExpressionHasNoValue, syntax.Right.Span);
            hasVoidOperand = true;
        }

        if (hasVoidOperand || left.Type.IsError || right.Type.IsError)
        {
            return new BoundErrorExpression();
        }

        // Design §4.4: a concrete operand of the same category is the
        // expected type of a pending sibling; categories never cross.
        if (left.Type.IsPending && !right.Type.IsPending && left.Type.CanResolveTo(right.Type))
        {
            left = ResolvePending(left, right.Type);
        }
        else if (right.Type.IsPending && !left.Type.IsPending && right.Type.CanResolveTo(left.Type))
        {
            right = ResolvePending(right, left.Type);
        }
        else if (left.Type.IsPending && right.Type.IsPending && left.Type == right.Type
            && IsArithmeticOperandType(kind.Value, left.Type))
        {
            // Both sides stay pending: the whole operation is pending.
            return new BoundBinaryExpression(kind.Value, left, right, left.Type);
        }

        // Mixed categories and other invalid pairs: resolve what is still
        // pending to its default so the diagnostic names real types.
        left = ResolveToDefault(left);
        right = ResolveToDefault(right);
        if (left.Type.IsError || right.Type.IsError)
        {
            return new BoundErrorExpression();
        }

        if (left.Type != right.Type || !IsArithmeticOperandType(kind.Value, left.Type))
        {
            _diagnostics.Report(
                ErrorCodes.InvalidBinaryOperator, syntax.OperatorToken.Span,
                syntax.OperatorToken.Text, left.Type, right.Type);
            return new BoundErrorExpression();
        }

        return new BoundBinaryExpression(kind.Value, left, right, left.Type);
    }

    /// <summary>Spec §8.1: arithmetic needs numeric operands; `%` needs integers. (String `+` arrives in step 7.)</summary>
    private static bool IsArithmeticOperandType(BoundBinaryOperatorKind kind, ArithType type) =>
        kind == BoundBinaryOperatorKind.Remainder ? type.IsInteger : type.IsNumeric;

    private BoundExpression BindCallExpression(CallExpressionSyntax syntax)
    {
        if (SyntaxFacts.IsTypeKeyword(syntax.Callee.Kind))
        {
            // Explicit conversions arrive in step 7 (design §6).
            BindArgumentsForDiagnostics(syntax);
            _diagnostics.Report(ErrorCodes.NotYetImplemented, syntax.Callee.Span, "explicit conversions");
            return new BoundErrorExpression();
        }

        if (syntax.Callee.IsMissing)
        {
            BindArgumentsForDiagnostics(syntax);
            return new BoundErrorExpression();
        }

        if (syntax.Callee.Text == "print")
        {
            // Statement-position print was intercepted; here its (absent)
            // value is being used.
            BindArgumentsForDiagnostics(syntax);
            _diagnostics.Report(ErrorCodes.ExpressionHasNoValue, syntax.Span);
            return new BoundErrorExpression();
        }

        if (!_functions.TryGetValue(syntax.Callee.Text, out FunctionSymbol? function))
        {
            BindArgumentsForDiagnostics(syntax);
            _diagnostics.Report(ErrorCodes.UndefinedFunction, syntax.Callee.Span, syntax.Callee.Text);
            return new BoundErrorExpression();
        }

        if (syntax.Arguments.Length != function.Parameters.Length)
        {
            BindArgumentsForDiagnostics(syntax);
            _diagnostics.Report(
                ErrorCodes.WrongArgumentCount, syntax.Span,
                function.Name, function.Parameters.Length, syntax.Arguments.Length);
            return new BoundErrorExpression();
        }

        // Spec §7: a parameter's type is an expected type for its argument.
        ImmutableArray<BoundExpression>.Builder arguments =
            ImmutableArray.CreateBuilder<BoundExpression>(syntax.Arguments.Length);
        for (int i = 0; i < syntax.Arguments.Length; i++)
        {
            arguments.Add(BindExpressionWithType(syntax.Arguments[i], function.Parameters[i].Type));
        }

        return new BoundCallExpression(function, arguments.MoveToImmutable());
    }

    /// <summary>Binds arguments of an unresolvable call purely for their own diagnostics.</summary>
    private void BindArgumentsForDiagnostics(CallExpressionSyntax syntax)
    {
        foreach (ExpressionSyntax argument in syntax.Arguments)
        {
            ResolveToDefault(BindExpression(argument, expected: null));
        }
    }

    // ---- Literals and pending resolution --------------------------------

    private BoundExpression BindLiteral(Token token, ArithType? expected, bool negated, TextSpan span)
    {
        switch (token.Kind)
        {
            case SyntaxKind.TrueKeyword:
                return new BoundLiteralExpression(ArithType.Bool, true, token);
            case SyntaxKind.FalseKeyword:
                return new BoundLiteralExpression(ArithType.Bool, false, token);
            case SyntaxKind.StringLiteralToken:
                return new BoundLiteralExpression(ArithType.String, UnescapeString(token.Text), token);
            case SyntaxKind.IntegerLiteralToken:
            {
                ArithType? type = token.Text.EndsWith("i32", StringComparison.Ordinal) ? ArithType.I32
                    : token.Text.EndsWith("i64", StringComparison.Ordinal) ? ArithType.I64
                    : expected is { IsInteger: true, IsPending: false } ? expected
                    : null;
                return type is null
                    ? new BoundLiteralExpression(ArithType.PendingInt, Value: null, token, negated)
                    : MakeIntegerLiteral(token, type, negated, span);
            }

            case SyntaxKind.FloatLiteralToken:
            {
                ArithType? type = token.Text.EndsWith("f32", StringComparison.Ordinal) ? ArithType.F32
                    : token.Text.EndsWith("f64", StringComparison.Ordinal) ? ArithType.F64
                    : expected is { IsFloat: true, IsPending: false } ? expected
                    : null;
                if (type is null)
                {
                    return new BoundLiteralExpression(ArithType.PendingFloat, Value: null, token, negated);
                }

                double magnitude = double.Parse(TrimSuffix(token.Text), CultureInfo.InvariantCulture);
                double value = negated ? -magnitude : magnitude;
                object boxed = type == ArithType.F32 ? (object)(float)value : value;
                return new BoundLiteralExpression(type, boxed, token);
            }

            default:
                throw new UnreachableException($"unhandled literal token {token.Kind}");
        }
    }

    private static string TrimSuffix(string text) =>
        text.Length > 3 && char.IsAsciiLetter(text[^3]) ? text[..^3] : text;

    /// <summary>
    /// Parses an integer literal against a concrete type, applying the
    /// unsigned-magnitude rule for a literal beneath unary `-` (spec §4.2).
    /// </summary>
    private BoundExpression MakeIntegerLiteral(Token token, ArithType type, bool negated, TextSpan span)
    {
        string digits = TrimSuffix(token.Text);
        ulong limit = type == ArithType.I32
            ? negated ? 2_147_483_648UL : 2_147_483_647UL
            : negated ? 9_223_372_036_854_775_808UL : 9_223_372_036_854_775_807UL;
        if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ulong magnitude)
            || magnitude > limit)
        {
            _diagnostics.Report(
                ErrorCodes.IntegerLiteralOutOfRange, span, negated ? "-" + token.Text : token.Text, type);
            return new BoundErrorExpression();
        }

        long value = negated ? unchecked(-(long)magnitude) : (long)magnitude;
        object boxed = type == ArithType.I32 ? (object)(int)value : value;
        return new BoundLiteralExpression(type, boxed, token);
    }

    /// <summary>
    /// Rewrites a pending subtree — literals, folded-sign literals, unary
    /// minus, and arithmetic over them — to the given concrete type. This is
    /// the moment of literal parsing and range checking (design §4.4).
    /// </summary>
    private BoundExpression ResolvePending(BoundExpression bound, ArithType target)
    {
        Debug.Assert(!target.IsPending && !target.IsError, "resolution target must be concrete");
        switch (bound)
        {
            case BoundLiteralExpression { Type.IsPending: true } literal:
                return literal.Type.IsInteger
                    ? MakeIntegerLiteral(literal.Token, target, literal.Negated, literal.Token.Span)
                    : BindLiteral(literal.Token, target, literal.Negated, literal.Token.Span);
            case BoundUnaryExpression { Type.IsPending: true } unary:
            {
                BoundExpression operand = ResolvePending(unary.Operand, target);
                return operand.Type.IsError
                    ? new BoundErrorExpression()
                    : new BoundUnaryExpression(unary.OperatorKind, operand, target);
            }

            case BoundBinaryExpression { Type.IsPending: true } binary:
            {
                BoundExpression left = ResolvePending(binary.Left, target);
                BoundExpression right = ResolvePending(binary.Right, target);
                return left.Type.IsError || right.Type.IsError
                    ? new BoundErrorExpression()
                    : new BoundBinaryExpression(binary.OperatorKind, left, right, target);
            }

            default:
                throw new UnreachableException($"unexpected pending node {bound.GetType().Name}");
        }
    }

    private static string UnescapeString(string text)
    {
        // The lexer only produces well-formed string tokens here: quoted,
        // with valid escapes.
        System.Text.StringBuilder builder = new(text.Length - 2);
        for (int i = 1; i < text.Length - 1; i++)
        {
            char c = text[i];
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            i++;
            builder.Append(text[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => throw new UnreachableException($"unhandled escape '\\{text[i]}'"),
            });
        }

        return builder.ToString();
    }

    // ---- Scopes ----------------------------------------------------------

    private void PushScope() => _scopes.Add(new Dictionary<string, VariableSymbol>(StringComparer.Ordinal));

    private void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    private void DeclareVariable(VariableSymbol variable, Token identifier)
    {
        if (identifier.IsMissing)
        {
            return; // The parser already reported the missing name.
        }

        // Spec §6: no redeclaration in the same scope; inner scopes shadow.
        if (!_scopes[^1].TryAdd(variable.Name, variable))
        {
            _diagnostics.Report(ErrorCodes.NameAlreadyDeclared, identifier.Span, variable.Name);
        }
    }

    private VariableSymbol? LookupVariable(Token identifier)
    {
        if (identifier.IsMissing)
        {
            return null; // Already diagnosed by the parser.
        }

        for (int i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(identifier.Text, out VariableSymbol? variable))
            {
                return variable;
            }
        }

        _diagnostics.Report(ErrorCodes.UndefinedName, identifier.Span, identifier.Text);
        return null;
    }
}
