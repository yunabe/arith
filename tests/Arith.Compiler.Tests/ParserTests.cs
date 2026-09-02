using Arith.Compiler.Diagnostics;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

public sealed class ParserTests
{
    private static SyntaxTree Parse(string source) => SyntaxTree.Parse(SourceText.From(source));

    /// <summary>Parses source expected to be error-free and dumps the whole tree.</summary>
    private static string DumpProgram(string source)
    {
        SyntaxTree tree = Parse(source);
        Assert.Empty(tree.Diagnostics);
        return SyntaxDumper.Dump(tree.Root);
    }

    /// <summary>Parses a single statement inside a wrapper function and dumps it.</summary>
    private static string DumpStatement(string statement)
    {
        SyntaxTree tree = Parse($"fn test() {{ {statement} }}");
        Assert.Empty(tree.Diagnostics);
        StatementSyntax single = Assert.Single(Assert.Single(tree.Root.Functions).Body.Statements);
        return SyntaxDumper.Dump(single);
    }

    /// <summary>Parses an expression via a let initializer and dumps it.</summary>
    private static string DumpExpression(string expression)
    {
        SyntaxTree tree = Parse($"fn test() {{ let value = {expression}; }}");
        Assert.Empty(tree.Diagnostics);
        LetStatementSyntax let = Assert.IsType<LetStatementSyntax>(
            Assert.Single(Assert.Single(tree.Root.Functions).Body.Statements));
        return SyntaxDumper.Dump(let.Initializer);
    }

    [Theory]
    [InlineData("42", "42")]
    [InlineData("x", "x")]
    [InlineData("true", "true")]
    [InlineData("\"hi\"", "\"hi\"")]
    [InlineData("1 + 2 * 3", "(+ 1 (* 2 3))")]
    [InlineData("1 * 2 + 3", "(+ (* 1 2) 3)")]
    [InlineData("1 - 2 - 3", "(- (- 1 2) 3)")]
    [InlineData("10 / 5 / 2", "(/ (/ 10 5) 2)")]
    [InlineData("(1 + 2) * 3", "(* (paren (+ 1 2)) 3)")]
    [InlineData("-2 * 3", "(* (- 2) 3)")]
    [InlineData("-(2 * 3)", "(- (paren (* 2 3)))")]
    [InlineData("-9223372036854775808", "(- 9223372036854775808)")]
    [InlineData("!a && b || c", "(|| (&& (! a) b) c)")]
    [InlineData("a || b && c", "(|| a (&& b c))")]
    [InlineData("a < b == c > d", "(== (< a b) (> c d))")]
    [InlineData("a == b && c != d", "(&& (== a b) (!= c d))")]
    [InlineData("a + b <= c % d", "(<= (+ a b) (% c d))")]
    [InlineData("f()", "(call f)")]
    [InlineData("f(1, x + 2)", "(call f 1 (+ x 2))")]
    [InlineData("f(g(x))", "(call f (call g x))")]
    [InlineData("i64(small)", "(call i64 small)")]
    [InlineData("f64(large) / 4.0", "(/ (call f64 large) 4.0)")]
    [InlineData("\"answer=\" + string(a)", "(+ \"answer=\" (call string a))")]
    public void ParseExpression_ProducesExpectedShape(string expression, string expected)
    {
        Assert.Equal(expected, DumpExpression(expression));
    }

    [Theory]
    [InlineData("let x = 1;", "(let x 1)")]
    [InlineData("let limit: i32 = 10;", "(let limit : i32 10)")]
    [InlineData("x = 1;", "(= x 1)")]
    [InlineData("total += i;", "(+= total i)")]
    [InlineData("x -= 1;", "(-= x 1)")]
    [InlineData("x *= 2;", "(*= x 2)")]
    [InlineData("x /= 2;", "(/= x 2)")]
    [InlineData("x %= 2;", "(%= x 2)")]
    [InlineData("print(x);", "(expr (call print x))")]
    [InlineData("string(1);", "(expr (call string 1))")]
    [InlineData("return;", "(return)")]
    [InlineData("return 0;", "(return 0)")]
    [InlineData("if a { }", "(if a (block))")]
    [InlineData("if a { } else { }", "(if a (block) (block))")]
    [InlineData(
        "if a { } else if b { } else { }",
        "(if a (block) (if b (block) (block)))")]
    [InlineData("while i < 10 { i += 1; }", "(while (< i 10) (block (+= i 1)))")]
    [InlineData("while a { break; }", "(while a (block (break)))")]
    [InlineData("while a { continue; }", "(while a (block (continue)))")]
    [InlineData("for i in 0..10 { print(i); }", "(for i .. 0 10 (block (expr (call print i))))")]
    [InlineData("for i in 0..=10 { }", "(for i ..= 0 10 (block))")]
    [InlineData("for i in a + 1..b - 1 { }", "(for i .. (+ a 1) (- b 1) (block))")]
    public void ParseStatement_ProducesExpectedShape(string statement, string expected)
    {
        Assert.Equal(expected, DumpStatement(statement));
    }

    [Theory]
    [InlineData("fn greet() { }", "(fn greet (block))")]
    [InlineData(
        "fn add(a: i64, b: i64) -> i64 { return a + b; }",
        "(fn add (param a i64) (param b i64) -> i64 (block (return (+ a b))))")]
    [InlineData(
        "fn main() -> i32 { return 0; }",
        "(fn main -> i32 (block (return 0)))")]
    public void ParseFunctionDeclaration_ProducesExpectedShape(string source, string expected)
    {
        Assert.Equal(expected, DumpProgram(source));
    }

    [Fact]
    public void Parse_SpecExampleProgram_ProducesNoDiagnostics()
    {
        const string source = """
            fn sum_range(start: i64, end: i64) -> i64 {
                let total = 0;

                for i in start..end {
                    total += i;
                }

                return total;
            }

            fn main() -> i32 {
                let result = sum_range(1, 11);

                if result > 50 {
                    print("large:");
                    print(result);
                } else {
                    print("small:");
                    print(result);
                }

                return 0;
            }
            """;

        SyntaxTree tree = Parse(source);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(2, tree.Root.Functions.Length);
        Assert.Equal(
            "(fn sum_range (param start i64) (param end i64) -> i64 "
            + "(block (let total 0) (for i .. start end (block (+= total i))) (return total)))",
            SyntaxDumper.Dump(tree.Root.Functions[0]));
    }

    [Fact]
    public void Parse_NodeSpans_CoverTheirSourceText()
    {
        //                     0         1         2
        //                     0123456789012345678901234
        const string source = "fn main() { return 10; }";

        SyntaxTree tree = Parse(source);

        Assert.Empty(tree.Diagnostics);
        FunctionDeclarationSyntax function = Assert.Single(tree.Root.Functions);
        Assert.Equal(TextSpan.FromBounds(0, source.Length), function.Span);
        ReturnStatementSyntax ret =
            Assert.IsType<ReturnStatementSyntax>(Assert.Single(function.Body.Statements));
        Assert.Equal("return 10;", tree.Text.ToString(ret.Span));
        Assert.Equal("10", tree.Text.ToString(ret.Value!.Span));
    }

    [Fact]
    public void Parse_MissingSemicolon_ReportsAndRecoversAtNextStatement()
    {
        SyntaxTree tree = Parse("fn test() { let x = 1 let y = 2; }");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("ARITH2001", diagnostic.Code);
        BlockSyntax body = Assert.Single(tree.Root.Functions).Body;
        Assert.Equal(2, body.Statements.Length);
        Assert.Equal("(let x 1)", SyntaxDumper.Dump(body.Statements[0]));
        Assert.Equal("(let y 2)", SyntaxDumper.Dump(body.Statements[1]));
    }

    [Theory]
    [InlineData("fn t() { let x; }")]      // Spec §6: an initializer is required.
    [InlineData("fn t() { let a = +1; }")] // Spec §8.1: unary `+` does not exist.
    public void SpecForbiddenForm_ReportsASyntaxError(string source)
    {
        SyntaxTree tree = Parse(source);

        Assert.Contains(tree.Diagnostics, d => d.Code == "ARITH2001");
    }

    [Fact]
    public void Parse_NonCallExpressionStatement_ReportsArith2002()
    {
        SyntaxTree tree = Parse("fn test() { 1 + 2; }");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("ARITH2002", diagnostic.Code);
        Assert.Equal("1 + 2", tree.Text.ToString(diagnostic.Span));
    }

    [Fact]
    public void Parse_MultipleErrors_AllReportedAndTreeStaysComplete()
    {
        const string source = """
            fn test() {
                let x = ;
                1 + 2;
                let y = 5
            }
            """;

        SyntaxTree tree = Parse(source);

        string[] expectedCodes = ["ARITH2001", "ARITH2002", "ARITH2001"];
        Assert.Equal(expectedCodes, tree.Diagnostics.Select(d => d.Code));
        BlockSyntax body = Assert.Single(tree.Root.Functions).Body;
        Assert.Collection(
            body.Statements,
            s => Assert.Equal("(let x (error))", SyntaxDumper.Dump(s)),
            s => Assert.Equal("(expr (+ 1 2))", SyntaxDumper.Dump(s)),
            s => Assert.Equal("(let y 5)", SyntaxDumper.Dump(s)));
    }

    [Fact]
    public void Parse_TopLevelNonFunction_ReportsAndSkipsToNextFunction()
    {
        SyntaxTree tree = Parse("let x = 1; fn main() { }");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("ARITH2001", diagnostic.Code);
        FunctionDeclarationSyntax function = Assert.Single(tree.Root.Functions);
        Assert.Equal("main", function.Identifier.Text);
    }

    [Fact]
    public void Parse_UnparsableStatementStart_SkipsToNextStatement()
    {
        SyntaxTree tree = Parse("fn test() { ) ) let x = 1; }");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("ARITH2001", diagnostic.Code);
        BlockSyntax body = Assert.Single(tree.Root.Functions).Body;
        Assert.Equal(2, body.Statements.Length);
        Assert.IsType<ErrorStatementSyntax>(body.Statements[0]);
        Assert.Equal("(let x 1)", SyntaxDumper.Dump(body.Statements[1]));
    }

    [Fact]
    public void Parse_BadTokenStatement_ReportsOnlyTheLexerDiagnostic()
    {
        SyntaxTree tree = Parse("fn test() { @; let x = 1; }");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("ARITH1001", diagnostic.Code);
        BlockSyntax body = Assert.Single(tree.Root.Functions).Body;
        Assert.Equal(2, body.Statements.Length);
        Assert.IsType<ErrorStatementSyntax>(body.Statements[0]);
    }

    [Fact]
    public void Parse_UnterminatedBlock_ReportsMissingBraceAndKeepsStatements()
    {
        SyntaxTree tree = Parse("fn test() { let x = 1;");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("ARITH2001", diagnostic.Code);
        BlockSyntax body = Assert.Single(tree.Root.Functions).Body;
        Assert.Equal("(let x 1)", SyntaxDumper.Dump(Assert.Single(body.Statements)));
    }

    [Fact]
    public void Parse_MissingParameterType_ReportsAndParsesRemainingParameters()
    {
        SyntaxTree tree = Parse("fn test(a: , b: i64) { }");

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("ARITH2001", diagnostic.Code);
        FunctionDeclarationSyntax function = Assert.Single(tree.Root.Functions);
        Assert.Equal(2, function.Parameters.Length);
        Assert.True(function.Parameters[0].Type.Keyword.IsMissing);
        Assert.Equal("i64", function.Parameters[1].Type.Keyword.Text);
    }
}
