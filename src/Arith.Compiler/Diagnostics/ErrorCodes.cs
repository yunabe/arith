namespace Arith.Compiler.Diagnostics;

/// <summary>
/// The registry of every diagnostic the compiler can report. Codes are grouped
/// by stage — ARITH1xxx lexical, ARITH2xxx syntactic, ARITH3xxx semantic,
/// ARITH4xxx target limits found during emission — and stay stable once
/// released.
/// </summary>
public static class ErrorCodes
{
    // Lexical errors (ARITH1xxx).
    public static readonly DiagnosticDescriptor UnexpectedCharacter =
        new("ARITH1001", "unexpected character '{0}'");

    public static readonly DiagnosticDescriptor UnterminatedStringLiteral =
        new("ARITH1002", "unterminated string literal");

    public static readonly DiagnosticDescriptor InvalidEscapeSequence =
        new("ARITH1003", "invalid escape sequence '{0}'");

    public static readonly DiagnosticDescriptor UnterminatedBlockComment =
        new("ARITH1004", "unterminated block comment");

    public static readonly DiagnosticDescriptor InvalidNumericSuffix =
        new("ARITH1005", "invalid suffix '{0}' on numeric literal");

    public static readonly DiagnosticDescriptor BareDollarInInterpolatedString =
        new("ARITH1006", "a '$' in an interpolated string must start an interpolation hole or be escaped as '\\$'");

    // Syntax errors (ARITH2xxx).
    public static readonly DiagnosticDescriptor UnexpectedToken =
        new("ARITH2001", "unexpected {0}, expected {1}");

    public static readonly DiagnosticDescriptor NonCallExpressionStatement =
        new("ARITH2002", "only a call expression can be used as a statement");

    public static readonly DiagnosticDescriptor TrailingComma =
        new("ARITH2003", "trailing comma is not allowed");

    public static readonly DiagnosticDescriptor InvalidAssignmentTarget =
        new("ARITH2004", "the target of an assignment must be a variable or an array element");

    // Semantic errors (ARITH3xxx).
    public static readonly DiagnosticDescriptor DuplicateFunction =
        new("ARITH3001", "function '{0}' is already declared");

    public static readonly DiagnosticDescriptor BuiltinRedeclared =
        new("ARITH3002", "'{0}' is a built-in function and cannot be redeclared");

    public static readonly DiagnosticDescriptor MissingEntryPoint =
        new("ARITH3003", "program must contain a 'main' function");

    public static readonly DiagnosticDescriptor InvalidEntryPointSignature =
        new("ARITH3004", "'main' must return no value or i32");

    public static readonly DiagnosticDescriptor UndefinedName =
        new("ARITH3005", "'{0}' is not defined");

    public static readonly DiagnosticDescriptor UndefinedFunction =
        new("ARITH3006", "function '{0}' is not defined");

    // ARITH3007 was never assigned and stays reserved: codes are stable once
    // released, so the gap is kept rather than renumbering the rest.

    public static readonly DiagnosticDescriptor WrongArgumentCount =
        new("ARITH3008", "function '{0}' takes {1} argument(s) but was given {2}");

    public static readonly DiagnosticDescriptor TypeMismatch =
        new("ARITH3009", "expected type '{0}' but found '{1}'");

    public static readonly DiagnosticDescriptor InvalidBinaryOperator =
        new("ARITH3010", "operator '{0}' cannot be applied to operands of type '{1}' and '{2}'");

    public static readonly DiagnosticDescriptor InvalidUnaryOperator =
        new("ARITH3011", "operator '{0}' cannot be applied to an operand of type '{1}'");

    public static readonly DiagnosticDescriptor IntegerLiteralOutOfRange =
        new("ARITH3012", "integer literal '{0}' is out of range for type '{1}'");

    public static readonly DiagnosticDescriptor NameAlreadyDeclared =
        new("ARITH3013", "'{0}' is already declared in this scope");

    public static readonly DiagnosticDescriptor ReturnValueInVoidFunction =
        new("ARITH3014", "cannot return a value from a function with no return type");

    public static readonly DiagnosticDescriptor MissingReturnValue =
        new("ARITH3015", "function must return a value of type '{0}'");

    public static readonly DiagnosticDescriptor NotAllPathsReturn =
        new("ARITH3016", "not every path through '{0}' returns a value");

    public static readonly DiagnosticDescriptor ExpressionHasNoValue =
        new("ARITH3017", "expression does not produce a value");

    public static readonly DiagnosticDescriptor LoopVariableReassigned =
        new("ARITH3018", "loop variable '{0}' cannot be reassigned");

    public static readonly DiagnosticDescriptor BreakOrContinueOutsideLoop =
        new("ARITH3019", "'{0}' can only be used inside a loop");

    public static readonly DiagnosticDescriptor InvalidConversion =
        new("ARITH3020", "cannot convert from '{0}' to '{1}'");

    public static readonly DiagnosticDescriptor EmptyArrayLiteralNeedsType =
        new("ARITH3021", "an empty array literal requires an expected array type");

    public static readonly DiagnosticDescriptor NotIndexable =
        new("ARITH3022", "a value of type '{0}' cannot be indexed");

    public static readonly DiagnosticDescriptor LenRequiresArray =
        new("ARITH3023", "'len' requires an array argument but was given '{0}'");

    public static readonly DiagnosticDescriptor PrintRequiresPrimitive =
        new("ARITH3024", "'print' does not accept a value of type '{0}'");

    public static readonly DiagnosticDescriptor InvalidEntryPointParameter =
        new("ARITH3025", "'main' cannot declare a parameter of type '{0}'");

    public static readonly DiagnosticDescriptor NotIterable =
        new("ARITH3026", "'for' cannot iterate over a value of type '{0}'");

    public static readonly DiagnosticDescriptor EntryPointArgsMustBeAlone =
        new("ARITH3027", "'main' cannot combine a '[]string' parameter with other parameters");

    // Target limits (ARITH4xxx): the program is valid Arith, but the .NET
    // runtime could not run what the emitter would produce for it.
    public static readonly DiagnosticDescriptor TooManyLocals =
        new("ARITH4001", "function '{0}' needs {1} local variable slots (including compiler temporaries), but a .NET method can have at most {2}");
}
