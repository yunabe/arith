using System.Collections.Immutable;

using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Binding;

/// <summary>
/// The bound (typed) tree, mirroring semantics rather than syntax: names are
/// resolved to symbols and every expression carries its type (design §3).
/// The emitter consumes only these nodes. Like the syntax tree, consumers
/// switch on concrete node types with an explicit fallback that throws.
/// </summary>
public abstract record BoundNode
{
    /// <summary>
    /// The originating source range; null for compiler-generated nodes.
    /// Containers retain ranges for uniformity, even when they emit no instructions.
    /// </summary>
    public TextSpan? Span { get; init; }
}

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

/// <summary>
/// `array[index] = value;` or a compound form (spec §8.4). Array and Index
/// are evaluated once, before Value; CompoundOperator is null for plain
/// assignment, otherwise the operation applied between the element's current
/// value and Value.
/// </summary>
public sealed record BoundElementAssignmentStatement(
    BoundExpression Array,
    BoundExpression Index,
    ArithType ElementType,
    BoundBinaryOperatorKind? CompoundOperator,
    BoundExpression Value) : BoundStatement;

public sealed record BoundExpressionStatement(BoundExpression Expression) : BoundStatement;

public sealed record BoundReturnStatement(BoundExpression? Value) : BoundStatement;

/// <summary>
/// The built-in `print` in statement position (spec §10.1). The argument's
/// type picks the emit lowering (design §4.5).
/// </summary>
public sealed record BoundPrintStatement(BoundExpression Argument) : BoundStatement;

/// <summary>`if cond { then } [else …]`; Else is a BoundBlock or a nested BoundIfStatement.</summary>
public sealed record BoundIfStatement(
    BoundExpression Condition,
    BoundBlock Then,
    BoundStatement? Else) : BoundStatement;

public sealed record BoundWhileStatement(
    BoundExpression Condition,
    BoundBlock Body) : BoundStatement;

/// <summary>
/// `for variable in start..end { body }` (IsInclusive selects `..=`). Start
/// and End are i64 and evaluated once, left to right; the emitter lowers
/// the two forms with the overflow-safe shapes of design §4.5.
/// </summary>
public sealed record BoundForStatement(
    LocalSymbol Variable,
    BoundExpression Start,
    BoundExpression End,
    bool IsInclusive,
    BoundBlock Body) : BoundStatement;

/// <summary>
/// `for variable in array { body }` (spec §9.3). The array is evaluated
/// once; each iteration reads the element at the current index when it
/// starts, so element writes are visible to later iterations. Variable has
/// the array's element type and is read-only.
/// </summary>
public sealed record BoundForEachStatement(
    LocalSymbol Variable,
    BoundExpression Array,
    BoundBlock Body) : BoundStatement;

public sealed record BoundBreakStatement : BoundStatement;

public sealed record BoundContinueStatement : BoundStatement;

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

/// <summary>
/// An explicit conversion `type(operand)` (spec §7). The operand was typed
/// independently — the target provides no expected type — and the pair was
/// validated; narrowing integer and float-to-integer conversions fault at
/// runtime when out of range.
/// </summary>
public sealed record BoundConversionExpression(
    ArithType Type,
    BoundExpression Operand) : BoundExpression(Type);

/// <summary>`[e1, e2, …]` (spec §4.5). Type is the array type; Elements match its element type.</summary>
public sealed record BoundArrayLiteralExpression(
    ArithType Type,
    ImmutableArray<BoundExpression> Elements) : BoundExpression(Type);

/// <summary>
/// `[value; count]` (spec §4.5). Value is evaluated once and shared by every
/// slot — significant when the element is itself an array reference — and
/// Count (i64) is evaluated after it; a negative count faults at runtime.
/// </summary>
public sealed record BoundArrayRepeatExpression(
    ArithType Type,
    BoundExpression Value,
    BoundExpression Count) : BoundExpression(Type);

/// <summary>An element read `array[index]` (spec §8.6). Type is the array's element type; Index is i64.</summary>
public sealed record BoundIndexExpression(
    BoundExpression Array,
    BoundExpression Index,
    ArithType Type) : BoundExpression(Type);

/// <summary>The built-in `len(array)` (spec §10.2), an i64-valued expression.</summary>
public sealed record BoundLenExpression(BoundExpression Array) : BoundExpression(ArithType.I64);

/// <summary>Placeholder for an expression that could not be bound; its Error type suppresses cascades.</summary>
public sealed record BoundErrorExpression() : BoundExpression(ArithType.Error);

public enum BoundUnaryOperatorKind
{
    Negation,
    LogicalNegation,
}

public enum BoundBinaryOperatorKind
{
    // Arithmetic: numeric operands, result of the operand type.
    Addition,
    Subtraction,
    Multiplication,
    Division,
    Remainder,

    // Comparison: numeric operands, bool result.
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,

    // Equality: numeric, bool, or string operands; bool result.
    Equals,
    NotEquals,

    // Logical: bool operands, bool result, short-circuit evaluation.
    LogicalAnd,
    LogicalOr,
}
