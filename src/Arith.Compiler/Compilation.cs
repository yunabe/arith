using System.Collections.Immutable;

using Arith.Compiler.Binding;
using Arith.Compiler.Diagnostics;
using Arith.Compiler.Emit;
using Arith.Compiler.Syntax;

namespace Arith.Compiler;

/// <summary>
/// The semantic half of the pipeline (design §4.6): created from a parsed
/// <see cref="SyntaxTree"/>, it binds the program and accumulates the
/// tree's diagnostics with its own. <see cref="Emit"/> requires
/// <see cref="HasErrors"/> to be false and can still fail on a target
/// limit (ARITH4xxx), reported the same way.
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

    /// <summary>All front-end diagnostics: the syntax tree's followed by binding's.</summary>
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
    /// Emits the program as matching in-memory PE and Portable PDB images. Emission is gated on an
    /// error-free compile (design §3): with errors, the result carries the
    /// diagnostics and neither image. A well-typed program can still exceed
    /// a limit of the target runtime (ARITH4xxx); the emitter reports that
    /// like any other error rather than producing an assembly the runtime
    /// would reject when the method is called.
    /// </summary>
    /// <param name="assemblyName">The emitted assembly's simple name.</param>
    /// <param name="debug">Disable JIT optimizations and add IL source boundaries for fault lines and call frames.</param>
    public EmitResult Emit(string assemblyName, bool debug = false)
    {
        if (HasErrors)
        {
            return new EmitResult(success: false, Diagnostics, peImage: [], pdbImage: []);
        }

        DiagnosticBag diagnostics = new();
        foreach (Diagnostic diagnostic in Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        (ImmutableArray<byte> peImage, ImmutableArray<byte> pdbImage) =
            Emitter.Emit(Program, assemblyName, SyntaxTree.Text, diagnostics, debug);
        return new EmitResult(
            success: !diagnostics.HasErrors, diagnostics.ToImmutableArray(), peImage, pdbImage);
    }
}
