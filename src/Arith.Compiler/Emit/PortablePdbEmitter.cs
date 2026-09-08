using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

using Arith.Compiler.Text;

namespace Arith.Compiler.Emit;

/// <summary>
/// Writes the debugging half of an assembly: one Document and one
/// MethodDebugInformation row per MethodDef, in the same order. The sequence
/// point blob format is defined by dotnet/runtime's PortablePdb-Metadata.md.
/// The PE's CodeView entry links the two files by PDB name and content ID.
/// </summary>
internal sealed class PortablePdbEmitter
{
    private static readonly Guid Sha256Algorithm = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private readonly MetadataBuilder _metadata = new();
    private readonly SourceText _source;
    private readonly DocumentHandle _document;

    public PortablePdbEmitter(SourceText source, string assemblyName)
    {
        _source = source;
        string documentName = string.IsNullOrEmpty(source.FilePath)
            ? assemblyName + ".arith" : Path.GetFullPath(source.FilePath);
        _document = _metadata.AddDocument(
            _metadata.GetOrAddDocumentName(documentName),
            _metadata.GetOrAddGuid(Sha256Algorithm),
            _metadata.GetOrAddBlob(source.Checksum),
            language: default); // Arith does not use another language's debugger/evaluator GUID.
    }

    public void AddMethod(StandaloneSignatureHandle locals, IReadOnlyList<SourceSequencePoint> points)
    {
        if (points.Count == 0)
        {
            // The synthesized entry-point bridge has no source locations.
            _metadata.AddMethodDebugInformation(default, default);
            return;
        }

        BlobBuilder blob = new();
        blob.WriteCompressedInteger(MetadataTokens.GetRowNumber(locals));
        int previousOffset = 0;
        LinePosition? previousStart = null;
        foreach (SourceSequencePoint point in points)
        {
            blob.WriteCompressedInteger(point.Offset - previousOffset);
            previousOffset = point.Offset;
            if (point.Span is not { } span)
            {
                // A hidden point has zero line and column deltas and no
                // start-position fields. It does not reset previousStart.
                blob.WriteCompressedInteger(0);
                blob.WriteCompressedInteger(0);
                continue;
            }

            LinePosition start = _source.GetLinePosition(span.Start);
            LinePosition end = _source.GetLinePosition(span.End);
            if (end.Line >= SequencePoint.HiddenLine)
            {
                // Lines at/above the hidden marker cannot be represented.
                blob.WriteCompressedInteger(0);
                blob.WriteCompressedInteger(0);
                continue;
            }

            // The metadata reader requires columns strictly below 0xffff.
            // Clamp long lines, leaving room for a nonempty same-line range.
            start = start with { Column = Math.Min(start.Column, ushort.MaxValue - 2) };
            end = end with { Column = Math.Min(end.Column, ushort.MaxValue - 1) };
            int deltaLines = end.Line - start.Line;
            blob.WriteCompressedInteger(deltaLines);
            if (deltaLines == 0)
            {
                blob.WriteCompressedInteger(Math.Max(1, end.Column - start.Column));
            }
            else
            {
                blob.WriteCompressedSignedInteger(end.Column - start.Column);
            }

            if (previousStart is { } previous)
            {
                // Loop tests can follow their bodies in IL order, so source
                // line deltas may be negative even though IL offsets increase.
                blob.WriteCompressedSignedInteger(start.Line - previous.Line);
                blob.WriteCompressedSignedInteger(start.Column - previous.Column);
            }
            else
            {
                blob.WriteCompressedInteger(start.Line);
                blob.WriteCompressedInteger(start.Column);
            }

            previousStart = start;
        }

        _metadata.AddMethodDebugInformation(_document, _metadata.GetOrAddBlob(blob));
    }

    public ImmutableArray<byte> Serialize(
        ImmutableArray<int> typeSystemRowCounts, MethodDefinitionHandle entryPoint,
        string pdbName, DebugDirectoryBuilder debugDirectory)
    {
        PortablePdbBuilder builder = new(_metadata, typeSystemRowCounts, entryPoint, ComputeContentId);
        BlobBuilder blob = new();
        BlobContentId contentId = builder.Serialize(blob);
        byte[] bytes = blob.ToArray();
        debugDirectory.AddCodeViewEntry(pdbName, contentId, builder.FormatVersion);
        debugDirectory.AddPdbChecksumEntry("SHA256", [.. SHA256.HashData(bytes)]);
        return [.. bytes];
    }

    private static BlobContentId ComputeContentId(IEnumerable<Blob> blobs)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (Blob blob in blobs)
        {
            hash.AppendData(blob.GetBytes().AsSpan());
        }

        return BlobContentId.FromHash(hash.GetHashAndReset());
    }
}

/// <summary>A method-relative IL offset and its source range; null means hidden generated code.</summary>
internal readonly record struct SourceSequencePoint(int Offset, TextSpan? Span);
