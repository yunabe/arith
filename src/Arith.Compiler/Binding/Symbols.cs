using System.Collections.Immutable;

namespace Arith.Compiler.Binding;

/// <summary>A named entity produced by binding. Symbols are compared by reference.</summary>
public abstract class Symbol
{
    private protected Symbol(string name) => Name = name;

    public string Name { get; }

    public override string ToString() => Name;
}

/// <summary>A variable: a function parameter or a local declared with let.</summary>
public abstract class VariableSymbol : Symbol
{
    private protected VariableSymbol(string name, ArithType type)
        : base(name) => Type = type;

    public ArithType Type { get; }
}

public sealed class ParameterSymbol(string name, ArithType type, int index) : VariableSymbol(name, type)
{
    /// <summary>Zero-based position in the parameter list (the emitter's argument slot).</summary>
    public int Index { get; } = index;
}

public sealed class LocalSymbol(string name, ArithType type, bool isReadOnly = false) : VariableSymbol(name, type)
{
    /// <summary>True for a range-for loop variable, which cannot be reassigned (spec §9.3).</summary>
    public bool IsReadOnly { get; } = isReadOnly;
}

/// <summary>A top-level function. ReturnType is Void for a function with no `-&gt;` clause.</summary>
public sealed class FunctionSymbol(string name, ImmutableArray<ParameterSymbol> parameters, ArithType returnType)
    : Symbol(name)
{
    public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters;

    public ArithType ReturnType { get; } = returnType;
}
