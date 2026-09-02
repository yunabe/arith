namespace Arith.Compiler.Diagnostics;

/// <summary>
/// The registry of every diagnostic the compiler can report. Codes are grouped
/// by stage — ARITH1xxx lexical, ARITH2xxx syntactic, ARITH3xxx semantic — and
/// stay stable once released.
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

    // Syntax errors (ARITH2xxx).
    public static readonly DiagnosticDescriptor UnexpectedToken =
        new("ARITH2001", "unexpected {0}, expected {1}");

    public static readonly DiagnosticDescriptor NonCallExpressionStatement =
        new("ARITH2002", "only a call expression can be used as a statement");

    public static readonly DiagnosticDescriptor TrailingComma =
        new("ARITH2003", "trailing comma is not allowed");

    // Semantic errors (ARITH3xxx).
    public static readonly DiagnosticDescriptor DuplicateFunction =
        new("ARITH3001", "function '{0}' is already declared");

    public static readonly DiagnosticDescriptor PrintRedeclared =
        new("ARITH3002", "'print' is a built-in function and cannot be redeclared");

    public static readonly DiagnosticDescriptor MissingEntryPoint =
        new("ARITH3003", "program must contain a 'main' function");

    public static readonly DiagnosticDescriptor InvalidEntryPointSignature =
        new("ARITH3004", "'main' must take no parameters and return no value or i32");

    public static readonly DiagnosticDescriptor UndefinedName =
        new("ARITH3005", "'{0}' is not defined");

    public static readonly DiagnosticDescriptor UndefinedFunction =
        new("ARITH3006", "function '{0}' is not defined");

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

    public static readonly DiagnosticDescriptor ExpressionHasNoValue =
        new("ARITH3017", "expression does not produce a value");

    // Temporary code for constructs the staged implementation has not
    // reached yet (design §6); every use disappears by the end of step 7.
    public static readonly DiagnosticDescriptor NotYetImplemented =
        new("ARITH3901", "{0} is not implemented yet");
}
