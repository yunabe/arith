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

    // IDEAL: one diagnostic saying a '}' is missing before `else`, then keep
    // the else clause attached to the if. An `else` inside a then-block is a
    // strong signal that the block should have ended immediately before it.
    // TODAY: statement recovery consumes both `else` and its '{'. The
    // would-be else body is consequently appended to the then-block, and the
    // recovered if has no else clause at all.
    [Fact]
    public void MissingCloseBraceBeforeElse_DropsTheElseClause()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn t() { if a { let x = 1; else { let y = 2; } }");

        string[] expectedCodes = ["ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal(
            "(fn t (block (if a (block (let x 1) (error-stmt) (let y 2)))))",
            dump);
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

    // IDEAL: the lexer diagnostic for '@' is sufficient; consume its Bad
    // token as the malformed identifier and resume at '=' without parser
    // diagnostics cascading from the same character.
    // FIXED: matches the ideal — MatchToken drops the Bad token silently
    // (cascade suppression), so the lexer's diagnostic stands alone and the
    // rest of the let, initializer included, parses normally.
    [Fact]
    public void BadTokenInIdentifierPosition_ReportsOnlyTheLexerDiagnostic()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { let @ = 1; }");

        string[] expectedCodes = ["ARITH1001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (let  1)))", dump);
    }

    // A companion to the case above: when the expected token sits right
    // behind the Bad token, MatchToken's silent drop lets it match, so
    // `@x` recovers as the identifier `x` with only the lexer diagnostic.
    [Fact]
    public void BadTokenBeforeIdentifier_RecoversTheIdentifier()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { let @x = 1; }");

        string[] expectedCodes = ["ARITH1001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (let x 1)))", dump);
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
    // TODAY: six diagnostics — `i64` is mistaken for the function body's
    // first statement and then for a conversion call, dragging the real '{'
    // and the `return` into the wreckage. (Adding '{' to the
    // error-expression stop set fixed the if/for cases but leaves this '{'
    // unconsumed to be reported one extra time — the real cure is treating
    // `fn name() type {` as a missing '->'.)
    [Fact]
    public void MissingArrowBeforeReturnType_CascadesIntoSixDiagnostics()
    {
        (string[] codes, string dump) = ParseScenario("fn f() i64 { return 0; }");

        string[] expectedCodes =
        [
            "ARITH2001", "ARITH2001", "ARITH2001",
            "ARITH2001", "ARITH2001", "ARITH2001",
        ];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn f (block (expr (call i64 (error))) (error-stmt) (return 0)))", dump);
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

    // IDEAL: interpret `return }` as the grammar-valid valueless `return;`
    // with only its semicolon missing, so the return node has no value and a
    // single diagnostic points at the insertion site.
    // FIXED: matches the ideal — a '}' after `return` selects the valueless
    // form, and the one diagnostic is the missing ';'.
    [Fact]
    public void MissingSemicolonAfterValuelessReturn_RecoversAsValueless()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { return }");

        string[] expectedCodes = ["ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (return)))", dump);
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

    // IDEAL: the unterminated-string diagnostic alone — everything after it
    // on the line is inside the Bad token, so the ')' and ';' complaints are
    // consequences, not new information.
    // TODAY: the Bad token has eaten `);`, so the parser adds two noise
    // diagnostics for the ')' and ';' it can no longer find. (The next line
    // does recover cleanly.)
    [Fact]
    public void UnterminatedString_AddsNoiseForTheSwallowedDelimiters()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn t() { print(\"hello);\n    let x = 1;\n}");

        string[] expectedCodes = ["ARITH1002", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (expr (call print (error))) (let x 1)))", dump);
    }

    // IDEAL: the diagnostic is accurate, but a smarter recovery could notice
    // that the comment swallowed line-start `fn` declarations and either
    // suggest where the missing `*/` probably belongs or re-lex the
    // remainder — and the binder should suppress its future "no main
    // function" complaint for a file that ends inside a comment.
    // TODAY: one correct diagnostic, and the entire program silently
    // becomes empty.
    [Fact]
    public void UnterminatedBlockComment_SwallowsTheWholeProgram()
    {
        (string[] codes, string dump) = ParseScenario("/* TODO explain\nfn main() { return 0; }");

        string[] expectedCodes = ["ARITH1004"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("", dump);
    }

    // IDEAL: one diagnostic per quote character — or one for the whole
    // literal — saying Arith strings use straight double quotes. Smart
    // quotes arrive via copy-paste from documents; single quotes are a
    // habit from other languages. Both deserve a targeted message.
    // TODAY: each bad quote is an "unexpected character", the string's
    // content lexes as an identifier, and the parser piles four more
    // diagnostics on top: six in total for one string literal. (MatchToken's
    // Bad-token suppression removed the seventh.)
    [Theory]
    [InlineData("fn t() { print(“hi”); }")]
    [InlineData("fn t() { print('hi'); }")]
    public void WrongQuoteCharacters_ProduceSixDiagnostics(string source)
    {
        (string[] codes, string dump) = ParseScenario(source);

        string[] expectedCodes =
        [
            "ARITH1001", "ARITH1001", "ARITH2001",
            "ARITH2001", "ARITH2002", "ARITH2001",
        ];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal(
            "(fn t (block (expr (call print (error))) (expr hi) (error-stmt)))",
            dump);
    }

    // IDEAL: `=` directly inside a condition is the classic C-habit slip;
    // report "use '==' to compare, '=' is not an expression", treat it as
    // '==', and keep the whole condition.
    // TODAY: the condition silently truncates to `x`, and the two
    // diagnostics complain about a '{' and a statement — neither mentions
    // '=' vs '=='. The `print(x)` body survives only as debris.
    [Fact]
    public void AssignmentInCondition_NeverMentionsDoubleEquals()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { if x = 1 { print(x); } }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (if x (block (error-stmt)))))", dump);
    }

    // IDEAL: a lone '&' one character from '&&' deserves "did you mean
    // '&&'?" (Arith has no bitwise operators), parsed as '&&' for recovery:
    // one diagnostic, condition intact.
    // TODAY: the lexer's Bad token truncates the condition at `a`, and the
    // parser reports three more errors while `b { }` shreds into debris.
    // (MatchToken now swallows the Bad token where the then-block's '{' was
    // expected, which removed one cascade diagnostic but not the shredding.)
    [Fact]
    public void SingleAmpersandInCondition_ShredsTheIfStatement()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { if a & b { } }");

        string[] expectedCodes = ["ARITH1001", "ARITH2002", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (if a (block (expr b) (error-stmt)))))", dump);
    }

    // IDEAL: `for i = 0; …` is unmistakably a C-style for; one diagnostic
    // explaining Arith's range syntax (`for i in 0..10`) beats parsing the
    // three clauses as garbage.
    // TODAY: eight diagnostics. The `i < 10` clause becomes a statement,
    // `i += 1` is adopted as a loop-body statement, and the real body's
    // '{' is reported twice.
    [Fact]
    public void CStyleForLoop_ProducesEightDiagnostics()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { for i = 0; i < 10; i += 1 { } }");

        string[] expectedCodes =
        [
            "ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001",
            "ARITH2001", "ARITH2002", "ARITH2001", "ARITH2001",
        ];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal(
            "(fn t (block (for i  (error) 0 (block (error-stmt) (expr (< i 10)) (+= i 1) (error-stmt)))))",
            dump);
    }

    // IDEAL: one "functions cannot be nested" diagnostic (spec §5), parsing
    // `inner` as a function anyway so its body is still checked, and
    // keeping `outer`'s remaining statements.
    // TODAY: `fn` in statement position is debris — four diagnostics,
    // `inner` survives only as a phantom call expression, and the final
    // diagnostic even blames the function's own closing '}' at top level.
    [Fact]
    public void NestedFunctionDeclaration_BecomesPhantomCallPlusFourDiagnostics()
    {
        (string[] codes, string dump) = ParseScenario("fn outer() { fn inner() { } }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn outer (block (error-stmt) (expr (call inner)) (error-stmt)))", dump);
    }

    // IDEAL: '++' is the most common habit from C-family languages; a
    // targeted "Arith has no '++'; use 'i += 1'" costs one diagnostic.
    // TODAY: the second '+' fails to start an expression and the first
    // turns the statement into `i + (error)`, which then also triggers the
    // only-calls-can-be-statements error — two diagnostics, no hint.
    [Fact]
    public void IncrementOperator_GetsNoTargetedSuggestion()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { i++; }");

        string[] expectedCodes = ["ARITH2001", "ARITH2002"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (expr (+ i (error)))))", dump);
    }

    // IDEAL: one "trailing comma is not allowed" diagnostic and no extra
    // parameter node.
    // FIXED: matches the ideal — the list parsers spot a ')' right after a
    // ',' and report ARITH2003 at the comma; no ghost parameter is created.
    [Fact]
    public void TrailingCommaInParameterList_ReportsOneTargetedDiagnostic()
    {
        (string[] codes, string dump) = ParseScenario("fn f(a: i64,) { }");

        string[] expectedCodes = ["ARITH2003"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn f (param a i64) (block))", dump);
    }

    // The argument-list variant of the case above, sharing the same fix.
    [Fact]
    public void TrailingCommaInArgumentList_ReportsOneTargetedDiagnostic()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { f(1, 2,); }");

        string[] expectedCodes = ["ARITH2003"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (expr (call f 1 2))))", dump);
    }

    // IDEAL: one diagnostic ("expected a condition before '{'").
    // FIXED: matches the ideal — '{' joined the error-expression stop set,
    // so it opens the body as written and the single diagnostic is the
    // missing condition.
    [Fact]
    public void MissingIfCondition_RecoversWithOneDiagnostic()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { if { print(x); } }");

        string[] expectedCodes = ["ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (if (error) (block (expr (call print x))))))", dump);
    }

    // IDEAL: "'else' has no matching 'if'" as the only diagnostic, skipping
    // exactly the else block and keeping `let x = 1;`.
    // TODAY: the statement-boundary skip swallows the else's `{ }`, but the
    // block's '}' then closes the *function*, so `let x = 1;` is reported
    // as top-level junk and vanishes — half the function body is lost to
    // one stray keyword.
    [Fact]
    public void OrphanElse_ClosesTheFunctionEarlyAndDropsTheRest()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { else { } let x = 1; }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (error-stmt)))", dump);
    }

    // IDEAL: "assignment is a statement, not an expression" (spec §8.4) at
    // the '=', as the single diagnostic — this is exactly the C idiom the
    // spec rules out, so the parser can name it.
    // TODAY: the parenthesized expression closes early at `x`, and three
    // diagnostics blame the ')', the ';', and the leftover `5)` instead of
    // the real issue.
    [Fact]
    public void AssignmentUsedAsExpression_BlamesEverythingButTheAssignment()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { let y = (x = 5); }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (let y (paren x)) (error-stmt)))", dump);
    }

    // IDEAL: `print x;` — a name directly followed by another name — is a
    // call missing its parentheses; one diagnostic saying so.
    // TODAY: three diagnostics: `print` alone is not a call statement, the
    // ';' is missing, and then `x` alone is not a call statement either.
    [Fact]
    public void CallWithoutParentheses_ReportsThreeUnrelatedDiagnostics()
    {
        (string[] codes, string dump) = ParseScenario("fn t() { print x; }");

        string[] expectedCodes = ["ARITH2002", "ARITH2001", "ARITH2002"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (expr print) (expr x)))", dump);
    }

    // IDEAL: one "expected ',' between parameters" diagnostic, treating the
    // whitespace before `b` as the missing separator and preserving both
    // parameters and the empty body.
    // TODAY: the parser ends the parameter list after `a`, fabricates ')' and
    // '{', then reinterprets `b: i64` as body debris: five diagnostics, the
    // second parameter is lost, and the real body braces close the debris.
    [Fact]
    public void MissingCommaBetweenParameters_TurnsTheSecondParameterIntoBodyDebris()
    {
        (string[] codes, string dump) = ParseScenario("fn f(a: i64 b: i64) { }");

        string[] expectedCodes =
            ["ARITH2001", "ARITH2001", "ARITH2002", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn f (param a i64) (block (expr b) (error-stmt)))", dump);
    }

    // IDEAL: the missing `()` on a no-parameter function costs one targeted
    // diagnostic (or one for each parenthesis) and produces no parameters.
    // TODAY: seeing '{' where '(' belongs enters parameter parsing anyway,
    // fabricating an identifier, ':', type, and ')' around a ghost parameter:
    // five diagnostics for the two omitted delimiters.
    [Fact]
    public void MissingEmptyParameterParentheses_FabricatesAGhostParameter()
    {
        (string[] codes, string dump) = ParseScenario("fn f { }");

        string[] expectedCodes =
            ["ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn f (param  ) (block))", dump);
    }

    // IDEAL: one "unknown type 'int'" diagnostic, ideally suggesting `i32`
    // or `i64`, while keeping `1` as the initializer and the next statement.
    // TODAY: ParseType leaves `int` unconsumed. It becomes the initializer,
    // the real '= 1;' is skipped as another statement, and one familiar type
    // name produces four diagnostics and a materially wrong let node.
    [Fact]
    public void UnknownLocalTypeName_BecomesTheInitializer()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn t() { let x: int = 1; let y = 2; }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (let x :  int) (error-stmt) (let y 2)))", dump);
    }

    // IDEAL: one "expected ',' between arguments" diagnostic, inserting the
    // separator so the call remains `f(1, 2)` and the following let survives.
    // TODAY: the call closes after `1`; `2` is ejected into a non-call
    // expression statement and ')' becomes debris, yielding five diagnostics
    // and a call with the wrong arity.
    [Fact]
    public void MissingCommaBetweenArguments_EjectsTheSecondArgumentIntoAStatement()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn t() { f(1 2); let x = 3; }");

        string[] expectedCodes =
            ["ARITH2001", "ARITH2001", "ARITH2002", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (expr (call f 1)) (expr 2) (error-stmt) (let x 3)))", dump);
    }

    // IDEAL: one diagnostic at '*' saying an operand is missing after '+',
    // then consume or reinterpret the extra operator without losing `2` from
    // the initializer.
    // TODAY: '*' becomes the error operand, `2` is re-parsed as a forbidden
    // expression statement, and one doubled operator produces three
    // diagnostics plus a truncated initializer.
    [Fact]
    public void DoubledBinaryOperator_EjectsTheRightOperandIntoAStatement()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn t() { let x = 1 + * 2; let y = 3; }");

        string[] expectedCodes = ["ARITH2001", "ARITH2001", "ARITH2002"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal("(fn t (block (let x (+ 1 (error))) (expr 2) (let y 3)))", dump);
    }

    // IDEAL: `for i = 0..10` is one token away from Arith's range syntax;
    // report "expected 'in', found '='", treat it as `in`, and preserve the
    // loop and the statement after it.
    // TODAY: five diagnostics follow. The start and range operator are lost,
    // the loop body is mostly debris, and only the later let fully recovers.
    [Fact]
    public void EqualsInsteadOfIn_DestroysTheRangeLoop()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn t() { for i = 0..10 { print(i); } let y = 2; }");

        string[] expectedCodes =
            ["ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001", "ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal(
            "(fn t (block (for i  (error) 0 (block (error-stmt))) (let y 2)))",
            dump);
    }

    // IDEAL: one diagnostic for the missing range start, leaving '..' in
    // place as the range operator and parsing `10` as the end.
    // FIXED: matches the ideal — the range operators joined the
    // error-expression stop set, so '..' survives as the operator, `10` is
    // the end, and the loop keeps its body.
    [Fact]
    public void MissingRangeStart_RecoversWithOneDiagnostic()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn t() { for i in ..10 { print(i); } let y = 2; }");

        string[] expectedCodes = ["ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal(
            "(fn t (block (for i .. (error) 10 (block (expr (call print i)))) (let y 2)))",
            dump);
    }

    // IDEAL: one diagnostic for the missing range end, leaving '{' to open
    // the loop body — the same delimiter-preservation principle as a missing
    // if condition, but at the second expression in a for production.
    // FIXED: matches the ideal — '{' joined the error-expression stop set,
    // so the body opens normally and the single diagnostic is the missing
    // end expression.
    [Fact]
    public void MissingRangeEnd_RecoversWithOneDiagnostic()
    {
        (string[] codes, string dump) = ParseScenario(
            "fn t() { for i in 0.. { print(i); } let y = 2; }");

        string[] expectedCodes = ["ARITH2001"];
        Assert.Equal(expectedCodes, codes);
        Assert.Equal(
            "(fn t (block (for i .. 0 (error) (block (expr (call print i)))) (let y 2)))",
            dump);
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
