namespace Arith.Compiler.Diagnostics;

/// <summary>
/// A diagnostic kind: a stable <c>ARITHxxxx</c> code plus a
/// <see cref="string.Format(IFormatProvider, string, object[])"/> message template.
/// Tests assert codes, not message strings.
/// </summary>
public sealed record DiagnosticDescriptor(
    string Code,
    string MessageTemplate,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error);
