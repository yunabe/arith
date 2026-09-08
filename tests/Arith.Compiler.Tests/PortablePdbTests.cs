using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

using Arith.Compiler.Binding;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

public sealed class PortablePdbTests
{
    private static EmitResult Emit(SourceText source, bool debug = false)
    {
        EmitResult result = Compilation.Create(SyntaxTree.Parse(source)).Emit("symbols", debug);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result;
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16")]
    public void Document_PreservesSuppliedPathAndHashOfOriginalBytes(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16" => Encoding.Unicode,
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes("fn main() { print(\"日本語\"); }\r\n")];
        string path = Path.Combine("sources", "symbols.arith");
        EmitResult result = Emit(SourceText.FromBytes(bytes, path));
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbImage(result.PdbImage);
        MetadataReader pdb = provider.GetMetadataReader();
        Document document = pdb.GetDocument(Assert.Single(pdb.Documents));

        Assert.Equal(path, pdb.GetString(document.Name));
        Assert.Equal(new Guid("8829d00f-11b8-4213-878b-770e8597ac16"), pdb.GetGuid(document.HashAlgorithm));
        Assert.Equal(SHA256.HashData(bytes), pdb.GetBlobBytes(document.Hash));
        Assert.True(document.Language.IsNil); // Do not select a C# expression evaluator for Arith.
    }

    [Theory]
    [InlineData("prog.arith")]
    [InlineData("../virtual/prog.arith")]
    [InlineData("memory://session/prog.arith")]
    [InlineData("invalid\0path.arith")]
    [InlineData("   ")]
    [InlineData("")]
    public void Document_DoesNotInterpretOrValidateCallerSuppliedNames(string path)
    {
        EmitResult result = Emit(SourceText.From("fn main() { }", path));
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbImage(result.PdbImage);
        MetadataReader pdb = provider.GetMetadataReader();
        Document document = pdb.GetDocument(Assert.Single(pdb.Documents));
        Assert.Equal(path.Length == 0 ? "symbols.arith" : path, pdb.GetString(document.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DebugMode_ControlsTheAssemblyDebuggableAttribute(bool debug)
    {
        EmitResult result = Compilation.Create(SyntaxTree.Parse(SourceText.From("fn main() { }")))
            .Emit("symbols", debug: debug);
        Assert.True(result.Success);
        using PEReader pe = new(result.PeImage);
        MetadataReader metadata = pe.GetMetadataReader();
        CustomAttributeHandleCollection attributes = metadata.GetAssemblyDefinition().GetCustomAttributes();
        if (!debug)
        {
            Assert.Empty(attributes);
            return;
        }

        CustomAttribute attribute = metadata.GetCustomAttribute(Assert.Single(attributes));
        MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        Assert.Equal("System.Diagnostics", metadata.GetString(type.Namespace));
        Assert.Equal("DebuggableAttribute", metadata.GetString(type.Name));
        Assert.Equal(".ctor", metadata.GetString(constructor.Name));
        Assert.Equal(new byte[] { 1, 0, 1, 1, 0, 0 }, metadata.GetBlobBytes(attribute.Value));
    }

    [Fact]
    public void PeDebugDirectory_LinksToMatchingPortablePdb()
    {
        EmitResult result = Emit(SourceText.From("fn main() { print(1); }"));
        using PEReader pe = new(result.PeImage);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbImage(result.PdbImage);
        MetadataReader pdb = provider.GetMetadataReader();
        DebugDirectoryEntry codeViewEntry = Assert.Single(pe.ReadDebugDirectory(), e => e.Type == DebugDirectoryEntryType.CodeView);
        CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntry);
        BlobContentId id = new(pdb.DebugMetadataHeader!.Id);

        Assert.True(codeViewEntry.IsPortableCodeView);
        Assert.Equal("symbols.pdb", codeView.Path); // A sibling name survives moving the output directory.
        Assert.Equal(id.Guid, codeView.Guid);
        Assert.Equal(id.Stamp, codeViewEntry.Stamp);
        Assert.Equal(1, codeView.Age);
        Assert.Equal(pe.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress,
            MetadataTokens.GetToken(pdb.DebugMetadataHeader.EntryPoint));

        DebugDirectoryEntry checksumEntry = Assert.Single(pe.ReadDebugDirectory(), e => e.Type == DebugDirectoryEntryType.PdbChecksum);
        PdbChecksumDebugDirectoryData checksum = pe.ReadPdbChecksumDebugDirectoryData(checksumEntry);
        Assert.Equal("SHA256", checksum.AlgorithmName);
        Assert.Equal(SHA256.HashData(result.PdbImage.AsSpan()), checksum.Checksum.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Methods_HaveAlignedDebugRowsAndValidOffsetsIncludingGeneratedCode(bool debug)
    {
        EmitResult result = Emit(SourceText.From("""
            fn empty() { }
            fn loop(n: i64) {
                while n > 0 {
                    n -= 1;
                }
                let a = [1; n];
                for x in a { print(x); }
                for i in 0..n { if i == 1 { continue; } }
                for i in 0..=n { if i == 2 { break; } }
            }
            fn main(n: i64) { loop(n); }
            fn branches(flag: bool) -> i64 { if flag { return 1; } else { return 2; } }
            """), debug);
        using PEReader pe = new(result.PeImage);
        MetadataReader metadata = pe.GetMetadataReader();
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbImage(result.PdbImage);
        MetadataReader pdb = provider.GetMetadataReader();
        Assert.Equal(metadata.MethodDefinitions.Count, pdb.MethodDebugInformation.Count);

        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
            MethodDebugInformation information = pdb.GetMethodDebugInformation(handle);
            SequencePoint[] points = [.. information.GetSequencePoints()];
            string name = metadata.GetString(method.Name);
            if (name == "<Main>")
            {
                Assert.Empty(points);
                Assert.True(information.Document.IsNil);
                continue;
            }

            Assert.Equal(body.LocalSignature, information.LocalSignature);
            Assert.NotEmpty(points);
            int previousOffset = -1;
            foreach (SequencePoint point in points)
            {
                Assert.InRange(point.Offset, previousOffset + 1, body.GetILBytes()!.Length - 1);
                previousOffset = point.Offset;
                Assert.Equal(Assert.Single(pdb.Documents), point.Document);
                if (!point.IsHidden)
                {
                    Assert.InRange(point.StartLine, 1, point.EndLine);
                    Assert.InRange(point.StartColumn, 1, ushort.MaxValue - 2);
                    Assert.InRange(point.EndColumn, 1, ushort.MaxValue - 1);
                }
            }

            if (name == "empty")
            {
                Assert.True(Assert.Single(points).IsHidden);
            }
            else if (name == "loop")
            {
                // Optimized output starts at the hidden branch; debug output
                // first inserts a visible nop boundary for the while statement.
                Assert.Equal(!debug, points[0].IsHidden);
                Assert.Contains(points, p => p.StartLine == 4);
                // The while test follows the body in IL, but precedes it in source.
                SequencePoint[] visible = [.. points.Where(p => !p.IsHidden)];
                Assert.Contains(Enumerable.Range(1, visible.Length - 1),
                    i => visible[i].StartLine == 3 && visible[i - 1].StartLine == 4);
            }
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void MultilineArithmetic_MapsTheThrowingInstructionToTheWholeExpression(string newline)
    {
        string code = string.Join(newline, "fn main() {", "    print(10 /", "        0);", "}");
        EmitResult result = Emit(SourceText.From(code));
        using PEReader pe = new(result.PeImage);
        MethodDefinitionHandle main = pe.GetMetadataReader().MethodDefinitions.First();
        MethodDefinition method = pe.GetMetadataReader().GetMethodDefinition(main);
        byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbImage(result.PdbImage);
        SequencePoint[] points = [.. provider.GetMetadataReader().GetMethodDebugInformation(main).GetSequencePoints()];
        SequencePoint division = Assert.Single(points, p => !p.IsHidden && il[p.Offset] == (byte)ILOpCode.Div);

        Assert.Equal(2, division.StartLine);
        Assert.Equal(11, division.StartColumn);
        Assert.Equal(3, division.EndLine);
        Assert.Equal(10, division.EndColumn);
    }

    [Fact]
    public void PendingLiteralResolution_PreservesNestedSourceSpans()
    {
        const string code = "fn main() { let x = -(1 + -2); }";
        Compilation compilation = Compilation.Create(SyntaxTree.Parse(SourceText.From(code)));
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Severity == Diagnostics.DiagnosticSeverity.Error);
        BoundLetStatement let = Assert.IsType<BoundLetStatement>(compilation.Program.Functions[0].Body.Statements[0]);
        BoundUnaryExpression unary = Assert.IsType<BoundUnaryExpression>(let.Initializer);
        BoundBinaryExpression sum = Assert.IsType<BoundBinaryExpression>(unary.Operand);

        Assert.Equal("let x = -(1 + -2);", code.Substring(let.Span!.Value.Start, let.Span.Value.Length));
        Assert.Equal("-(1 + -2)", code.Substring(unary.Span!.Value.Start, unary.Span.Value.Length));
        Assert.Equal("(1 + -2)", code.Substring(sum.Span!.Value.Start, sum.Span.Value.Length));
        Assert.Equal("1", code.Substring(sum.Left.Span!.Value.Start, sum.Left.Span.Value.Length));
        Assert.Equal("-2", code.Substring(sum.Right.Span!.Value.Start, sum.Right.Span.Value.Length));
    }

    [Fact]
    public void MultilineArrayRepeat_MapsCheckedCountConversionToTheRepeatExpression()
    {
        EmitResult result = Emit(SourceText.From("""
            fn main(n: i64) {
                print(n);
                let a = [0;
                    n];
            }
            """));
        using PEReader pe = new(result.PeImage);
        MethodDefinitionHandle main = pe.GetMetadataReader().MethodDefinitions.First();
        MethodDefinition method = pe.GetMetadataReader().GetMethodDefinition(main);
        byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
        int conversion = Array.IndexOf(il, (byte)ILOpCode.Conv_ovf_u);
        Assert.True(conversion >= 0);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbImage(result.PdbImage);
        SequencePoint point = provider.GetMetadataReader().GetMethodDebugInformation(main)
            .GetSequencePoints().Last(p => p.Offset <= conversion);

        Assert.False(point.IsHidden);
        Assert.Equal(3, point.StartLine);
        Assert.Equal(13, point.StartColumn);
        Assert.Equal(4, point.EndLine);
        Assert.Equal(11, point.EndColumn);
    }

    [Fact]
    public void VeryLongLine_ProducesRepresentableColumns()
    {
        string code = "fn main() {" + new string(' ', 70_000) + "print(1); }";
        EmitResult result = Emit(SourceText.From(code));
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbImage(result.PdbImage);
        SequencePoint[] points = [.. provider.GetMetadataReader()
            .GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(1)).GetSequencePoints()];
        Assert.Contains(points, p => !p.IsHidden && p.StartColumn == 65533 && p.EndColumn == 65534);
    }

    [Fact]
    public void InvalidProgram_EmitsNeitherImage()
    {
        EmitResult result = Compilation.Create(SyntaxTree.Parse(SourceText.From("fn main() { print(missing); }"))).Emit("bad");
        Assert.False(result.Success);
        Assert.Empty(result.PeImage);
        Assert.Empty(result.PdbImage);
    }
}
