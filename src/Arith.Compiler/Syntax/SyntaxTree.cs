using System.Collections.Immutable;

using Arith.Compiler.Diagnostics;
using Arith.Compiler.Text;

namespace Arith.Compiler.Syntax;

/// <summary>
/// The syntactic half of the pipeline: lexes and parses one source document.
/// Parse never fails — errors are collected in <see cref="Diagnostics"/> and
/// the tree is complete regardless (see docs/compiler-design.md §3). Semantic
/// analysis is the Compilation's job, not this type's.
/// </summary>
public sealed class SyntaxTree
{
    private SyntaxTree(SourceText text, CompilationUnitSyntax root, ImmutableArray<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        Diagnostics = diagnostics;
    }

    public SourceText Text { get; }

    public CompilationUnitSyntax Root { get; }

    /// <summary>All lexical and syntactic diagnostics, in source order per stage.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public static SyntaxTree Parse(SourceText text)
    {
        DiagnosticBag diagnostics = new();
        ImmutableArray<Token> tokens = Lexer.Lex(text, diagnostics);
        CompilationUnitSyntax root = Parser.Parse(tokens, diagnostics);
        return new SyntaxTree(text, root, diagnostics.ToImmutableArray());
    }
}
