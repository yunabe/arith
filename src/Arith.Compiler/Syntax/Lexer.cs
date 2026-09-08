using System.Collections.Immutable;

using Arith.Compiler.Diagnostics;
using Arith.Compiler.Text;

namespace Arith.Compiler.Syntax;

/// <summary>
/// Converts Arith source text into a flat token array ending with an
/// EndOfFile token. Lexical errors are reported to the diagnostic bag as a
/// Bad token, and scanning continues, so one pass surfaces every lexical
/// error in the file.
/// </summary>
public sealed class Lexer
{
    private readonly SourceText _text;
    private readonly DiagnosticBag _diagnostics;
    private readonly int _end;
    private int _position;
    private int _lineEnd = -1;

    private Lexer(SourceText text, DiagnosticBag diagnostics, int start, int end)
    {
        _text = text;
        _diagnostics = diagnostics;
        _position = start;
        _end = end;
    }

    public static ImmutableArray<Token> Lex(SourceText text, DiagnosticBag diagnostics) =>
        LexRange(text, new TextSpan(0, text.Length), diagnostics);

    /// <summary>
    /// Lexes just the given range — used for the `${…}` holes of an
    /// interpolated string (design §7), so hole tokens and diagnostics keep
    /// their real source positions. The EndOfFile token sits at the range's
    /// end.
    /// </summary>
    internal static ImmutableArray<Token> LexRange(SourceText text, TextSpan range, DiagnosticBag diagnostics)
    {
        Lexer lexer = new(text, diagnostics, range.Start, range.End);
        ImmutableArray<Token>.Builder tokens = ImmutableArray.CreateBuilder<Token>();
        Token token;
        do
        {
            token = lexer.NextToken();
            tokens.Add(token);
        }
        while (token.Kind != SyntaxKind.EndOfFileToken);
        return tokens.ToImmutable();
    }

    private bool AtEnd => _position >= _end;

    private char Current => Peek(0);

    private char Lookahead => Peek(1);

    private char Peek(int offset)
    {
        int index = _position + offset;
        return index < _end ? _text[index] : '\0';
    }

    /// <summary>
    /// Scans the next token. The loop discards whitespace and comments
    /// (spec §2.3) — they produce no tokens — then the first significant
    /// character alone decides the token class, and the class-specific
    /// method consumes the rest.
    /// </summary>
    private Token NextToken()
    {
        while (true)
        {
            if (AtEnd)
            {
                return new Token(SyntaxKind.EndOfFileToken, new TextSpan(_position, 0), "");
            }

            char c = Current;
            if (char.IsWhiteSpace(c))
            {
                _position++;
                continue;
            }

            // Line comment: runs to (but not past) the end of the line.
            if (c == '/' && Lookahead == '/')
            {
                while (!AtEnd && Current is not ('\n' or '\r'))
                {
                    _position++;
                }

                continue;
            }

            // Block comment: normally skipped like whitespace; only an
            // unterminated one surfaces as a token (a Bad one).
            if (c == '/' && Lookahead == '*')
            {
                Token? unterminated = SkipBlockComment();
                if (unterminated is { } bad)
                {
                    return bad;
                }

                continue;
            }

            return c switch
            {
                '"' => LexString(),
                'f' when Lookahead == '"' => LexInterpolatedString(),
                >= '0' and <= '9' => LexNumber(),
                (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' => LexIdentifierOrKeyword(),
                _ => LexOperatorOrUnexpected(),
            };
        }
    }

    /// <summary>Skips a block comment, or returns a Bad token if it is unterminated (spec §2.3: no nesting).</summary>
    private Token? SkipBlockComment()
    {
        int start = _position;
        _position += 2;
        while (!AtEnd)
        {
            if (Current == '*' && Lookahead == '/')
            {
                _position += 2;
                return null;
            }

            _position++;
        }

        TextSpan span = TextSpan.FromBounds(start, _position);
        _diagnostics.Report(ErrorCodes.UnterminatedBlockComment, span);
        return MakeToken(SyntaxKind.BadToken, span);
    }

    /// <summary>
    /// Lexes a double-quoted string literal (spec §4.4). The token keeps the
    /// raw text, quotes and escapes included; unescaping happens later. A
    /// newline or end of file inside the literal makes it unterminated — the
    /// token then ends at the newline so the next line lexes normally.
    /// </summary>
    private Token LexString()
    {
        int start = _position;
        _position++;
        bool hasError = false;
        while (true)
        {
            if (AtEnd || Current is '\n' or '\r')
            {
                _diagnostics.Report(ErrorCodes.UnterminatedStringLiteral, TextSpan.FromBounds(start, _position));
                hasError = true;
                break;
            }

            char c = Current;
            if (c == '"')
            {
                _position++;
                break;
            }

            if (c == '\\')
            {
                // Spec §4.4: \n \r \t \" \\ are the only escape sequences.
                if (Lookahead is 'n' or 'r' or 't' or '"' or '\\')
                {
                    _position += 2;
                }
                else if (_position + 1 >= _end || Lookahead is '\n' or '\r')
                {
                    // A lone backslash at the end of the line/file: the
                    // enclosing loop reports the unterminated string.
                    _position++;
                }
                else
                {
                    TextSpan escapeSpan = new(_position, 2);
                    _diagnostics.Report(
                        ErrorCodes.InvalidEscapeSequence, escapeSpan, _text.ToString(escapeSpan));
                    hasError = true;
                    _position += 2;
                }
            }
            else
            {
                _position++;
            }
        }

        SyntaxKind kind = hasError ? SyntaxKind.BadToken : SyntaxKind.StringLiteralToken;
        return MakeToken(kind, TextSpan.FromBounds(start, _position));
    }

    /// <summary>
    /// Lexes an <c>f"…"</c> interpolated string (spec §4.6) as one token
    /// whose Segments record the text runs and `${…}` hole spans; the
    /// parser re-lexes each hole with <see cref="LexRange"/> and desugars
    /// the whole token, so no later stage needs a new node kind (design
    /// §7). Text runs follow the plain-string escape rules plus `\$`; a
    /// bare `$` is a lexical error. A hole's matching `}` is found by
    /// tokenizing (see <see cref="ScanHole"/>), so strings, block comments,
    /// and nested interpolated strings inside it skip as whole units.
    /// </summary>
    private Token LexInterpolatedString()
    {
        int start = _position;
        _position += 2; // f"
        bool hasError = false;
        ImmutableArray<InterpolatedSegment>.Builder segments =
            ImmutableArray.CreateBuilder<InterpolatedSegment>();
        int runStart = _position;

        void FlushRun(int end)
        {
            if (end > runStart)
            {
                segments.Add(new InterpolatedSegment(TextSpan.FromBounds(runStart, end), IsHole: false));
            }
        }

        while (true)
        {
            if (AtEnd || Current is '\n' or '\r')
            {
                _diagnostics.Report(ErrorCodes.UnterminatedStringLiteral, TextSpan.FromBounds(start, _position));
                hasError = true;
                FlushRun(_position);
                break;
            }

            char c = Current;
            if (c == '"')
            {
                FlushRun(_position);
                _position++;
                break;
            }

            if (c == '\\')
            {
                // The plain-string escapes (spec §4.4) plus \$ for a
                // literal dollar sign (spec §4.6).
                if (Lookahead is 'n' or 'r' or 't' or '"' or '\\' or '$')
                {
                    _position += 2;
                }
                else if (_position + 1 >= _end || Lookahead is '\n' or '\r')
                {
                    _position++; // The enclosing loop reports the unterminated string.
                }
                else
                {
                    TextSpan escapeSpan = new(_position, 2);
                    _diagnostics.Report(
                        ErrorCodes.InvalidEscapeSequence, escapeSpan, _text.ToString(escapeSpan));
                    hasError = true;
                    _position += 2;
                }

                continue;
            }

            if (c == '$')
            {
                if (Lookahead != '{')
                {
                    _diagnostics.Report(
                        ErrorCodes.BareDollarInInterpolatedString, new TextSpan(_position, 1));
                    hasError = true;
                    _position++;
                    continue;
                }

                FlushRun(_position);
                _position += 2; // ${
                if (!ScanHole(out TextSpan holeSpan))
                {
                    _diagnostics.Report(
                        ErrorCodes.UnterminatedStringLiteral, TextSpan.FromBounds(start, _position));
                    hasError = true;
                    break;
                }

                segments.Add(new InterpolatedSegment(holeSpan, IsHole: true));
                runStart = _position;
                continue;
            }

            _position++;
        }

        TextSpan span = TextSpan.FromBounds(start, _position);
        SyntaxKind kind = hasError ? SyntaxKind.BadToken : SyntaxKind.InterpolatedStringToken;
        return new Token(kind, span, _text.ToString(span)) { Segments = segments.ToImmutable() };
    }

    /// <summary>
    /// Finds the `}` matching an already-consumed `${` by reading tokens
    /// within the current line — with a throwaway diagnostic bag, since the real
    /// lexing and reporting happen when the parser re-lexes the hole — and
    /// tracking brace-token depth. Tokenizing is what makes hole contents
    /// scan correctly: a string, a block comment, or a nested interpolated
    /// string is one skipped unit, so a `}` or `"` inside it cannot end
    /// the hole early. False when the line (or range) ends first: the
    /// hole, and with it the literal, is unterminated.
    /// </summary>
    private bool ScanHole(out TextSpan span)
    {
        int start = _position;
        int lineEnd = GetLineEnd();
        // Read only through the matching brace. LexRange materializes the
        // entire suffix before returning, which makes adjacent holes do
        // quadratic work. Share the known line end with nested scanners.
        Lexer holeLexer = new(_text, new DiagnosticBag(), start, lineEnd) { _lineEnd = lineEnd };
        int depth = 1;
        while (true)
        {
            Token token = holeLexer.NextToken();
            if (token.Kind == SyntaxKind.EndOfFileToken)
            {
                break;
            }

            if (token.Kind == SyntaxKind.OpenBraceToken)
            {
                depth++;
            }
            else if (token.Kind == SyntaxKind.CloseBraceToken && --depth == 0)
            {
                span = TextSpan.FromBounds(start, token.Span.Start);
                _position = token.Span.End;
                return true;
            }
        }

        span = TextSpan.FromBounds(start, lineEnd);
        _position = lineEnd;
        return false;
    }

    /// <summary>Finds the current line's end once, even when it contains many holes.</summary>
    private int GetLineEnd()
    {
        if (_position > _lineEnd)
        {
            _lineEnd = _position;
            while (_lineEnd < _end && _text[_lineEnd] is not ('\n' or '\r'))
            {
                _lineEnd++;
            }
        }

        return _lineEnd;
    }

    /// <summary>
    /// Lexes a decimal integer or floating-point literal, with an optional
    /// i32/i64/f32/f64 type suffix (spec §4.2–§4.3). The token records only
    /// where the literal is and whether it is integer- or float-shaped; the
    /// value is parsed by the binder, whose expected type decides the valid
    /// range.
    /// </summary>
    private Token LexNumber()
    {
        int start = _position;
        while (char.IsAsciiDigit(Current))
        {
            _position++;
        }

        // A '.' continues the literal only when a digit follows; otherwise it
        // belongs to a range operator, as in `0..10`.
        bool isFloat = false;
        if (Current == '.' && char.IsAsciiDigit(Lookahead))
        {
            isFloat = true;
            _position++;
            while (char.IsAsciiDigit(Current))
            {
                _position++;
            }
        }

        // An identifier-shaped tail must be exactly a type suffix of the
        // literal's own category (spec §4.2/§4.3): i32/i64 on integers,
        // f32/f64 on floats. Anything else — 10abc, 10f32, 1.5i32 — is one
        // Bad token rather than a literal silently followed by an identifier.
        if (Current is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_')
        {
            int suffixStart = _position;
            while (Current is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
            {
                _position++;
            }

            TextSpan suffixSpan = TextSpan.FromBounds(suffixStart, _position);
            string suffix = _text.ToString(suffixSpan);
            bool isValidSuffix = isFloat ? suffix is "f32" or "f64" : suffix is "i32" or "i64";
            if (!isValidSuffix)
            {
                _diagnostics.Report(ErrorCodes.InvalidNumericSuffix, suffixSpan, suffix);
                return MakeToken(SyntaxKind.BadToken, TextSpan.FromBounds(start, _position));
            }
        }

        SyntaxKind kind = isFloat ? SyntaxKind.FloatLiteralToken : SyntaxKind.IntegerLiteralToken;
        return MakeToken(kind, TextSpan.FromBounds(start, _position));
    }

    /// <summary>
    /// Lexes <c>[A-Za-z_][A-Za-z0-9_]*</c> (spec §2.1, ASCII only) and
    /// classifies the word as a keyword or an identifier (§2.2).
    /// </summary>
    private Token LexIdentifierOrKeyword()
    {
        int start = _position;
        while (Current is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_')
        {
            _position++;
        }

        TextSpan span = TextSpan.FromBounds(start, _position);
        string text = _text.ToString(span);
        return new Token(SyntaxFacts.GetKeywordOrIdentifierKind(text), span, text);
    }

    /// <summary>
    /// Lexes operators and punctuation by longest match — two-character
    /// forms (and three-character <c>..=</c>) are listed before their
    /// one-character prefixes. A character starting no token at all — which
    /// includes a lone <c>&amp;</c> or <c>|</c>, since Arith has no bitwise
    /// operators — becomes a Bad token.
    /// </summary>
    private Token LexOperatorOrUnexpected()
    {
        char c = Current;
        char next = Lookahead;
        (SyntaxKind kind, int length) = (c, next) switch
        {
            ('(', _) => (SyntaxKind.OpenParenToken, 1),
            (')', _) => (SyntaxKind.CloseParenToken, 1),
            ('{', _) => (SyntaxKind.OpenBraceToken, 1),
            ('}', _) => (SyntaxKind.CloseBraceToken, 1),
            ('[', _) => (SyntaxKind.OpenBracketToken, 1),
            (']', _) => (SyntaxKind.CloseBracketToken, 1),
            (',', _) => (SyntaxKind.CommaToken, 1),
            (':', _) => (SyntaxKind.ColonToken, 1),
            (';', _) => (SyntaxKind.SemicolonToken, 1),
            ('.', '.') when Peek(2) == '=' => (SyntaxKind.DotDotEqualsToken, 3),
            ('.', '.') => (SyntaxKind.DotDotToken, 2),
            ('-', '>') => (SyntaxKind.ArrowToken, 2),
            ('+', '=') => (SyntaxKind.PlusEqualsToken, 2),
            ('+', _) => (SyntaxKind.PlusToken, 1),
            ('-', '=') => (SyntaxKind.MinusEqualsToken, 2),
            ('-', _) => (SyntaxKind.MinusToken, 1),
            ('*', '=') => (SyntaxKind.StarEqualsToken, 2),
            ('*', _) => (SyntaxKind.StarToken, 1),
            ('/', '=') => (SyntaxKind.SlashEqualsToken, 2),
            ('/', _) => (SyntaxKind.SlashToken, 1),
            ('%', '=') => (SyntaxKind.PercentEqualsToken, 2),
            ('%', _) => (SyntaxKind.PercentToken, 1),
            ('=', '=') => (SyntaxKind.EqualsEqualsToken, 2),
            ('=', _) => (SyntaxKind.EqualsToken, 1),
            ('!', '=') => (SyntaxKind.BangEqualsToken, 2),
            ('!', _) => (SyntaxKind.BangToken, 1),
            ('<', '=') => (SyntaxKind.LessEqualsToken, 2),
            ('<', _) => (SyntaxKind.LessToken, 1),
            ('>', '=') => (SyntaxKind.GreaterEqualsToken, 2),
            ('>', _) => (SyntaxKind.GreaterToken, 1),
            ('&', '&') => (SyntaxKind.AmpersandAmpersandToken, 2),
            ('|', '|') => (SyntaxKind.PipePipeToken, 2),
            _ => (SyntaxKind.BadToken, char.IsHighSurrogate(c) && char.IsLowSurrogate(next) ? 2 : 1),
        };

        TextSpan span = new(_position, length);
        if (kind == SyntaxKind.BadToken)
        {
            _diagnostics.Report(ErrorCodes.UnexpectedCharacter, span, _text.ToString(span));
        }

        _position += length;
        return MakeToken(kind, span);
    }

    private Token MakeToken(SyntaxKind kind, TextSpan span) => new(kind, span, _text.ToString(span));
}
