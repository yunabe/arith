using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Arith.Compiler.Diagnostics;

namespace Arith.Compiler.Tests;

/// <summary>
/// Keeps docs/diagnostics.md and the ErrorCodes registry in sync: every
/// registered code must be documented with its exact message template, and
/// every documented code must exist (or be explicitly marked reserved).
/// </summary>
public sealed partial class DiagnosticsReferenceTests
{
    [GeneratedRegex(@"^\| (ARITH\d{4}) \| (.*?) \|", RegexOptions.Multiline)]
    private static partial Regex TableRow();

    private static string ReferencePath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "docs", "diagnostics.md"));

    private static Dictionary<string, DiagnosticDescriptor> Registry() =>
        typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (DiagnosticDescriptor)field.GetValue(null)!)
            .ToDictionary(descriptor => descriptor.Code);

    private static Dictionary<string, string> DocumentedRows() =>
        TableRow().Matches(File.ReadAllText(ReferencePath()))
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);

    [Fact]
    public void EveryRegisteredCode_IsDocumentedWithItsMessageTemplate()
    {
        Dictionary<string, string> rows = DocumentedRows();

        foreach ((string code, DiagnosticDescriptor descriptor) in Registry())
        {
            Assert.True(rows.ContainsKey(code), $"{code} is missing from docs/diagnostics.md");
            Assert.Equal($"`{descriptor.MessageTemplate}`", rows[code]);
        }
    }

    [Fact]
    public void EveryDocumentedCode_IsRegisteredOrMarkedReserved()
    {
        Dictionary<string, DiagnosticDescriptor> registry = Registry();

        foreach ((string code, string message) in DocumentedRows())
        {
            Assert.True(
                registry.ContainsKey(code) || message.Contains("reserved", StringComparison.Ordinal),
                $"{code} is documented but not registered");
        }
    }
}
