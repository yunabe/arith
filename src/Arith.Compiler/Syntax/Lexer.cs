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
    private int _position;

    private Lexer(SourceText text, DiagnosticBag diagnostics)
    {
        _text = text;
        _diagnostics = diagnostics;
    }

    public static ImmutableArray<Token> Lex(SourceText text, DiagnosticBag diagnostics)
    {
        Lexer lexer = new(text, diagnostics);
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

    private bool AtEnd => _position >= _text.Length;

    private char Current => Peek(0);

    private char Lookahead => Peek(1);

    private char Peek(int offset)
    {
        int index = _position + offset;
        return index < _text.Length ? _text[index] : '\0';
    }

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

            if (c == '/' && Lookahead == '/')
            {
                while (!AtEnd && Current is not ('\n' or '\r'))
                {
                    _position++;
                }

                continue;
            }

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
                else if (_position + 1 >= _text.Length || Lookahead is '\n' or '\r')
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
