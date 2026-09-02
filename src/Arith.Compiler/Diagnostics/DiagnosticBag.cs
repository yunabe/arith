using System.Collections;
using System.Collections.Immutable;

using Arith.Compiler.Text;

namespace Arith.Compiler.Diagnostics;

/// <summary>
/// The append-only diagnostic collection threaded through every compiler stage.
/// Stages report here and keep going; only emission requires an error-free bag.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = [];

    public bool HasErrors { get; private set; }

    public void Report(DiagnosticDescriptor descriptor, TextSpan span, params object[] args)
    {
        Diagnostic diagnostic = Diagnostic.Create(descriptor, span, args);
        _diagnostics.Add(diagnostic);
        HasErrors |= diagnostic.Severity == DiagnosticSeverity.Error;
    }

    public ImmutableArray<Diagnostic> ToImmutableArray() => [.. _diagnostics];

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
