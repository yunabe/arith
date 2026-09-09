using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

using Arith.Compiler.Diagnostics;
using Arith.Compiler.Emit;
using Arith.Compiler.Syntax;
using Arith.Compiler.Text;

namespace Arith.Compiler.Tests;

/// <summary>
/// The CLR refuses to run a method whose locals signature counts more than
/// 65,535 variables, yet the metadata encoder and ILVerify both accept one
/// (issue #33). The emitter must count every slot it allocates — `let`s and
/// its own temporaries alike — and report ARITH4001 instead of producing
/// such an assembly.
/// </summary>
public sealed class LocalSlotLimitTests
{
    private const int Limit = Emitter.MaxLocalsPerMethod;

    /// <summary>
    /// A function `name` with `letCount` locals (`v0 = 11`, the rest `0`)
    /// followed by `tail`, and a `main` that calls it.
    /// </summary>
    private static string Function(int letCount, string tail = "", string name = "huge", string parameters = "")
    {
        StringBuilder source = new();
        source.Append("fn ").Append(name).Append('(').Append(parameters).Append(") { let v0 = 11;");
        for (int i = 1; i < letCount; i++)
        {
            source.Append(" let v").Append(i).Append(" = 0;");
        }

        return source.Append(' ').Append(tail).Append(" }\n").ToString();
    }

    private static string Program(int letCount, string tail = "") =>
        Function(letCount, tail) + "fn main() { huge(); }\n";

    private static EmitResult Emit(string source) =>
        Compilation.Create(SyntaxTree.Parse(SourceText.From(source))).Emit("locals");

    /// <summary>The variable count recorded in the named method's locals signature.</summary>
    private static int LocalCount(ImmutableArray<byte> peImage, string name)
    {
        using PEReader pe = new(peImage);
        MetadataReader metadata = pe.GetMetadataReader();
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (metadata.GetString(method.Name) != name)
            {
                continue;
            }

            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
            if (body.LocalSignature.IsNil)
            {
                return 0;
            }

            BlobReader signature = metadata.GetBlobReader(
                metadata.GetStandaloneSignature(body.LocalSignature).Signature);
            Assert.Equal((byte)SignatureKind.LocalVariables, signature.ReadByte());
            return signature.ReadCompressedInteger();
        }

        throw new InvalidOperationException($"method '{name}' not found");
    }

    private static Diagnostic AssertTooManyLocals(EmitResult result, string source, string name, int needed)
    {
        Assert.False(result.Success);
        Assert.True(result.PeImage.IsEmpty, "a failed emit must not hand back an assembly");
        Assert.True(result.PdbImage.IsEmpty, "a failed emit must not hand back symbols");
        Diagnostic diagnostic = Assert.Single(result.Diagnostics, d => d.Code == ErrorCodes.TooManyLocals.Code
            && d.Message.Contains($"'{name}'", StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains($" {needed} ", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains($" {Limit}", diagnostic.Message, StringComparison.Ordinal);
        // Located at the function's identifier, as the binder locates its own
        // function-level diagnostics.
        Assert.Equal(new TextSpan(source.IndexOf("fn " + name, StringComparison.Ordinal) + 3, name.Length), diagnostic.Span);
        return diagnostic;
    }

    [Fact]
    public void LastSupportedCount_EmitsEverySlot()
    {
        EmitResult result = Emit(Program(Limit));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        IlVerification.AssertValid(result.PeImage);
        Assert.Equal(Limit, LocalCount(result.PeImage, "huge"));
    }

    [Fact]
    public void FirstUnsupportedCount_ReportsTooManyLocals()
    {
        string source = Program(Limit + 1);

        EmitResult result = Emit(source);

        Diagnostic diagnostic = AssertTooManyLocals(result, source, "huge", Limit + 1);
        Assert.Single(result.Diagnostics);
        Assert.Equal(
            $"function 'huge' needs {Limit + 1} local variable slots (including compiler temporaries), but a .NET method can have at most {Limit}",
            diagnostic.Message);
    }

    public static TheoryData<string, int> GeneratedTemporaries => new()
    {
        // A numeric print converts through one temporary per type.
        { "print(v0);", 1 },
        // Two prints of the same type share that temporary.
        { "print(v0); print(v0);", 1 },
        // Different types each get their own.
        { "print(v0); print(1.5);", 2 },
        // A range loop: the loop variable plus the end temporary.
        { "for i in 0..v0 { }", 2 },
        // An array loop: the variable plus array, length, and index temporaries.
        { "for x in [v0] { }", 4 },
        // A repeat array literal: value, count, array, and index temporaries.
        { "let a = [v0; 2];", 5 },
    };

    [Theory]
    [MemberData(nameof(GeneratedTemporaries))]
    public void GeneratedTemporaries_CountTowardTheLimit(string tail, int temporaries)
    {
        // Exactly at the limit once the temporaries are added: still emitted.
        EmitResult atLimit = Emit(Program(Limit - temporaries, tail));
        Assert.True(atLimit.Success, string.Join("\n", atLimit.Diagnostics));
        IlVerification.AssertValid(atLimit.PeImage);
        Assert.Equal(Limit, LocalCount(atLimit.PeImage, "huge"));

        // One more source local and a temporary crosses the boundary.
        string source = Program(Limit - temporaries + 1, tail);
        AssertTooManyLocals(Emit(source), source, "huge", Limit + 1);
    }

    [Fact]
    public void Parameters_DoNotConsumeLocalSlots()
    {
        string source = Function(Limit, "print(v0 + a + b + c);", parameters: "a: i64, b: i64, c: i64")
            + "fn main() { huge(1, 2, 3); }\n";
        // ... though the print temporary does, so drop one let to fit.
        source = source.Replace(" let v1 = 0;", "", StringComparison.Ordinal);

        EmitResult result = Emit(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        IlVerification.AssertValid(result.PeImage);
        Assert.Equal(Limit, LocalCount(result.PeImage, "huge"));
    }

    [Fact]
    public void FarPastTheLimit_ReportsTheFullCountWithoutThrowing()
    {
        // Slots past 65,535 cannot even be encoded as ldloc/stloc operands;
        // the walk must survive them and report how many the function needs.
        const int needed = Limit + 5_000;
        string source = Program(needed - 1, "print(v0);");

        EmitResult result = Emit(source);

        AssertTooManyLocals(result, source, "huge", needed);
    }

    [Fact]
    public void EveryOverflowingFunction_IsReported()
    {
        string source =
            Function(Limit + 1, name: "first")
            + Function(Limit, name: "fine")
            + Function(Limit + 2, name: "second")
            + "fn main() { first(); fine(); second(); }\n";

        EmitResult result = Emit(source);

        AssertTooManyLocals(result, source, "first", Limit + 1);
        AssertTooManyLocals(result, source, "second", Limit + 2);
        Assert.Equal(2, result.Diagnostics.Length);
    }

    [Fact]
    public void DebugMode_AppliesTheSameLimit()
    {
        string source = Program(Limit, "print(v0);");

        EmitResult result = Compilation.Create(SyntaxTree.Parse(SourceText.From(source))).Emit("locals", debug: true);

        AssertTooManyLocals(result, source, "huge", Limit + 1);
    }
}
