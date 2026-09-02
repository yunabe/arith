using System.Collections.Immutable;

using Arith.Compiler.Text;

namespace Arith.Compiler.Syntax;

public abstract record StatementSyntax(TextSpan Span) : SyntaxNode(Span);

/// <summary>
/// A brace-enclosed statement list. Grammatically blocks only appear as
/// function and control-flow bodies, but deriving StatementSyntax lets an
/// if-statement's else clause hold either a block or a nested if directly.
/// </summary>
public sealed record BlockSyntax(
    ImmutableArray<StatementSyntax> Statements,
    TextSpan Span) : StatementSyntax(Span);

/// <summary><c>let name [: type] = initializer;</c>. A null Type means the type is inferred.</summary>
public sealed record LetStatementSyntax(
    Token Identifier,
    TypeSyntax? Type,
    ExpressionSyntax Initializer,
    TextSpan Span) : StatementSyntax(Span);

/// <summary><c>name op value;</c> where op is <c>=</c> or a compound-assignment operator.</summary>
public sealed record AssignmentStatementSyntax(
    Token Identifier,
    Token OperatorToken,
    ExpressionSyntax Value,
    TextSpan Span) : StatementSyntax(Span);

/// <summary>
/// An expression used as a statement. The grammar only allows call
/// expressions here (spec §12); the parser reports ARITH2002 for anything
/// else but still keeps the expression, so the binder can check it.
/// </summary>
public sealed record ExpressionStatementSyntax(
    ExpressionSyntax Expression,
    TextSpan Span) : StatementSyntax(Span);

/// <summary><c>return;</c> or <c>return value;</c>.</summary>
public sealed record ReturnStatementSyntax(
    ExpressionSyntax? Value,
    TextSpan Span) : StatementSyntax(Span);

/// <summary><c>if cond block [else (if | block)]</c>. Else is an IfStatementSyntax or a BlockSyntax.</summary>
public sealed record IfStatementSyntax(
    ExpressionSyntax Condition,
    BlockSyntax Then,
    StatementSyntax? Else,
    TextSpan Span) : StatementSyntax(Span);

/// <summary><c>while cond block</c>.</summary>
public sealed record WhileStatementSyntax(
    ExpressionSyntax Condition,
    BlockSyntax Body,
    TextSpan Span) : StatementSyntax(Span);

/// <summary>
/// <c>for name in start..end block</c>. RangeOperator distinguishes the
/// half-open <c>..</c> from the closed <c>..=</c> form.
/// </summary>
public sealed record ForStatementSyntax(
    Token Identifier,
    ExpressionSyntax Start,
    Token RangeOperator,
    ExpressionSyntax End,
    BlockSyntax Body,
    TextSpan Span) : StatementSyntax(Span);

/// <summary><c>break;</c>.</summary>
public sealed record BreakStatementSyntax(TextSpan Span) : StatementSyntax(Span);

/// <summary><c>continue;</c>.</summary>
public sealed record ContinueStatementSyntax(TextSpan Span) : StatementSyntax(Span);

/// <summary>
/// Placeholder for source the parser had to skip. It keeps the tree complete
/// so later stages need no null handling; the diagnostic was already
/// reported when this node was created.
/// </summary>
public sealed record ErrorStatementSyntax(TextSpan Span) : StatementSyntax(Span);
