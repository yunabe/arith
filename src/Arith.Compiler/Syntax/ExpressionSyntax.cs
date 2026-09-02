using System.Collections.Immutable;

using Arith.Compiler.Text;

namespace Arith.Compiler.Syntax;

public abstract record ExpressionSyntax(TextSpan Span) : SyntaxNode(Span);

/// <summary>A literal: integer, float, or string literal token, or <c>true</c>/<c>false</c>.</summary>
public sealed record LiteralExpressionSyntax(
    Token LiteralToken,
    TextSpan Span) : ExpressionSyntax(Span);

/// <summary>A variable or parameter reference.</summary>
public sealed record NameExpressionSyntax(
    Token Identifier,
    TextSpan Span) : ExpressionSyntax(Span);

/// <summary>
/// <c>callee(args)</c>. The callee token is an identifier (a function call)
/// or a type keyword (an explicit conversion, spec §7); the binder tells the
/// two apart.
/// </summary>
public sealed record CallExpressionSyntax(
    Token Callee,
    ImmutableArray<ExpressionSyntax> Arguments,
    TextSpan Span) : ExpressionSyntax(Span);

/// <summary>Unary <c>-</c> or <c>!</c> applied to an operand.</summary>
public sealed record UnaryExpressionSyntax(
    Token OperatorToken,
    ExpressionSyntax Operand,
    TextSpan Span) : ExpressionSyntax(Span);

/// <summary>A binary operation; the operator token's kind selects it.</summary>
public sealed record BinaryExpressionSyntax(
    ExpressionSyntax Left,
    Token OperatorToken,
    ExpressionSyntax Right,
    TextSpan Span) : ExpressionSyntax(Span);

/// <summary><c>(expression)</c>. Kept as a node so tests can assert grouping.</summary>
public sealed record ParenthesizedExpressionSyntax(
    ExpressionSyntax Expression,
    TextSpan Span) : ExpressionSyntax(Span);

/// <summary>
/// Placeholder where the grammar required an expression and none could be
/// parsed. Binds to the Error type, which suppresses cascading diagnostics.
/// </summary>
public sealed record ErrorExpressionSyntax(TextSpan Span) : ExpressionSyntax(Span);
