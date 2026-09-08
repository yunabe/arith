using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

using ILVerify;

namespace Arith.Compiler.Tests;

/// <summary>Verifies emitted bytes without loading or executing the generated assembly.</summary>
internal static class IlVerification
{
    internal static void AssertValid(ImmutableArray<byte> image)
    {
        IReadOnlyList<VerificationFailure> failures = Verify(image);
        Assert.True(failures.Count == 0,
            "Generated IL failed verification:\n" + string.Join("\n", failures.Select(f => f.Message)));
    }

    internal static IReadOnlyList<VerificationFailure> Verify(ImmutableArray<byte> image)
    {
        using PEReader pe = new(image);
        using RuntimeResolver resolver = new();
        Verifier verifier = new(resolver, new VerifierOptions { SanityChecks = true });
        verifier.SetSystemModuleName(new AssemblyNameInfo("System.Private.CoreLib"));

        // Verify every method, including unused functions and the synthesized
        // entry-point bridge. Also check the definitions of the emitted types.
        MetadataReader metadata = pe.GetMetadataReader();
        List<VerificationFailure> failures = [];
        foreach (VerificationResult result in verifier.Verify(pe))
        {
            failures.Add(Format(metadata, result));
        }

        foreach (TypeDefinitionHandle type in metadata.TypeDefinitions)
        {
            foreach (VerificationResult result in verifier.Verify(pe, type, verifyMethods: false))
            {
                failures.Add(Format(metadata, result, type));
            }
        }

        return failures;
    }

    private static VerificationFailure Format(
        MetadataReader metadata, VerificationResult result, TypeDefinitionHandle verifiedType = default)
    {
        string location = "<assembly>";
        TypeDefinitionHandle typeHandle = result.Type.IsNil ? verifiedType : result.Type;
        if (!result.Method.IsNil)
        {
            MethodDefinition method = metadata.GetMethodDefinition(result.Method);
            TypeDefinition type = metadata.GetTypeDefinition(method.GetDeclaringType());
            location = metadata.GetString(type.Name) + "." + metadata.GetString(method.Name);
        }
        else if (!typeHandle.IsNil)
        {
            location = metadata.GetString(metadata.GetTypeDefinition(typeHandle).Name);
        }

        ErrorArgument[] arguments = result.ErrorArguments ?? [];
        if (arguments.FirstOrDefault(a => a.Name == "Offset")?.Value is int offset)
        {
            location += $" IL_{offset:X4}";
        }

        string details = string.Join(", ", arguments.Select(a => $"{a.Name}={a.Value}"));
        string code = result.ExceptionID?.ToString() ?? result.Code.ToString();
        // Type-definition diagnostics use positional Args rather than the
        // method verifier's named ErrorArguments.
        string message = result.Args is { Length: > 0 }
            ? string.Format(CultureInfo.InvariantCulture, result.Message, result.Args)
            : result.Message;
        return new VerificationFailure(result.Code, $"{location}: {code}: {message} [{details}]");
    }

    // Resolve facades and their forwarded types from the framework actually
    // running this test. No SDK install path, global tool, or RID is hard-coded.
    // IResolver requires the same PEReader for repeated requests; the cache and
    // its streams are scoped to one verification, so parallel tests are isolated.
    private sealed class RuntimeResolver : IResolver, IDisposable
    {
        private readonly Dictionary<string, PEReader> _readers = new(StringComparer.OrdinalIgnoreCase);

        public PEReader? ResolveAssembly(AssemblyNameInfo assemblyName)
        {
            string name = assemblyName.Name;
            if (!_readers.TryGetValue(name, out PEReader? reader))
            {
                string path = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll");
                // Let ILVerify report an unresolved reference with its assembly
                // name instead of aborting verification with FileNotFoundException.
                if (!File.Exists(path))
                {
                    return null;
                }

                reader = new PEReader(File.OpenRead(path));
                _readers.Add(name, reader);
            }

            return reader;
        }

        public PEReader ResolveModule(AssemblyNameInfo referencingAssembly, string fileName) =>
            throw new NotSupportedException($"Unexpected netmodule '{fileName}' in '{referencingAssembly.Name}'.");

        public void Dispose()
        {
            foreach (PEReader reader in _readers.Values)
            {
                reader.Dispose();
            }
        }
    }
}

internal sealed record VerificationFailure(VerifierError Code, string Message);
