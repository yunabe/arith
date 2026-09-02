using System.Collections.Immutable;

using Arith.Compiler.Text;

namespace Arith.Compiler.Syntax;

/// <summary>
/// Base of the untyped syntax tree. Nodes are immutable records mirroring the
/// grammar (spec §12); consumers switch on the concrete node type — with an
/// explicit fallback arm that throws, since C# cannot check exhaustiveness
/// over an open hierarchy (see docs/compiler-design.md §4.3).
/// </summary>
public abstract record SyntaxNode(TextSpan Span);

/// <summary>A whole source file: its top-level function declarations.</summary>
public sealed record CompilationUnitSyntax(
    ImmutableArray<FunctionDeclarationSyntax> Functions,
    TextSpan Span) : SyntaxNode(Span);

/// <summary><c>fn name(params) [-&gt; type] block</c>. A null ReturnType means the function returns no value.</summary>
public sealed record FunctionDeclarationSyntax(
    Token Identifier,
    ImmutableArray<ParameterSyntax> Parameters,
    TypeSyntax? ReturnType,
    BlockSyntax Body,
    TextSpan Span) : SyntaxNode(Span);

/// <summary>One <c>name: type</c> function parameter.</summary>
public sealed record ParameterSyntax(
    Token Identifier,
    TypeSyntax Type,
    TextSpan Span) : SyntaxNode(Span);

/// <summary>
/// A type name in source: one of the six type keywords. The keyword token is
/// missing when the parser expected a type and found something else.
/// </summary>
public sealed record TypeSyntax(Token Keyword, TextSpan Span) : SyntaxNode(Span);
