using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

using ILVerify;

namespace Arith.Compiler.Tests;

public sealed class IlVerificationTests
{
    private static ImmutableArray<byte> Compile(string source, bool debug)
    {
        EmitResult result = Compilation.Create(SyntaxTree.Parse(SourceText.From(source))).Emit("verify", debug);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result.PeImage;
    }

    public static TheoryData<string, bool> Examples
    {
        get
        {
            TheoryData<string, bool> cases = [];
            foreach (string name in typeof(IlVerificationTests).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("Examples.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                cases.Add(name, false);
                cases.Add(name, true);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_AllMethodsPassVerification(string resourceName, bool debug)
    {
        using Stream stream = typeof(IlVerificationTests).Assembly.GetManifestResourceStream(resourceName)!;
        using StreamReader reader = new(stream);
        IlVerification.AssertValid(Compile(reader.ReadToEnd(), debug));
    }

    public static TheoryData<string, bool> NumericModes => new()
    {
        { "i32", false }, { "i32", true }, { "i64", false }, { "i64", true },
        { "f32", false }, { "f32", true }, { "f64", false }, { "f64", true },
    };

    [Theory]
    [MemberData(nameof(NumericModes))]
    public void NumericOperatorsConversionsAndArrays_PassVerification(string type, bool debug)
    {
        // Each type selects different arithmetic, comparison, conversion,
        // ToString/TryParse/Parse, array-load/store, and metadata signatures.
        string remainder = type is "i32" or "i64" ? "n %= b;" : "";
        string source = $$"""
            fn operations(a: {{type}}, b: {{type}}) -> {{type}} {
                let n = -a;
                n += b; n -= b; n *= b; n /= b; {{remainder}}
                print(a < b); print(a <= b); print(a > b); print(a >= b);
                print(a == b); print(a != b);
                let values = [a, b];
                values[0] += b;
                let copies = [values[0]; 3];
                for value in copies { print(value); }
                return n;
            }
            fn main(a: {{type}}, b: {{type}}) {
                print(operations(a, b));
                print(i32(a)); print(i64(a)); print(f32(a)); print(f64(a));
                print(string(a)); print({{type}}("1"));
            }
            """;
        IlVerification.AssertValid(Compile(source, debug));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BooleanStringAndNestedArrays_PassVerification(bool debug)
    {
        const string source = """
            fn logic(a: bool, b: bool, c: bool) -> bool {
                return (a && b) || !c;
            }
            fn text(a: string, b: string) -> string {
                if a == b { return a; }
                if a != b { return f"${a}${bool(b)}${f32(a)}${f64(a)}"; }
                return a + b;
            }
            fn main(args: []string) {
                print(logic(true, false, bool(args[0])));
                print(text(args[0], args[1]));
                let flags = [true, false];
                flags[0] = !flags[1];
                for flag in flags { print(flag); }
                let words = ["word"; 2];
                words[1] = args[0];
                for word in words { print(word); }
                let grid = [[1], [2]];
                let copies = [grid; 2];
                copies[0][1][0] += 1;
                print(len(copies[0]));
                let empty: [][]string = [];
                print(len(empty));
            }
            """;
        IlVerification.AssertValid(Compile(source, debug));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatWithPendingOperands_PassVerification(bool debug)
    {
        // The fill loop must preserve the stack values already pushed by
        // the enclosing store, array literal, call, or binary expression.
        const string source = """
            fn select(n: i64, values: []i64) -> i64 { return values[n]; }
            fn main(n: i64) {
                let grid = [[1; n], [2; 0]];
                grid[0] = [3; n];
                print(select(0, [4; n]));
                print(5 + len([6; n]));
                print(len([7; 8 + len([9; n])]));
            }
            """;
        IlVerification.AssertValid(Compile(source, debug));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedPrimitiveEntryPointAndReturnValue_PassVerification(bool debug)
    {
        const string source = """
            fn main(a: i32, b: i64, c: f32, d: f64, e: bool, f: string) -> i32 {
                print(a); print(b); print(c); print(d); print(e); print(f);
                return a;
            }
            """;
        IlVerification.AssertValid(Compile(source, debug));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedLoopsAndAllReturningBranches_PassVerification(bool debug)
    {
        const string source = """
            fn control(n: i64) -> i64 {
                let sum = 0;
                while n > 0 {
                    n -= 1;
                    if n == 3 { continue; }
                    if n == 1 { break; }
                    for i in 0..n {
                        if i == 1 { continue; }
                        sum += i;
                    }
                    for i in 0..=n {
                        if i == 2 { break; }
                        sum += i;
                    }
                }
                if sum > 0 { return sum; } else { return -sum; }
            }
            fn main() { print(control(4)); }
            """;
        IlVerification.AssertValid(Compile(source, debug));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeStackAndLocalSlots_PassVerification(bool debug)
    {
        // Exercise maxStack > 8, long branch distances, and local indices
        // beyond the single-byte forms of ldloc/stloc, without running code.
        string parameters = string.Join(", ", Enumerable.Range(0, 12).Select(i => $"p{i}: i64"));
        string arguments = string.Join(", ", Enumerable.Range(0, 12).Select(i => $"v{i}"));
        string declarations = string.Join("\n", Enumerable.Range(0, 260).Select(i => $"let v{i} = n;"));
        string source = $$"""
            fn many({{parameters}}) -> i64 { return p11; }
            fn main(n: i64) {
                if n > 0 {
                    {{declarations}}
                    print(many({{arguments}}));
                    print(v259);
                }
            }
            """;
        IlVerification.AssertValid(Compile(source, debug));
    }

    [Fact]
    public void MissingAssembly_ReportsUnresolvedReferenceWithMethodName()
    {
        ImmutableArray<byte> valid = Compile("fn main() { print(1); }", debug: false);
        IlVerification.AssertValid(valid);
        using PEReader pe = new(valid);
        MetadataReader metadata = pe.GetMetadataReader();
        StringHandle name = metadata.AssemblyReferences
            .Select(h => metadata.GetAssemblyReference(h).Name)
            .Single(h => metadata.GetString(h) == "System.Console");
        byte[] corrupted = valid.ToArray();
        byte[] replacement = Encoding.UTF8.GetBytes("MissingConsole");
        Assert.Equal(Encoding.UTF8.GetByteCount(metadata.GetString(name)), replacement.Length);
        int nameOffset = pe.PEHeaders.MetadataStartOffset
            + metadata.GetHeapMetadataOffset(HeapIndex.String) + MetadataTokens.GetHeapOffset(name);
        replacement.CopyTo(corrupted, nameOffset);

        IReadOnlyList<VerificationFailure> failures = IlVerification.Verify([.. corrupted]);
        Assert.Contains(failures, f => f.Message.Contains("Program.main:", StringComparison.Ordinal)
            && f.Message.Contains("FileLoadErrorGeneric", StringComparison.Ordinal)
            && f.Message.Contains("MissingConsole", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateInterfaceImplementation_IsRejectedWithoutMethodBodies()
    {
        IlVerification.AssertValid(CreateInterfaceFixture(duplicate: false));
        VerificationFailure failure = Assert.Single(IlVerification.Verify(CreateInterfaceFixture(duplicate: true)));
        Assert.Equal(VerifierError.InterfaceImplHasDuplicate, failure.Code);
        Assert.StartsWith("Implementation: InterfaceImplHasDuplicate:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("IMarker", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", failure.Message, StringComparison.Ordinal);
    }

    private static ImmutableArray<byte> CreateInterfaceFixture(bool duplicate)
    {
        // No MethodDef rows: only the type-definition pass can reject this.
        // Arith has no interfaces, so construct a tiny metadata-only library.
        MetadataBuilder metadata = new();
        metadata.AddModule(0, metadata.GetOrAddString("interfaces.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("interfaces"), new Version(1, 0, 0, 0),
            default, default, 0, AssemblyHashAlgorithm.None);
        AssemblyName core = typeof(object).Assembly.GetName();
        AssemblyReferenceHandle coreRef = metadata.AddAssemblyReference(
            metadata.GetOrAddString(core.Name!), core.Version!, default,
            metadata.GetOrAddBlob(core.GetPublicKeyToken()!), 0, default);
        TypeReferenceHandle objectRef = metadata.AddTypeReference(coreRef,
            metadata.GetOrAddString("System"), metadata.GetOrAddString("Object"));
        FieldDefinitionHandle firstField = MetadataTokens.FieldDefinitionHandle(1);
        MethodDefinitionHandle firstMethod = MetadataTokens.MethodDefinitionHandle(1);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default,
            metadata.GetOrAddString("<Module>"), default, firstField, firstMethod);
        TypeDefinitionHandle marker = metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract, default,
            metadata.GetOrAddString("IMarker"), default, firstField, firstMethod);
        TypeDefinitionHandle implementation = metadata.AddTypeDefinition(TypeAttributes.Public, default,
            metadata.GetOrAddString("Implementation"), objectRef, firstField, firstMethod);
        metadata.AddInterfaceImplementation(implementation, marker);
        if (duplicate)
        {
            metadata.AddInterfaceImplementation(implementation, marker);
        }

        ManagedPEBuilder builder = new(PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata), new BlobBuilder());
        BlobBuilder image = new();
        builder.Serialize(image);
        return image.ToImmutableArray();
    }

    [Theory]
    [InlineData("underflow", "unused", VerifierError.StackUnderflow)]
    [InlineData("return-type", "unused", VerifierError.StackUnexpected)]
    [InlineData("maxstack", "unused", VerifierError.StackOverflow)]
    [InlineData("underflow", "<Main>", VerifierError.StackUnderflow)]
    public void CorruptedMethod_IsRejectedEvenWhenNotCalled(
        string corruption, string methodName, VerifierError expected)
    {
        // Start from a verified image, then change only the selected body.
        // unused is never called; <Main> is the compiler-generated bridge.
        ImmutableArray<byte> valid = Compile(
            "fn unused() -> i64 { let n = 1; return n; } fn main() { }", debug: false);
        IlVerification.AssertValid(valid);
        byte[] corrupted = valid.ToArray();
        using PEReader pe = new(valid);
        MetadataReader metadata = pe.GetMetadataReader();
        MethodDefinition method = metadata.GetMethodDefinition(metadata.MethodDefinitions.Single(
            h => metadata.GetString(metadata.GetMethodDefinition(h).Name) == methodName));
        int rva = method.RelativeVirtualAddress;
        SectionHeader section = pe.PEHeaders.SectionHeaders.Single(
            s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize);
        int bodyOffset = section.PointerToRawData + rva - section.VirtualAddress;

        if (corruption == "maxstack")
        {
            // A local signature forces a fat method header, whose second
            // ushort is maxStack. A declared zero cannot hold the literal.
            Assert.Equal(3, corrupted[bodyOffset] & 3);
            BinaryPrimitives.WriteUInt16LittleEndian(corrupted.AsSpan(bodyOffset + 2), 0);
        }
        else
        {
            int headerSize = (corrupted[bodyOffset] & 3) == 2 ? 1 : (corrupted[bodyOffset + 1] >> 4) * 4;
            int codeSize = pe.GetMethodBody(rva).GetILBytes()!.Length;
            Span<byte> il = corrupted.AsSpan(bodyOffset + headerSize, codeSize);
            il.Clear(); // nop is 0x00; preserve the body's size and following metadata.
            il[0] = (byte)(corruption == "underflow" ? ILOpCode.Pop : ILOpCode.Ldnull);
            il[^1] = (byte)ILOpCode.Ret;
        }

        IReadOnlyList<VerificationFailure> failures = IlVerification.Verify([.. corrupted]);
        Assert.Contains(failures, f => f.Code == expected);
        VerificationFailure failure = failures.First(f => f.Code == expected);
        Assert.Contains("Program." + methodName, failure.Message, StringComparison.Ordinal);
        Assert.Contains("IL_", failure.Message, StringComparison.Ordinal);
    }
}
