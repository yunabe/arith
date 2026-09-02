using System.Collections.Immutable;

using Arith.Compiler.Binding;
using Arith.Compiler.Diagnostics;
using Arith.Compiler.Emit;
using Arith.Compiler.Syntax;

namespace Arith.Compiler;

/// <summary>
/// The semantic half of the pipeline (design §4.6): created from a parsed
/// <see cref="SyntaxTree"/>, it binds the program and accumulates the
/// tree's diagnostics with its own. Emission (step 5) will live here too
/// and requires <see cref="HasErrors"/> to be false.
/// </summary>
public sealed class Compilation
{
    private Compilation(SyntaxTree syntaxTree, BoundProgram program, ImmutableArray<Diagnostic> diagnostics, bool hasErrors)
    {
        SyntaxTree = syntaxTree;
        Program = program;
        Diagnostics = diagnostics;
        HasErrors = hasErrors;
    }

    public SyntaxTree SyntaxTree { get; }

    /// <summary>The bound program. Complete even when there are errors; only emission requires an error-free compile.</summary>
    public BoundProgram Program { get; }

    /// <summary>All diagnostics: the syntax tree's followed by binding's.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public bool HasErrors { get; }

    public static Compilation Create(SyntaxTree syntaxTree)
    {
        DiagnosticBag diagnostics = new();
        foreach (Diagnostic diagnostic in syntaxTree.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        BoundProgram program = Binder.Bind(syntaxTree.Root, diagnostics);
        return new Compilation(syntaxTree, program, diagnostics.ToImmutableArray(), diagnostics.HasErrors);
    }

    /// <summary>
    /// Emits the program as an in-memory PE image. Emission is gated on an
    /// error-free compile (design §3): with errors, the result carries the
    /// diagnostics and no image.
    /// </summary>
    public EmitResult Emit(string assemblyName)
    {
        if (HasErrors)
        {
            return new EmitResult(success: false, Diagnostics, peImage: []);
        }

        ImmutableArray<byte> peImage = Emitter.Emit(Program, assemblyName);
        return new EmitResult(success: true, Diagnostics, peImage);
    }
}
