using System.Diagnostics;

namespace Arith.Compiler.Binding;

/// <summary>
/// The closed set of Arith types, as singletons compared by reference.
///
/// PendingInt and PendingFloat are binder-internal (design §4.4): an
/// unsuffixed numeric literal — or an arithmetic expression built only from
/// them — keeps a pending type until a forcing context resolves it to a
/// concrete type of the same category (or to the category default, i64/f64).
/// Pending types never appear in a finished bound tree.
///
/// Error absorbs any operand and suppresses follow-on diagnostics. Void is
/// the return "type" of a function with no return clause; it is not
/// denotable in Arith source.
/// </summary>
public sealed class ArithType
{
    public static readonly ArithType Bool = new("bool");
    public static readonly ArithType I32 = new("i32", isInteger: true);
    public static readonly ArithType I64 = new("i64", isInteger: true);
    public static readonly ArithType F32 = new("f32", isFloat: true);
    public static readonly ArithType F64 = new("f64", isFloat: true);
#pragma warning disable CA1720 // "String" is the Arith type's own name.
    public static readonly ArithType String = new("string");
#pragma warning restore CA1720
    public static readonly ArithType Void = new("void");
    public static readonly ArithType Error = new("?", isError: true);
    public static readonly ArithType PendingInt = new("{integer}", isInteger: true, isPending: true);
    public static readonly ArithType PendingFloat = new("{float}", isFloat: true, isPending: true);

    private ArithType? _arrayType;

    private ArithType(
        string name, bool isInteger = false, bool isFloat = false, bool isError = false,
        bool isPending = false, ArithType? elementType = null)
    {
        Name = name;
        IsInteger = isInteger;
        IsFloat = isFloat;
        IsError = isError;
        IsPending = isPending;
        ElementType = elementType;
    }

    public string Name { get; }

    /// <summary>The element type when this is an array type; null otherwise.</summary>
    public ArithType? ElementType { get; }

    public bool IsArray => ElementType is not null;

    public bool IsInteger { get; }

    public bool IsFloat { get; }

    public bool IsError { get; }

    public bool IsPending { get; }

    public bool IsNumeric => IsInteger || IsFloat;

    /// <summary>True for the six spec §3 primitive value types.</summary>
    public bool IsPrimitive => !IsArray && !IsError && !IsPending && this != Void;

    /// <summary>
    /// The interned array type `[]this` (spec §3.1). Array types are
    /// structural, and interning keeps type equality a reference
    /// comparison: every request for `[]i64` returns the same instance.
    /// The exchange makes the cache safe under parallel test runs, since
    /// the primitive singletons are shared process-wide.
    /// </summary>
    public ArithType ArrayOf()
    {
        Debug.Assert(!IsError && !IsPending && this != Void, "no arrays of non-value types");
        ArithType? existing = Volatile.Read(ref _arrayType);
        if (existing is not null)
        {
            return existing;
        }

        ArithType created = new("[]" + Name, elementType: this);
        return Interlocked.CompareExchange(ref _arrayType, created, null) ?? created;
    }

    /// <summary>The category default an unforced pending type resolves to (spec §4.2/§4.3).</summary>
    public ArithType DefaultForPending => IsInteger ? I64 : F64;

    /// <summary>True when a pending value of this type may resolve to <paramref name="target"/> (same numeric category, concrete).</summary>
    public bool CanResolveTo(ArithType target) =>
        !target.IsPending && (IsInteger ? target.IsInteger : target.IsFloat);

    public override string ToString() => Name;
}
