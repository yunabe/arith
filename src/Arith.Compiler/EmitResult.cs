using System.Collections.Immutable;

using Arith.Compiler.Diagnostics;

namespace Arith.Compiler;

/// <summary>
/// The result of <see cref="Compilation.Emit"/>: in-memory PE and Portable
/// PDB images plus all accumulated diagnostics. Writing artifacts and AOT
/// packaging are the CLI's job, consuming these bytes (design §4.6).
/// </summary>
public sealed class EmitResult
{
    internal EmitResult(
        bool success, ImmutableArray<Diagnostic> diagnostics,
        ImmutableArray<byte> peImage, ImmutableArray<byte> pdbImage)
    {
        Success = success;
        Diagnostics = diagnostics;
        PeImage = peImage;
        PdbImage = pdbImage;
    }

    public bool Success { get; }

    /// <summary>Every diagnostic from every stage, syntax first.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>The emitted assembly bytes; empty unless <see cref="Success"/>.</summary>
    public ImmutableArray<byte> PeImage { get; }

    /// <summary>The matching Portable PDB bytes; empty unless <see cref="Success"/>.</summary>
    public ImmutableArray<byte> PdbImage { get; }
}
