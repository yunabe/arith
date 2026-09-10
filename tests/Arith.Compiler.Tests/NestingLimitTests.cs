using Arith.Compiler.Binding;
using Arith.Compiler.Diagnostics;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

/// <summary>
/// The nesting limit (<see cref="SyntaxFacts.MaxNestingDepth"/>, design
/// §4.3) and the iterative left-spine walks that keep flat operator chains
/// off it — issue #34: a flat 15,000-term sum overflowed the binder's stack.
/// Recovery trees past the limit are pinned the way
/// ParserRecoveryScenarioTests pins them. The crash shapes also run as
/// separate processes in Arith.Cli.Tests, so a regression cannot take this
/// runner down with it.
/// </summary>
public sealed class NestingLimitTests
{
    private const int Max = SyntaxFacts.MaxNestingDepth;

    /// <summary>
    /// Levels `fn main() { print(…); }` spends around its argument: the
    /// statement, the call expression, and the argument expression.
    /// </summary>
    private const int PrintArgumentDepth = 3;

    /// <summary>Long enough that one stack frame per operator would overflow any default stack.</summary>
    private const int ChainLength = 20_000;

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    private static string Chain(string term, string op, int count = ChainLength) =>
        string.Join($" {op} ", Enumerable.Repeat(term, count));

    private static string InMain(string body) => $"fn main() {{ {body} }}";

    private static string Printing(string expression) => InMain($"print({expression});");

    private static SyntaxTree Parse(string source) => SyntaxTree.Parse(SourceText.From(source));

    private static Compilation Compile(string source) => Compilation.Create(Parse(source));

    private static string[] Codes(IEnumerable<Diagnostic> diagnostics) => [.. diagnostics.Select(d => d.Code)];

    private static (string[] Codes, string Dump) CompileScenario(string source)
    {
        Compilation compilation = Compile(source);
        return (Codes(compilation.Diagnostics), SyntaxDumper.Dump(compilation.SyntaxTree.Root));
    }

    /// <summary>Compiles error-free, emits in both modes with verified IL, and returns main's body.</summary>
    private static BoundBlock CompileAndEmit(string source)
    {
        Compilation compilation = Compile(source);
        Assert.Empty(compilation.Diagnostics);
        foreach (bool debug in new[] { false, true })
        {
            EmitResult result = compilation.Emit("test", debug);
            Assert.True(result.Success);
            IlVerification.AssertValid(result.PeImage);
        }

        return compilation.Program.Functions.Single(f => f.Symbol.Name == "main").Body;
    }

    private static string LetType(BoundBlock body) =>
        Assert.IsType<BoundLetStatement>(body.Statements[0]).Local.Type.Name;

    /// <summary>The number of binary nodes along the left spine — the depth a per-operator recursion would reach.</summary>
    private static int LeftSpineLength(BoundExpression expression)
    {
        int length = 0;
        while (expression is BoundBinaryExpression binary)
        {
            length++;
            expression = binary.Left;
        }

        return length;
    }

    // ---- Flat chains have no limit (issue #34) ---------------------------

    [Theory]
    [InlineData("1", "+", "i64")]
    [InlineData("1", "*", "i64")]
    [InlineData("1.5", "-", "f64")]
    [InlineData("\"a\"", "+", "string")]
    [InlineData("true", "&&", "bool")]
    [InlineData("false", "||", "bool")]
    [InlineData("1 * 2", "+", "i64")]
    [InlineData("i64(1)", "-", "i64")]
    public void FlatChain_BindsAndEmits_WithoutRecursingPerOperator(string term, string op, string expectedType)
    {
        BoundLetStatement let = Assert.IsType<BoundLetStatement>(
            CompileAndEmit(InMain($"let x = {Chain(term, op)}; print(x == x);")).Statements[0]);

        Assert.Equal(expectedType, let.Local.Type.Name);
        Assert.InRange(LeftSpineLength(let.Initializer), ChainLength - 1, ChainLength);
    }

    [Fact]
    public void FlatChain_AsComparisonOperand_BindsToBool()
    {
        Assert.Equal("bool", LetType(CompileAndEmit(InMain($"let x = {Chain("1", "+")} < {Chain("1", "+")} + 1;"))));
    }

    // Pending-literal resolution (design §4.4) rewrites the whole chain to
    // the target type; it walks the same left spine iteratively.
    [Theory]
    [InlineData("let x: i32 = {0};", "1", "+", "i32")]           // Annotation resolves the chain.
    [InlineData("let x: f32 = {0};", "0.5", "*", "f32")]
    [InlineData("let x = {0} + 2i32;", "1", "+", "i32")]         // A concrete last operand resolves the pending chain.
    [InlineData("let x = 2i32 + {0};", "1", "+", "i32")]
    [InlineData("let x: i32 = -({0});", "1", "-", "i32")]        // Through a pending unary.
    [InlineData("let x = {0};", "1", "+", "i64")]                // Default resolution.
    public void FlatPendingChain_ResolvesToTheTargetType(string template, string term, string op, string expectedType)
    {
        string statement = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Chain(term, op));

        Assert.Equal(expectedType, LetType(CompileAndEmit(InMain(statement))));
    }

    [Fact]
    public void FlatPendingChain_OutOfRangeLiteral_IsReportedOnceAtTheLiteral()
    {
        string source = InMain($"let x: i32 = {Chain("1", "+")} + 3000000000;");
        Compilation compilation = Compile(source);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("ARITH3012", diagnostic.Code);
        Assert.Equal(source.IndexOf("3000000000", StringComparison.Ordinal), diagnostic.Span.Start);
    }

    [Fact]
    public void FlatChain_TypeErrorAtTheEnd_IsReportedOnceAtTheOperator()
    {
        string source = InMain($"let x = {Chain("1", "+")} + true;");
        Compilation compilation = Compile(source);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("ARITH3010", diagnostic.Code);
        Assert.Equal(source.LastIndexOf('+'), diagnostic.Span.Start);
    }

    [Fact]
    public void InterpolatedString_WithThousandsOfHoles_DesugarsToAFlatChain()
    {
        // f"${x}${x}…" desugars to string(x) + string(x) + …, another
        // left-deep chain (design §7).
        BoundLetStatement let = Assert.IsType<BoundLetStatement>(
            CompileAndEmit(InMain($"let x = 1; let s = f\"{Repeat("${x}", ChainLength)}\"; print(s);")).Statements[1]);

        Assert.Equal("string", let.Local.Type.Name);
        Assert.Equal(ChainLength - 1, LeftSpineLength(let.Initializer));
    }

    // ---- The limit ------------------------------------------------------

    [Fact]
    public void Parentheses_AtTheLimit_CompileAndEmit()
    {
        int depth = Max - PrintArgumentDepth;

        CompileAndEmit(Printing(Repeat("(", depth) + "1" + Repeat(")", depth)));
    }

    [Fact]
    public void Parentheses_OnePastTheLimit_ReportOnceWhereNestingExceedsIt()
    {
        int depth = Max - PrintArgumentDepth + 1;
        string source = Printing(Repeat("(", depth) + "1" + Repeat(")", depth));
        Compilation compilation = Compile(source);

        // The innermost expression is the first construct past the limit;
        // it is skipped, so the parentheses around it close normally.
        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("ARITH2005", diagnostic.Code);
        Assert.Equal(source.IndexOf('1', StringComparison.Ordinal), diagnostic.Span.Start);
        Assert.Equal(1, diagnostic.Span.Length);
        Assert.Equal(
            "(fn main (block (expr (call print " + Repeat("(paren ", depth) + "(error)" + Repeat(")", depth) + "))))",
            SyntaxDumper.Dump(compilation.SyntaxTree.Root));
    }

    // Past the limit, the rest of the enclosing expression is skipped as
    // one error, so however deep the source goes the tree stops at the
    // limit and the enclosing productions close normally.
    [Fact]
    public void Parentheses_FarPastTheLimit_SkipTheRestAsOneError()
    {
        int depth = Max - PrintArgumentDepth + 1;
        (string[] codes, string dump) = CompileScenario(Printing(Repeat("(", 5_000) + "1" + Repeat(")", 5_000)));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(
            "(fn main (block (expr (call print " + Repeat("(paren ", depth) + "(error)" + Repeat(")", depth) + "))))",
            dump);
    }

    [Fact]
    public void UnaryOperators_PastTheLimit_ReportOnce()
    {
        int depth = Max - PrintArgumentDepth + 1;
        (string[] codes, string dump) = CompileScenario(Printing(Repeat("-", 5_000) + "1"));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(
            "(fn main (block (expr (call print " + Repeat("(- ", depth) + "(error)" + Repeat(")", depth) + "))))",
            dump);
    }

    [Fact]
    public void CallArguments_PastTheLimit_ReportOnce()
    {
        int depth = Max - PrintArgumentDepth + 1;
        (string[] codes, string dump) = CompileScenario(Printing(Repeat("i64(", 5_000) + "1" + Repeat(")", 5_000)));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(
            "(fn main (block (expr (call print " + Repeat("(call i64 ", depth) + "(error)" + Repeat(")", depth) + "))))",
            dump);
    }

    [Fact]
    public void ArrayLiterals_PastTheLimit_ReportOnce()
    {
        int depth = Max - PrintArgumentDepth + 1;
        (string[] codes, string dump) = CompileScenario(Printing(Repeat("[", 5_000) + "1" + Repeat("]", 5_000)));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(
            "(fn main (block (expr (call print " + Repeat("(array ", depth) + "(error)" + Repeat(")", depth) + "))))",
            dump);
    }

    [Fact]
    public void RightNestedOperands_PastTheLimit_ReportOnce()
    {
        // `1 - (1 - (…))`: each level is a right operand plus a parenthesis,
        // two levels of nesting, unlike the left-deep chains above.
        int depth = (Max - PrintArgumentDepth) / 2 + 1;
        (string[] codes, string dump) = CompileScenario(Printing(Repeat("1 - (", 5_000) + "1" + Repeat(")", 5_000)));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(
            "(fn main (block (expr (call print " + Repeat("(- 1 (paren ", depth) + "(error)" + Repeat("))", depth) + "))))",
            dump);
    }

    [Fact]
    public void IndexChain_PastTheLimit_ReportsOnceAndSkipsTheChain()
    {
        // The postfix loop builds `a[0][0]…` without recursing, but the
        // binder and emitter recurse once per `[`, so the wraps count too.
        (string[] codes, string dump) = CompileScenario(
            InMain($"let a = [1]; print(a{Repeat("[0]", 5_000)});"));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal("(fn main (block (let a (array 1)) (expr (call print (error)))))", dump);
    }

    [Fact]
    public void IndexChain_WithinTheLimit_Parses()
    {
        // Each `[` is a level and its index expression another.
        int depth = Max - PrintArgumentDepth - 1;
        SyntaxTree tree = Parse(InMain($"let a = [1]; print(a{Repeat("[0]", depth)});"));

        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void NestedIfStatements_PastTheLimit_ReportOnce()
    {
        // The if at the limit keeps its shape; its condition is the first
        // construct past the limit, and the if inside its body is skipped
        // whole (brackets tracked) as one error statement.
        (string[] codes, string dump) = CompileScenario(
            InMain(Repeat("if true { ", 5_000) + "print(1);" + Repeat(" }", 5_000)));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(
            "(fn main (block " + Repeat("(if true (block ", Max - 1)
            + "(if (error) (block (error-stmt)))" + Repeat("))", Max - 1) + "))",
            dump);
    }

    [Fact]
    public void StatementsInTheDeepestBlock_RecoverIndividually()
    {
        // Siblings in the block at the limit: each too-deep expression is
        // skipped through its own `;`, the diagnostic is reported once, and
        // statements without a nested construct (`return;`) survive.
        (string[] codes, string dump) = CompileScenario(
            InMain(Repeat("if true { ", Max - 1) + "let x = 1; print(x); return;" + Repeat(" }", Max - 1)));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(
            "(fn main (block " + Repeat("(if true (block ", Max - 1)
            + "(let x (error)) (expr (error)) (return)" + Repeat("))", Max - 1) + "))",
            dump);
    }

    [Fact]
    public void CompoundStatementPastTheLimit_IsSkippedWholeAndItsSiblingsSeparately()
    {
        // The skip of the inner `if` stops at the statement keyword after
        // its body; `return;` is then skipped as its own statement — every
        // sibling in that block is past the limit too.
        (string[] codes, string dump) = CompileScenario(
            InMain(Repeat("if true { ", Max) + "if true { print(1); } return;" + Repeat(" }", Max)));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(
            "(fn main (block " + Repeat("(if true (block ", Max - 1)
            + "(if (error) (block (error-stmt) (error-stmt)))" + Repeat("))", Max - 1) + "))",
            dump);
    }

    [Fact]
    public void ElseIfChain_PastTheLimit_ReportsOnceAndSkipsTheRestOfTheChain()
    {
        // `else if` nests one statement deeper per arm (design §4.3), so a
        // long chain hits the limit like any other nesting. The first
        // construct past it is print's argument in the last complete arm;
        // the next arm keeps its shape with its condition operand and body
        // expression skipped, the arm after that loses its condition and
        // body statement, and the remaining arms are skipped as one error
        // statement — an `if` right after `else` is part of the chain
        // being skipped.
        int arms = 5_000;
        string source = InMain(
            "let x = 5; if x == 0 { print(0); } "
            + string.Join(" ", Enumerable.Range(1, arms - 1).Select(i => $"else if x == {i} {{ print({i}); }}")));
        (string[] codes, string dump) = CompileScenario(source);

        int complete = Max - 3; // Arm k's print argument is level k + 4, so arms 0 … Max - 4 are complete.
        string expected = "(fn main (block (let x 5) "
            + string.Concat(Enumerable.Range(0, complete).Select(k => $"(if (== x {k}) (block (expr (call print {k}))) "))
            + $"(if (== x {complete}) (block (expr (call print (error)))) "
            + "(if (== x (error)) (block (expr (error))) "
            + "(if (error) (block (error-stmt)) (error-stmt))"
            + Repeat(")", complete + 2) + "))";
        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal(expected, dump);
    }

    [Fact]
    public void ElseIfChain_WithinTheLimit_Compiles()
    {
        int arms = Max - 4;
        CompileAndEmit(InMain(
            $"let x = {arms}; if x == 0 {{ print(0); }} "
            + string.Join(" ", Enumerable.Range(1, arms).Select(i => $"else if x == {i} {{ print({i}); }}"))));
    }

    // ---- Array types: the emitter encodes element types recursively -------

    [Fact]
    public void ArrayType_AtTheLimit_CompilesAndEmits()
    {
        // A parameter type sits outside any statement, so all 256 levels
        // are its own; a let's annotation is one level in.
        CompileAndEmit(
            $"fn deep(x: {Repeat("[]", Max)}i64) {{ }} fn main() {{ let a: {Repeat("[]", Max - 1)}i64 = []; }}");
    }

    [Fact]
    public void ArrayType_PastTheLimit_ReportsOnceAndBindsAsAnErrorType()
    {
        string source = $"fn deep(x: {Repeat("[]", 5_000)}i64) {{ }} fn main() {{ deep([[1]]); }}";
        Compilation compilation = Compile(source);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("ARITH2005", diagnostic.Code);
        Assert.Equal("fn deep(x: ".Length + "[]".Length * Max, diagnostic.Span.Start);
        Assert.Equal(
            "(fn deep (param x ) (block)) (fn main (block (expr (call deep (array (array 1))))))",
            SyntaxDumper.Dump(compilation.SyntaxTree.Root));
    }

    [Fact]
    public void ArrayTypeAnnotation_PastTheLimit_ReportsOnce()
    {
        (string[] codes, string dump) = CompileScenario(
            InMain($"let a: {Repeat("[]", 5_000)}i64 = [[1]]; print(len(a));"));

        Assert.Equal(["ARITH2005"], codes);
        Assert.Equal("(fn main (block (let a :  (array (array 1))) (expr (call print (call len a)))))", dump);
    }

    // ---- Interpolated strings: the lexer's share of the limit -------------

    [Fact]
    public void NestedInterpolatedStrings_PastTheParserLimit_ReportOnceForTheOutermostHole()
    {
        // 255 nested literals stay under the whole-file lex's check (which
        // counts holes alone) but not the parser's, which also counts the
        // statement and call around them. The parser re-lexes the outer
        // literal's hole at its own depth, and that re-lex scans every
        // literal nested inside at once, so the limit is hit there: the
        // whole hole becomes one Bad token and the diagnostic covers it.
        int literals = Max - 1;
        string source = Printing(Repeat("f\"${", literals) + "1" + Repeat("}\"", literals));
        Compilation compilation = Compile(source);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("ARITH2005", diagnostic.Code);
        Assert.Equal("fn main() { print(f\"${".Length, diagnostic.Span.Start);
        Assert.Equal(source.LastIndexOf("}\");", StringComparison.Ordinal), diagnostic.Span.End);
        Assert.Equal(
            "(fn main (block (expr (call print (call string (error))))))",
            SyntaxDumper.Dump(compilation.SyntaxTree.Root));
    }

    // IDEAL: one ARITH2005, with the literal ending at its real closing
    // quote so the `);` after it still parse.
    // TODAY: the lexer cannot find a literal's end without scanning its
    // holes, and scanning is what nests, so past the limit the literal runs
    // to the end of the line like an unterminated one — and the `)` and `;`
    // on that line are then reported missing.
    [Fact]
    public void NestedInterpolatedStrings_PastTheLexerLimit_LexAsOneBadTokenToTheEndOfTheLine()
    {
        string literal = Repeat("f\"${", 5_000) + "1" + Repeat("}\"", 5_000);
        string source = "fn main() {\n    print(" + literal + ");\n}\n";
        DiagnosticBag bag = new();
        System.Collections.Immutable.ImmutableArray<Token> tokens = Lexer.Lex(SourceText.From(source), bag);

        Assert.Equal(
            [SyntaxKind.FnKeyword, SyntaxKind.IdentifierToken, SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken,
             SyntaxKind.OpenBraceToken, SyntaxKind.IdentifierToken, SyntaxKind.OpenParenToken, SyntaxKind.BadToken,
             SyntaxKind.CloseBraceToken, SyntaxKind.EndOfFileToken],
            tokens.Select(t => t.Kind));
        Diagnostic diagnostic = Assert.Single(bag.ToImmutableArray());
        Assert.Equal("ARITH2005", diagnostic.Code);
        Assert.Equal(source.IndexOf("f\"", StringComparison.Ordinal), diagnostic.Span.Start);
        Assert.Equal(literal.Length + ");".Length, diagnostic.Span.Length);

        (string[] codes, string dump) = CompileScenario(source);
        Assert.Equal(["ARITH2005", "ARITH2001", "ARITH2001"], codes);
        Assert.Equal("(fn main (block (expr (call print (error)))))", dump);
    }

    [Fact]
    public void NestedInterpolatedStrings_WithinTheLimit_Compile()
    {
        int literals = Max - PrintArgumentDepth;

        CompileAndEmit(Printing(Repeat("f\"${", literals) + "1" + Repeat("}\"", literals)));
    }
}
