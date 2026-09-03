using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

/// <summary>
/// Emitter tests that inspect the generated metadata and IL directly, for
/// properties an end-to-end run cannot distinguish — notably string equality:
/// both sides of a literal comparison are interned via the same #US token, so
/// reference equality (ceq) would produce the same observable output as the
/// required string.Equals call (spec §8.2).
/// </summary>
public sealed class EmitterTests
{
    private static (PEReader Pe, MetadataReader Metadata) EmitProgram(string source)
    {
        Compilation compilation = Compilation.Create(SyntaxTree.Parse(SourceText.From(source)));
        EmitResult result = compilation.Emit("test");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        PEReader pe = new(ImmutableArray.CreateRange(result.PeImage));
        return (pe, pe.GetMetadataReader());
    }

    private static byte[] MethodBodyIl(PEReader pe, MetadataReader metadata, string name)
    {
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) == name)
            {
                return pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
                    ?? throw new InvalidOperationException($"'{name}' has no IL body");
            }
        }

        throw new InvalidOperationException($"method '{name}' not found");
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle) =>
        Enumerable.Range(0, haystack.Length - needle.Length + 1)
            .Any(i => haystack.AsSpan(i, needle.Length).SequenceEqual(needle));

    [Fact]
    public void StringEquality_CallsStringEquals_NotReferenceEquality()
    {
        (PEReader pe, MetadataReader metadata) = EmitProgram(
            """
            fn main() {
                print("a" == "b");
            }
            """);
        using (pe)
        {
            // The metadata must reference the static System.String::Equals.
            MemberReferenceHandle equalsReference = default;
            foreach (MemberReferenceHandle handle in metadata.MemberReferences)
            {
                MemberReference member = metadata.GetMemberReference(handle);
                if (metadata.GetString(member.Name) != "Equals"
                    || member.Parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                TypeReference parent = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                if (metadata.GetString(parent.Namespace) == "System"
                    && metadata.GetString(parent.Name) == "String")
                {
                    equalsReference = handle;
                    break;
                }
            }

            Assert.False(equalsReference.IsNil, "no MemberRef to System.String::Equals in the metadata");

            // main's body must actually call it (0x28 = call, then the
            // little-endian token) and must not compare references: ceq
            // (0xFE 0x01) has no business in this method.
            byte[] il = MethodBodyIl(pe, metadata, "main");
            int token = MetadataTokens.GetToken(equalsReference);
            byte[] callInstruction =
            [
                0x28,
                (byte)token, (byte)(token >> 8), (byte)(token >> 16), (byte)(token >> 24),
            ];
            Assert.True(ContainsSequence(il, callInstruction), "main does not call String::Equals");
            Assert.False(ContainsSequence(il, [0xFE, 0x01]), "main compares strings with ceq");
        }
    }

    [Fact]
    public void TypedMain_GetsASynthesizedBridgeEntryPoint()
    {
        (PEReader pe, MetadataReader metadata) = EmitProgram(
            """
            fn main(n: i64) {
                print(n);
            }
            """);
        using (pe)
        {
            // The PE entry point must be the synthesized <Main>(string[])
            // bridge, not the user's main(int64).
            MethodDefinitionHandle bridge = default;
            foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
            {
                if (metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "<Main>")
                {
                    bridge = handle;
                }
            }

            Assert.False(bridge.IsNil, "no synthesized <Main> bridge in the metadata");
            Assert.Equal(
                MetadataTokens.GetToken(bridge),
                pe.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress);
        }
    }

    [Fact]
    public void ParameterlessMain_AlsoRunsBehindTheBridge()
    {
        // Spec §5.1 requires exactly one argument per parameter — zero
        // included — so even a parameterless main gets the bridge with its
        // argument-count check.
        (PEReader pe, MetadataReader metadata) = EmitProgram("fn main() { }");
        using (pe)
        {
            MethodDefinitionHandle bridge = default;
            foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
            {
                if (metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "<Main>")
                {
                    bridge = handle;
                }
            }

            Assert.False(bridge.IsNil, "no synthesized <Main> bridge in the metadata");
            Assert.Equal(
                MetadataTokens.GetToken(bridge),
                pe.PEHeaders.CorHeader!.EntryPointTokenOrRelativeVirtualAddress);
        }
    }

    [Fact]
    public void CheckedIntegerArithmetic_UsesOvfOpcodes()
    {
        (PEReader pe, MetadataReader metadata) = EmitProgram(
            """
            fn add(a: i64, b: i64) -> i64 {
                return a + b;
            }

            fn main() {
                print(add(1, 2));
            }
            """);
        using (pe)
        {
            byte[] il = MethodBodyIl(pe, metadata, "add");
            Assert.Contains((byte)0xD6, il); // add.ovf (spec §11: checked).
            Assert.DoesNotContain((byte)0x58, il); // plain add.
        }
    }
}
