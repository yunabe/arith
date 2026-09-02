using Arith.Compiler.Diagnostics;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

public sealed class DiagnosticBagTests
{
    [Fact]
    public void Report_FormatsMessageTemplateWithArguments()
    {
        DiagnosticBag bag = new();

        bag.Report(ErrorCodes.UnexpectedCharacter, new TextSpan(3, 1), "@");

        Diagnostic diagnostic = Assert.Single(bag);
        Assert.Equal("ARITH1001", diagnostic.Code);
        Assert.Equal("unexpected character '@'", diagnostic.Message);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(new TextSpan(3, 1), diagnostic.Span);
    }

    [Fact]
    public void HasErrors_EmptyBag_IsFalse()
    {
        DiagnosticBag bag = new();

        Assert.False(bag.HasErrors);
    }

    [Fact]
    public void HasErrors_AfterErrorReport_IsTrue()
    {
        DiagnosticBag bag = new();

        bag.Report(ErrorCodes.UnterminatedStringLiteral, new TextSpan(0, 5));

        Assert.True(bag.HasErrors);
    }
}
