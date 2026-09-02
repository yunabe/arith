using System.Collections.Immutable;

using Arith.Compiler.Syntax;

namespace Arith.Compiler.Binding;

/// <summary>
/// The bound (typed) tree, mirroring semantics rather than syntax: names are
/// resolved to symbols and every expression carries its type (design §3).
/// The emitter consumes only these nodes. Like the syntax tree, consumers
/// switch on concrete node types with an explicit fallback that throws.
/// </summary>
public abstract record BoundNode;

/// <summary>A whole bound program: one body per (uniquely named) function, plus the entry point.</summary>
public sealed record BoundProgram(
    ImmutableArray<BoundFunction> Functions,
    FunctionSymbol? EntryPoint) : BoundNode;

public sealed record BoundFunction(FunctionSymbol Symbol, BoundBlock Body) : BoundNode;

public abstract record BoundStatement : BoundNode;

public sealed record BoundBlock(ImmutableArray<BoundStatement> Statements) : BoundStatement;

public sealed record BoundLetStatement(LocalSymbol Local, BoundExpression Initializer) : BoundStatement;

/// <summary>
/// `x = value;` or a compound form: CompoundOperator is null for plain
/// assignment, otherwise the arithmetic operation to apply between the
/// variable's current value and Value (spec §8.4).
/// </summary>
public sealed record BoundAssignmentStatement(
    VariableSymbol Variable,
    BoundBinaryOperatorKind? CompoundOperator,
    BoundExpression Value) : BoundStatement;

public sealed record BoundExpressionStatement(BoundExpression Expression) : BoundStatement;

public sealed record BoundReturnStatement(BoundExpression? Value) : BoundStatement;

/// <summary>
/// The built-in `print` in statement position (spec §10.1). The argument's
/// type picks the emit lowering (design §4.5).
/// </summary>
public sealed record BoundPrintStatement(BoundExpression Argument) : BoundStatement;

/// <summary>Placeholder for a statement that could not be bound; already diagnosed.</summary>
public sealed record BoundErrorStatement : BoundStatement;

public abstract record BoundExpression(ArithType Type) : BoundNode;

/// <summary>
/// A literal. A resolved literal has its parsed Value (bool, int, long,
/// float, double, or string). While the literal is still pending (design
/// §4.4) Value is null and Token/Negated carry what resolution needs: the
/// raw digits and whether the literal sat directly beneath unary `-` (the
/// unsigned-magnitude rule of spec §4.2).
/// </summary>
public sealed record BoundLiteralExpression(
    ArithType Type,
    object? Value,
    Token Token,
    bool Negated = false) : BoundExpression(Type);

public sealed record BoundVariableExpression(VariableSymbol Variable) : BoundExpression(Variable.Type);

public sealed record BoundUnaryExpression(
    BoundUnaryOperatorKind OperatorKind,
    BoundExpression Operand,
    ArithType Type) : BoundExpression(Type);

public sealed record BoundBinaryExpression(
    BoundBinaryOperatorKind OperatorKind,
    BoundExpression Left,
    BoundExpression Right,
    ArithType Type) : BoundExpression(Type);

public sealed record BoundCallExpression(
    FunctionSymbol Function,
    ImmutableArray<BoundExpression> Arguments) : BoundExpression(Function.ReturnType);

/// <summary>Placeholder for an expression that could not be bound; its Error type suppresses cascades.</summary>
public sealed record BoundErrorExpression() : BoundExpression(ArithType.Error);

public enum BoundUnaryOperatorKind
{
    Negation,
}

public enum BoundBinaryOperatorKind
{
    Addition,
    Subtraction,
    Multiplication,
    Division,
    Remainder,
}
