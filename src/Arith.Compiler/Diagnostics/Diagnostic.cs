using System.Globalization;

using Arith.Compiler.Text;

namespace Arith.Compiler.Diagnostics;

/// <summary>One reported problem: severity, stable code, message, and source span.</summary>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, TextSpan Span)
{
    public static Diagnostic Create(DiagnosticDescriptor descriptor, TextSpan span, params object[] args)
    {
        string message = string.Format(CultureInfo.InvariantCulture, descriptor.MessageTemplate, args);
        return new Diagnostic(descriptor.Severity, descriptor.Code, message, span);
    }

    public override string ToString() => $"{Code}: {Message} at {Span}";
}
