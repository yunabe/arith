using Arith.Compiler.Diagnostics;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

/// <summary>
/// A catalog of realistic error patterns the parser currently recovers from
/// poorly. Each test pins the CURRENT behavior — the diagnostic sequence and
/// the recovered tree — so any change to recovery, better or worse, shows up
/// as a test failure. The comment on each test records the IDEAL behavior a
/// smarter parser would produce; when recovery improves, move the assertions
/// toward the ideal.
///
/// docs/compiler-design.md deliberately starts with minimal sync-point
/// recovery, so nothing here is a bug in the usual sense — this file is the
/// map of that decision's known weak spots.
/// </summary>
public sealed class ParserRecoveryScenarioTests
{
    private static (string[] Codes, string Dump) ParseScenario(string source)
    {
        SyntaxTree tree = SyntaxTree.Parse(SourceText.From(source));
        return ([.. tree.Diagnostics.Select(d => d.Code)], SyntaxDumper.Dump(tree.Root));
    }

    // IDEAL: one diagnostic saying a '}' is missing before `fn second`, and
    // `second` parsed as its own function — a `fn` inside a block almost
    // always means the previous function was never closed.
    // TODAY: `second` is swallowed into `first`'s body. The three
    // diagnostics blame the `fn` keyword and its braces, none mentions the
    // missing '}', and the tree has one function instead of two.
    [Fact]
    public void MissingCloseBraceBeforeNextFunction_SwallowsTheNextFunction()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn first() { let x = 1;\nfn second() { let y = 2; }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal(
            "(fn first (block (let x 1) (error-stmt) (expr (call second)) (error-stmt) (let y 2)))",
            dump);
    }

    // IDEAL: one diagnostic ("expected '{' after the if condition"), with the
    // function's closing '}' still closing the function.
    // TODAY: the fabricated '{' makes the if adopt `print(x);` AND the
    // function's real '}', so a second, misleading "expected '}'" is
    // reported at end of file.
    [Fact]
    public void MissingOpenBraceAfterIfCondition_StealsTheFunctionCloseBrace()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { if x > 0 print(x); }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (if (> x 0) (block (expr (call print x))))))", dump);
    }

    // IDEAL: one diagnostic pointing at `lett`, ideally suggesting `let`
    // (one edit away from a statement keyword).
    // TODAY: `lett` parses as a name expression, producing the unrelated
    // "only a call expression can be used as a statement", and the rest
    // re-parses as the assignment `x = 1` — two diagnostics, neither about
    // the typo.
    [Fact]
    public void MisspelledLetKeyword_ProducesUnrelatedDiagnostics()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { lett x = 1; }");

        string[] expectedCodes = ["ARITH2002", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (expr lett) (= x 1)))", dump);
    }

    // IDEAL: one diagnostic ("expected '=', found '=='"), treating the '=='
    // as the '=' and parsing the initializer normally.
    // TODAY: one typo cascades into four diagnostics — the missing '=', a
    // missing expression (the unconsumed '=='), a missing ';', and a bogus
    // statement error for the leftover `1`.
    [Fact]
    public void DoubleEqualsInLetInitializer_CascadesIntoFourDiagnostics()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { let x == 1; }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001", "ARITH2001", "ARITH2002"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (let x (error)) (expr 1)))", dump);
    }

    // IDEAL: `main() {` at the top level is recognized as a function
    // declaration missing its `fn`; report that and parse the function.
    // TODAY: the diagnostic is reasonable, but the whole of `main` —
    // signature and body — is silently skipped while resynchronizing to the
    // next `fn`, so the tree keeps only `helper` and the binder will later
    // report a missing entry point for a program that clearly has one.
    [Fact]
    public void MissingFnKeyword_SkipsTheEntireFunction()
    {
        (string[] codes, string dump) = ParseScenario("main() { return 0; }\nfn helper() { }");

        string[] expectedCodes = ["ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn helper (block))", dump);
    }

    // IDEAL: one diagnostic ("expected '->' before the return type"),
    // consuming `i64` as the return type and parsing the body normally.
    // TODAY: five diagnostics — `i64` is mistaken for the function body's
    // first statement and then for a conversion call, dragging the real '{'
    // and the `return` into the wreckage before recovery finds its feet.
    [Fact]
    public void MissingArrowBeforeReturnType_CascadesIntoFiveDiagnostics()
    {
        (string[] codes, string dump) = ParseScenario("fn f() i64 { return 0; }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn f (block (expr (call i64 (error))) (return 0)))", dump);
    }

    // IDEAL: the diagnostic points at (or near) the brace that was never
    // closed — here the `if a {` — using indentation or statement position
    // as the hint, the way rustc pairs "unclosed delimiter" notes.
    // TODAY: the single diagnostic sits at end of file, the least helpful
    // position, and the tree quietly attaches `return x;` and the final '}'
    // to the if body, so the '}' the user forgot appears to be the
    // function's.
    [Fact]
    public void UnclosedNestedBlock_ReportsAtEndOfFileInsteadOfTheOpenBrace()
    {
        const string source = "fn t() {\n    if a {\n        let x = 1;\n    return x;\n}";

        SyntaxTree tree = SyntaxTree.Parse(SourceText.From(source));

        Diagnostic diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("ARITH2001", diagnostic.Code);
        Assert.Equal(source.Length, diagnostic.Span.Start); // Reported at EOF.
        Assert.Equal(
            "(fn t (block (if a (block (let x 1) (return x)))))",
            SyntaxDumper.Dump(tree.Root));
    }

    // IDEAL: one diagnostic ("expected ';', found ','"), treating the comma
    // as the statement separator it was meant to be.
    // TODAY: the missing-';' diagnostic is right, but the leftover comma
    // cannot start a statement, so a second, noisier diagnostic and an
    // error-statement node follow it.
    [Fact]
    public void CommaInsteadOfSemicolon_ReportsTwiceForOneTypo()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { let x = 1, let y = 2; }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (let x 1) (error-stmt) (let y 2)))", dump);
    }

    // IDEAL: a targeted message like "remove this ';' — blocks are not
    // followed by a semicolon", with no error node cluttering the tree.
    // TODAY: recovery is fine (one diagnostic, rest of the block parses),
    // but "unexpected ';', expected a statement" reads as if a statement
    // were missing rather than a semicolon being extra.
    [Fact]
    public void SemicolonAfterBlock_ReportsAMisleadingMessage()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { if x { }; let y = 1; }");

        string[] expectedCodes = ["ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (if x (block)) (error-stmt) (let y 1)))", dump);
    }

    // A contrast case where the current minimal recovery already behaves
    // well: an extra top-level '}' costs exactly one diagnostic and both
    // functions survive. Kept here to define the bar the cases above miss.
    [Fact]
    public void ExtraTopLevelCloseBrace_RecoversWithOneDiagnostic()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { let x = 1; } } fn u() { }");

        string[] expectedCodes = ["ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (let x 1))) (fn u (block))", dump);
    }
}
