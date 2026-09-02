using System.Collections.Immutable;

using Arith.Compiler.Diagnostics;

namespace Arith.Compiler;

/// <summary>
/// The result of <see cref="Compilation.Emit"/>: the in-memory PE image plus
/// all accumulated diagnostics. The compiler library never touches the file
/// system — writing the dll, runtimeconfig, and launchers (and any future
/// AOT packaging) is the CLI artifact writer's job, consuming these bytes
/// (design §4.6).
/// </summary>
public sealed class EmitResult
{
    internal EmitResult(bool success, ImmutableArray<Diagnostic> diagnostics, ImmutableArray<byte> peImage)
    {
        Success = success;
        Diagnostics = diagnostics;
        PeImage = peImage;
    }

    public bool Success { get; }

    /// <summary>Every diagnostic from every stage, syntax first.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>The emitted assembly bytes; empty unless <see cref="Success"/>.</summary>
    public ImmutableArray<byte> PeImage { get; }
}
