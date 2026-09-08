using System.Collections.Immutable;

using Arith.Compiler.Diagnostics;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

public sealed class LexerTests
{
    private static ImmutableArray<Token> Lex(string source, out ImmutableArray<Diagnostic> diagnostics)
    {
        DiagnosticBag bag = new();
        ImmutableArray<Token> tokens = Lexer.Lex(SourceText.From(source), bag);
        diagnostics = bag.ToImmutableArray();
        return tokens;
    }

    /// <summary>Lexes source expected to be error-free and strips the trailing EndOfFile token.</summary>
    private static ImmutableArray<Token> LexClean(string source)
    {
        ImmutableArray<Token> tokens = Lex(source, out ImmutableArray<Diagnostic> diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(SyntaxKind.EndOfFileToken, tokens[^1].Kind);
        return tokens[..^1];
    }

    [Theory]
    [InlineData("fn", SyntaxKind.FnKeyword)]
    [InlineData("let", SyntaxKind.LetKeyword)]
    [InlineData("return", SyntaxKind.ReturnKeyword)]
    [InlineData("if", SyntaxKind.IfKeyword)]
    [InlineData("else", SyntaxKind.ElseKeyword)]
    [InlineData("while", SyntaxKind.WhileKeyword)]
    [InlineData("for", SyntaxKind.ForKeyword)]
    [InlineData("in", SyntaxKind.InKeyword)]
    [InlineData("break", SyntaxKind.BreakKeyword)]
    [InlineData("continue", SyntaxKind.ContinueKeyword)]
    [InlineData("true", SyntaxKind.TrueKeyword)]
    [InlineData("false", SyntaxKind.FalseKeyword)]
    [InlineData("bool", SyntaxKind.BoolKeyword)]
    [InlineData("i32", SyntaxKind.I32Keyword)]
    [InlineData("i64", SyntaxKind.I64Keyword)]
    [InlineData("f32", SyntaxKind.F32Keyword)]
    [InlineData("f64", SyntaxKind.F64Keyword)]
    [InlineData("string", SyntaxKind.StringKeyword)]
    [InlineData("(", SyntaxKind.OpenParenToken)]
    [InlineData(")", SyntaxKind.CloseParenToken)]
    [InlineData("{", SyntaxKind.OpenBraceToken)]
    [InlineData("}", SyntaxKind.CloseBraceToken)]
    [InlineData(",", SyntaxKind.CommaToken)]
    [InlineData(":", SyntaxKind.ColonToken)]
    [InlineData(";", SyntaxKind.SemicolonToken)]
    [InlineData("->", SyntaxKind.ArrowToken)]
    [InlineData("..", SyntaxKind.DotDotToken)]
    [InlineData("..=", SyntaxKind.DotDotEqualsToken)]
    [InlineData("+", SyntaxKind.PlusToken)]
    [InlineData("-", SyntaxKind.MinusToken)]
    [InlineData("*", SyntaxKind.StarToken)]
    [InlineData("/", SyntaxKind.SlashToken)]
    [InlineData("%", SyntaxKind.PercentToken)]
    [InlineData("!", SyntaxKind.BangToken)]
    [InlineData("=", SyntaxKind.EqualsToken)]
    [InlineData("+=", SyntaxKind.PlusEqualsToken)]
    [InlineData("-=", SyntaxKind.MinusEqualsToken)]
    [InlineData("*=", SyntaxKind.StarEqualsToken)]
    [InlineData("/=", SyntaxKind.SlashEqualsToken)]
    [InlineData("%=", SyntaxKind.PercentEqualsToken)]
    [InlineData("==", SyntaxKind.EqualsEqualsToken)]
    [InlineData("!=", SyntaxKind.BangEqualsToken)]
    [InlineData("<", SyntaxKind.LessToken)]
    [InlineData("<=", SyntaxKind.LessEqualsToken)]
    [InlineData(">", SyntaxKind.GreaterToken)]
    [InlineData(">=", SyntaxKind.GreaterEqualsToken)]
    [InlineData("&&", SyntaxKind.AmpersandAmpersandToken)]
    [InlineData("||", SyntaxKind.PipePipeToken)]
    [InlineData("abc", SyntaxKind.IdentifierToken)]
    [InlineData("_x1", SyntaxKind.IdentifierToken)]
    [InlineData("Fn", SyntaxKind.IdentifierToken)]  // Names are case-sensitive.
    [InlineData("42", SyntaxKind.IntegerLiteralToken)]
    [InlineData("0", SyntaxKind.IntegerLiteralToken)]
    [InlineData("10i32", SyntaxKind.IntegerLiteralToken)]
    [InlineData("10i64", SyntaxKind.IntegerLiteralToken)]
    [InlineData("3.14", SyntaxKind.FloatLiteralToken)]
    [InlineData("1.5f32", SyntaxKind.FloatLiteralToken)]
    [InlineData("1.5f64", SyntaxKind.FloatLiteralToken)]
    [InlineData("\"hello\"", SyntaxKind.StringLiteralToken)]
    [InlineData("\"\"", SyntaxKind.StringLiteralToken)]
    [InlineData("\"a\\n\\t\\r\\\"\\\\b\"", SyntaxKind.StringLiteralToken)]
    public void Lex_SingleToken_ProducesKindAndFullText(string source, SyntaxKind expectedKind)
    {
        Token token = Assert.Single(LexClean(source));

        Assert.Equal(expectedKind, token.Kind);
        Assert.Equal(source, token.Text);
        Assert.Equal(new TextSpan(0, source.Length), token.Span);
    }

    [Theory]
    [InlineData("0..10", new[] { SyntaxKind.IntegerLiteralToken, SyntaxKind.DotDotToken, SyntaxKind.IntegerLiteralToken })]
    [InlineData("0..=10", new[] { SyntaxKind.IntegerLiteralToken, SyntaxKind.DotDotEqualsToken, SyntaxKind.IntegerLiteralToken })]
    [InlineData("1.5..2.5", new[] { SyntaxKind.FloatLiteralToken, SyntaxKind.DotDotToken, SyntaxKind.FloatLiteralToken })]
    [InlineData("a==b", new[] { SyntaxKind.IdentifierToken, SyntaxKind.EqualsEqualsToken, SyntaxKind.IdentifierToken })]
    [InlineData("a= =b", new[] { SyntaxKind.IdentifierToken, SyntaxKind.EqualsToken, SyntaxKind.EqualsToken, SyntaxKind.IdentifierToken })]
    [InlineData("x-＞", new[] { SyntaxKind.IdentifierToken, SyntaxKind.MinusToken, SyntaxKind.BadToken })]
    [InlineData("-42", new[] { SyntaxKind.MinusToken, SyntaxKind.IntegerLiteralToken })]
    [InlineData("ifx", new[] { SyntaxKind.IdentifierToken })]
    [InlineData("fn main()", new[] { SyntaxKind.FnKeyword, SyntaxKind.IdentifierToken, SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken })]
    public void Lex_TokenSequence_SplitsAtTheRightBoundaries(string source, SyntaxKind[] expectedKinds)
    {
        ImmutableArray<Token> tokens = Lex(source, out _);

        Assert.Equal(SyntaxKind.EndOfFileToken, tokens[^1].Kind);
        Assert.Equal(expectedKinds, tokens[..^1].Select(t => t.Kind));
    }

    [Fact]
    public void Lex_WhitespaceAndComments_AreSkipped()
    {
        const string source = """
            // line comment
            let x = 1; /* block
            comment */ let /**/ y = 2;
            """;

        ImmutableArray<Token> tokens = LexClean(source);

        Assert.Equal(
            new[]
            {
                SyntaxKind.LetKeyword, SyntaxKind.IdentifierToken, SyntaxKind.EqualsToken,
                SyntaxKind.IntegerLiteralToken, SyntaxKind.SemicolonToken,
                SyntaxKind.LetKeyword, SyntaxKind.IdentifierToken, SyntaxKind.EqualsToken,
                SyntaxKind.IntegerLiteralToken, SyntaxKind.SemicolonToken,
            },
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Lex_BlockComment_DoesNotNest()
    {
        // Spec §2.3: block comments cannot be nested, so the comment ends at
        // the first */ and the second */ is two stray tokens.
        ImmutableArray<Token> tokens = Lex("/* /* */ */", out ImmutableArray<Diagnostic> diagnostics);

        Assert.Equal(
            new[] { SyntaxKind.StarToken, SyntaxKind.SlashToken, SyntaxKind.EndOfFileToken },
            tokens.Select(t => t.Kind));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Lex_TokenSpans_MatchSourcePositions()
    {
        //                     0123456789012345
        const string source = "let ab = 10i64;";

        ImmutableArray<Token> tokens = LexClean(source);

        Assert.Equal(new TextSpan(0, 3), tokens[0].Span);
        Assert.Equal(new TextSpan(4, 2), tokens[1].Span);
        Assert.Equal(new TextSpan(7, 1), tokens[2].Span);
        Assert.Equal(new TextSpan(9, 5), tokens[3].Span);
        Assert.Equal(new TextSpan(14, 1), tokens[4].Span);
    }

    [Fact]
    public void Lex_EmptySource_ProducesOnlyEndOfFile()
    {
        ImmutableArray<Token> tokens = Lex("", out ImmutableArray<Diagnostic> diagnostics);

        Token token = Assert.Single(tokens);
        Assert.Equal(SyntaxKind.EndOfFileToken, token.Kind);
        Assert.Equal(new TextSpan(0, 0), token.Span);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("@", "ARITH1001", 0, 1)]
    [InlineData("let @ = 1;", "ARITH1001", 4, 1)]
    [InlineData("&", "ARITH1001", 0, 1)]
    [InlineData("a | b", "ARITH1001", 2, 1)]
    [InlineData("\"abc", "ARITH1002", 0, 4)]
    [InlineData("\"abc\nxyz", "ARITH1002", 0, 4)]
    [InlineData("\"a\\", "ARITH1002", 0, 3)]
    [InlineData("\"a\\x1\"", "ARITH1003", 2, 2)]
    [InlineData("/* abc", "ARITH1004", 0, 6)]
    [InlineData("f\"a $ b\"", "ARITH1006", 4, 1)] // Spec §4.6: a bare $ must open a hole or be escaped.
    [InlineData("f\"a $x\"", "ARITH1006", 4, 1)]  // ($name shorthand is reserved, not supported.)
    [InlineData("\"a\\$\"", "ARITH1003", 2, 2)]   // \$ is only an escape inside f-strings.
    [InlineData("f\"abc", "ARITH1002", 0, 5)]
    [InlineData("f\"${x", "ARITH1002", 0, 5)]     // An unterminated hole is an unterminated literal.
    [InlineData("f\"${x // c}\"", "ARITH1002", 0, 12)] // A line comment swallows the rest of the line.
    [InlineData("f\"${1 /* }\"", "ARITH1002", 0, 11)] // ... as does an unterminated block comment.
    [InlineData("f\"a\\q\"", "ARITH1003", 3, 2)]
    [InlineData("10abc", "ARITH1005", 2, 3)]
    [InlineData("10f32", "ARITH1005", 2, 3)]
    [InlineData("1.5i64", "ARITH1005", 3, 3)]
    [InlineData("1.5x", "ARITH1005", 3, 1)]
    public void Lex_LexicalError_ReportsCodeAtSpan(
        string source, string expectedCode, int expectedStart, int expectedLength)
    {
        ImmutableArray<Token> tokens = Lex(source, out ImmutableArray<Diagnostic> diagnostics);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(new TextSpan(expectedStart, expectedLength), diagnostic.Span);
        Assert.Contains(tokens, t => t.Kind == SyntaxKind.BadToken);
    }

    [Fact]
    public void Lex_InterpolatedString_IsOneTokenRecordingItsSegments()
    {
        const string source = "f\"x = ${x}, y = ${y + 1}!\"";

        ImmutableArray<Token> tokens = LexClean(source);

        Token token = Assert.Single(tokens);
        Assert.Equal(SyntaxKind.InterpolatedStringToken, token.Kind);
        Assert.Equal(source, token.Text);
        (string Text, bool IsHole)[] segments =
            [.. token.Segments.Select(s => (source.Substring(s.Span.Start, s.Span.Length), s.IsHole))];
        (string, bool)[] expected =
            [("x = ", false), ("x", true), (", y = ", false), ("y + 1", true), ("!", false)];
        Assert.Equal(expected, segments);
    }

    [Theory]
    [InlineData("f\"\"", 0)]           // Empty literal: no segments at all.
    [InlineData("f\"${a}\"", 1)]       // Hole only: no empty text runs around it.
    [InlineData("f\"${a}${b}\"", 2)]   // ... nor between two holes.
    public void Lex_InterpolatedString_ProducesNoEmptyTextSegments(string source, int expectedCount)
    {
        ImmutableArray<Token> tokens = LexClean(source);

        Token token = Assert.Single(tokens);
        Assert.Equal(expectedCount, token.Segments.Length);
        Assert.All(token.Segments, s => Assert.True(s.IsHole));
    }

    [Fact]
    public void Lex_InterpolatedString_ManyHoles_UsesBoundedAllocations()
    {
        const int holeCount = 2000;
        string source = "f\"" + string.Concat(Enumerable.Repeat("${1}", holeCount)) + "\"";

        long before = GC.GetAllocatedBytesForCurrentThread();
        ImmutableArray<Token> tokens = Lex(source, out ImmutableArray<Diagnostic> diagnostics);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(diagnostics);
        Assert.Equal(2, tokens.Length);
        Assert.Equal(holeCount, tokens[0].Segments.Length);
        Assert.All(tokens[0].Segments, segment =>
        {
            Assert.True(segment.IsHole);
            Assert.Equal("1", source.Substring(segment.Span.Start, segment.Span.Length));
        });
        // A generous allocation ceiling avoids timing-sensitive assertions.
        // Re-tokenizing each remaining suffix allocates hundreds of MB here.
        Assert.True(allocated < 16 * 1024 * 1024, $"Lexing allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Lex_InterpolatedStrings_OnDifferentLines_KeepTheirHoleBoundaries()
    {
        const string source = """
            f"${1}${2}"
            f"longer: ${"}" /* } */ + f"${3}"}${4}"
            f"${5}"
            """;

        ImmutableArray<Token> tokens = LexClean(source);

        Assert.Equal(3, tokens.Length);
        string[] holes = [.. tokens.SelectMany(token => token.Segments)
            .Where(segment => segment.IsHole)
            .Select(segment => source.Substring(segment.Span.Start, segment.Span.Length))];
        Assert.Equal(["1", "2", "\"}\" /* } */ + f\"${3}\"", "4", "5"], holes);
    }

    [Fact]
    public void Lex_NestedInterpolatedString_TracksQuotesInsideTheHole()
    {
        // The nested literal's quotes and braces must not end the outer
        // hole early (design §7 brace/quote tracking).
        const string source = "f\"${f\"${name + \"!\"}\"}\"";

        ImmutableArray<Token> tokens = LexClean(source);

        Token token = Assert.Single(tokens);
        InterpolatedSegment hole = Assert.Single(token.Segments);
        Assert.True(hole.IsHole);
        Assert.Equal("f\"${name + \"!\"}\"", source.Substring(hole.Span.Start, hole.Span.Length));
    }

    [Fact]
    public void Lex_UnexpectedSurrogatePair_ProducesOneBadTokenForThePair()
    {
        ImmutableArray<Token> tokens = Lex("😀", out ImmutableArray<Diagnostic> diagnostics);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ARITH1001", diagnostic.Code);
        Assert.Equal("unexpected character '😀'", diagnostic.Message);
        Assert.Equal(new[] { SyntaxKind.BadToken, SyntaxKind.EndOfFileToken }, tokens.Select(t => t.Kind));
        Assert.Equal(new TextSpan(0, 2), tokens[0].Span);
    }

    [Fact]
    public void Lex_MultipleErrors_AllReportedInOnePass()
    {
        ImmutableArray<Token> tokens = Lex("let @ = 10abc; let $ = \"x\\q\";", out ImmutableArray<Diagnostic> diagnostics);

        string[] expectedCodes = ["ARITH1001", "ARITH1005", "ARITH1001", "ARITH1003"];
        Assert.Equal(expectedCodes, diagnostics.Select(d => d.Code));
        Assert.Equal(SyntaxKind.EndOfFileToken, tokens[^1].Kind);
    }

    [Fact]
    public void Lex_ErrorTokens_DoNotStopScanning()
    {
        // Scanning continues after an unterminated string on the next line.
        ImmutableArray<Token> tokens = Lex("\"abc\nlet x = 1;", out ImmutableArray<Diagnostic> diagnostics);

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ARITH1002", diagnostic.Code);
        Assert.Equal(
            new[]
            {
                SyntaxKind.BadToken,
                SyntaxKind.LetKeyword, SyntaxKind.IdentifierToken, SyntaxKind.EqualsToken,
                SyntaxKind.IntegerLiteralToken, SyntaxKind.SemicolonToken,
                SyntaxKind.EndOfFileToken,
            },
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Lex_SpecExampleProgram_LexesWithoutErrors()
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

        ImmutableArray<Token> tokens = LexClean(source);

        Assert.DoesNotContain(tokens, t => t.Kind == SyntaxKind.BadToken);
        // Spot-check a few structurally important tokens.
        Assert.Equal(2, tokens.Count(t => t.Kind == SyntaxKind.FnKeyword));
        Assert.Contains(tokens, t => t.Kind == SyntaxKind.DotDotToken);
        Assert.Contains(tokens, t => t is { Kind: SyntaxKind.StringLiteralToken, Text: "\"large:\"" });
        Assert.Contains(tokens, t => t is { Kind: SyntaxKind.ArrowToken });
    }
}
