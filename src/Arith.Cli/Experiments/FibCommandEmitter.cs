using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Arith.Cli.Experiments;

/// <summary>
/// Emits a small stand-alone .NET console program (`fib`) by constructing ECMA-335
/// metadata and CIL directly with System.Reflection.Metadata — the same low-level
/// library the C# compiler uses to write assemblies. This is a dry run for the
/// Arith compiler's code-generation stage; no C# compiler is involved.
///
/// The emitted program is equivalent to this C#:
/// <code>
/// static long Fib(int n) => n &lt; 2 ? 1 : Fib(n - 1) + Fib(n - 2);
///
/// static int Main(string[] args)
/// {
///     if (args.Length != 1 || !int.TryParse(args[0], out int n))
///     {
///         Console.Error.WriteLine("usage: fib &lt;n&gt;");
///         return 1;
///     }
///     Console.Write("fib(");
///     Console.Write(n);
///     Console.Write(") = ");
///     Console.WriteLine(Fib(n));
///     return 0;
/// }
/// </code>
/// </summary>
internal static class FibCommandEmitter
{
    // The runtime assemblies are referenced by the same identities the C# compiler
    // records when targeting net10.0. At run time the host resolves them from the
    // shared framework named in fib.runtimeconfig.json.
    private static readonly byte[] MicrosoftPublicKeyToken =
        [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a];

    private static readonly Version FrameworkAssemblyVersion = new(10, 0, 0, 0);

    private const string RuntimeConfigJson = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "10.0.0"
            }
          }
        }
        """;

    /// <summary>
    /// Writes the fib program into <paramref name="outputDirectory"/> and returns
    /// the paths of the files it created.
    /// </summary>
    internal static IReadOnlyList<string> Emit(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        List<string> written = [];

        string assemblyPath = Path.Combine(outputDirectory, "fib.dll");
        File.WriteAllBytes(assemblyPath, BuildAssembly());
        written.Add(assemblyPath);

        // Tells the host (`dotnet fib.dll`) which shared framework to load.
        string runtimeConfigPath = Path.Combine(outputDirectory, "fib.runtimeconfig.json");
        File.WriteAllText(runtimeConfigPath, RuntimeConfigJson + Environment.NewLine);
        written.Add(runtimeConfigPath);

        // Convenience launchers so the program can be run as `./fib 10`. A real
        // `arith build` would instead clone the SDK's native apphost executable.
        string launcherPath = Path.Combine(outputDirectory, "fib");
        File.WriteAllText(launcherPath, "#!/bin/sh\nexec dotnet \"$(dirname \"$0\")/fib.dll\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                launcherPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        written.Add(launcherPath);

        string cmdLauncherPath = Path.Combine(outputDirectory, "fib.cmd");
        File.WriteAllText(cmdLauncherPath, "@echo off\r\ndotnet \"%~dp0fib.dll\" %*\r\n");
        written.Add(cmdLauncherPath);

        return written;
    }

    /// <summary>Builds the bytes of fib.dll: a PE file wrapping metadata tables and IL.</summary>
    private static byte[] BuildAssembly()
    {
        MetadataBuilder metadata = new();

        // ---- Module and assembly identity (Module / Assembly tables) ----
        // Deterministic compilers derive the MVID from a hash of the output;
        // a random GUID is enough for this demo.
        metadata.AddModule(
            generation: 0,
            moduleName: metadata.GetOrAddString("fib.dll"),
            mvid: metadata.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);

        metadata.AddAssembly(
            name: metadata.GetOrAddString("fib"),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: 0,
            hashAlgorithm: AssemblyHashAlgorithm.Sha1);

        // ---- References to the runtime (AssemblyRef / TypeRef / MemberRef tables) ----
        AssemblyReferenceHandle systemRuntime = AddFrameworkReference(metadata, "System.Runtime");
        AssemblyReferenceHandle systemConsole = AddFrameworkReference(metadata, "System.Console");

        TypeReferenceHandle objectType = AddTypeReference(metadata, systemRuntime, "System", "Object");
        TypeReferenceHandle int32Type = AddTypeReference(metadata, systemRuntime, "System", "Int32");
        TypeReferenceHandle textWriterType = AddTypeReference(metadata, systemRuntime, "System.IO", "TextWriter");
        TypeReferenceHandle consoleType = AddTypeReference(metadata, systemConsole, "System", "Console");

        // Member references pair a declaring type with a name and a signature blob.
        // Signatures are the compact binary encoding from ECMA-335 §II.23.2.
        MemberReferenceHandle consoleWriteString = metadata.AddMemberReference(
            consoleType,
            metadata.GetOrAddString("Write"),
            MethodSignature(metadata, isInstanceMethod: false,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().String()));

        MemberReferenceHandle consoleWriteInt32 = metadata.AddMemberReference(
            consoleType,
            metadata.GetOrAddString("Write"),
            MethodSignature(metadata, isInstanceMethod: false,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().Int32()));

        MemberReferenceHandle consoleWriteLineInt64 = metadata.AddMemberReference(
            consoleType,
            metadata.GetOrAddString("WriteLine"),
            MethodSignature(metadata, isInstanceMethod: false,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().Int64()));

        // `Console.Error` is a property; IL calls its getter method directly.
        MemberReferenceHandle consoleGetError = metadata.AddMemberReference(
            consoleType,
            metadata.GetOrAddString("get_Error"),
            MethodSignature(metadata, isInstanceMethod: false,
                returnType: r => r.Type().Type(textWriterType, isValueType: false),
                parameterCount: 0,
                parameters: _ => { }));

        MemberReferenceHandle textWriterWriteLineString = metadata.AddMemberReference(
            textWriterType,
            metadata.GetOrAddString("WriteLine"),
            MethodSignature(metadata, isInstanceMethod: true,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().String()));

        MemberReferenceHandle int32TryParse = metadata.AddMemberReference(
            int32Type,
            metadata.GetOrAddString("TryParse"),
            MethodSignature(metadata, isInstanceMethod: false,
                returnType: r => r.Type().Boolean(),
                parameterCount: 2,
                parameters: p =>
                {
                    p.AddParameter().Type().String();
                    p.AddParameter().Type(isByRef: true).Int32(); // `out int` is a managed pointer
                }));

        // ---- Method bodies (IL stream) ----
        // MethodDef rows are numbered in insertion order, so Fib will be row 1 and
        // Main row 2. Fib's own handle is needed up front for the recursive call.
        MethodDefinitionHandle fibHandle = MetadataTokens.MethodDefinitionHandle(1);

        // Main has one local variable (`int n`), described by a signature of its own.
        BlobBuilder localsBlob = new();
        new BlobEncoder(localsBlob).LocalVariableSignature(variableCount: 1).AddVariable().Type().Int32();
        StandaloneSignatureHandle mainLocals = metadata.AddStandaloneSignature(metadata.GetOrAddBlob(localsBlob));

        BlobBuilder ilStream = new();
        MethodBodyStreamEncoder bodyStream = new(ilStream);
        int fibBodyOffset = EmitFibBody(bodyStream, fibHandle);
        int mainBodyOffset = EmitMainBody(
            metadata, bodyStream, mainLocals, fibHandle,
            consoleWriteString, consoleWriteInt32, consoleWriteLineInt64,
            consoleGetError, textWriterWriteLineString, int32TryParse);

        // ---- Method and parameter definitions (MethodDef / Param tables) ----
        ParameterHandle fibParameters = metadata.AddParameter(
            ParameterAttributes.None, metadata.GetOrAddString("n"), sequenceNumber: 1);

        MethodDefinitionHandle fibMethod = metadata.AddMethodDefinition(
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("Fib"),
            MethodSignature(metadata, isInstanceMethod: false,
                returnType: r => r.Type().Int64(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().Int32()),
            fibBodyOffset,
            parameterList: fibParameters);

        ParameterHandle mainParameters = metadata.AddParameter(
            ParameterAttributes.None, metadata.GetOrAddString("args"), sequenceNumber: 1);

        MethodDefinitionHandle mainMethod = metadata.AddMethodDefinition(
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            metadata.GetOrAddString("Main"),
            MethodSignature(metadata, isInstanceMethod: false,
                returnType: r => r.Type().Int32(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().SZArray().String()),
            mainBodyOffset,
            parameterList: mainParameters);

        if (fibMethod != fibHandle)
        {
            throw new InvalidOperationException("Fib was not assigned the predicted MethodDef row.");
        }

        // ---- Type definitions (TypeDef table) ----
        // Every module starts with the special <Module> type; its member lists point
        // at the first rows of the following type, meaning it owns no members itself.
        metadata.AddTypeDefinition(
            attributes: default,
            @namespace: default,
            name: metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: fibMethod);

        // `abstract sealed` is how metadata spells a static class.
        metadata.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed |
                TypeAttributes.BeforeFieldInit,
            @namespace: default,
            name: metadata.GetOrAddString("Program"),
            baseType: objectType,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: fibMethod);

        // ---- Wrap everything in a PE file with Main as the entry point ----
        ManagedPEBuilder peBuilder = new(
            PEHeaderBuilder.CreateExecutableHeader(),
            new MetadataRootBuilder(metadata),
            ilStream,
            entryPoint: mainMethod,
            flags: CorFlags.ILOnly);

        BlobBuilder peBlob = new();
        peBuilder.Serialize(peBlob);
        return peBlob.ToArray();
    }

    /// <summary>static long Fib(int n) => n &lt; 2 ? 1 : Fib(n - 1) + Fib(n - 2);</summary>
    private static int EmitFibBody(MethodBodyStreamEncoder bodyStream, MethodDefinitionHandle fibHandle)
    {
        BlobBuilder code = new();
        ControlFlowBuilder controlFlow = new();
        InstructionEncoder il = new(code, controlFlow);
        LabelHandle recurse = il.DefineLabel();

        il.LoadArgument(0);                    // push n
        il.LoadConstantI4(2);                  // push 2
        il.Branch(ILOpCode.Bge, recurse);      // if (n >= 2) goto recurse

        il.LoadConstantI8(1);                  // push 1L
        il.OpCode(ILOpCode.Ret);               // return 1

        il.MarkLabel(recurse);
        il.LoadArgument(0);
        il.LoadConstantI4(1);
        il.OpCode(ILOpCode.Sub);               // n - 1
        il.Call(fibHandle);                    // Fib(n - 1)
        il.LoadArgument(0);
        il.LoadConstantI4(2);
        il.OpCode(ILOpCode.Sub);               // n - 2
        il.Call(fibHandle);                    // Fib(n - 2)
        il.OpCode(ILOpCode.Add);
        il.OpCode(ILOpCode.Ret);

        return bodyStream.AddMethodBody(il, maxStack: 2);
    }

    /// <summary>static int Main(string[] args) — parse, print, and call Fib.</summary>
    private static int EmitMainBody(
        MetadataBuilder metadata,
        MethodBodyStreamEncoder bodyStream,
        StandaloneSignatureHandle mainLocals,
        MethodDefinitionHandle fibHandle,
        MemberReferenceHandle consoleWriteString,
        MemberReferenceHandle consoleWriteInt32,
        MemberReferenceHandle consoleWriteLineInt64,
        MemberReferenceHandle consoleGetError,
        MemberReferenceHandle textWriterWriteLineString,
        MemberReferenceHandle int32TryParse)
    {
        BlobBuilder code = new();
        ControlFlowBuilder controlFlow = new();
        InstructionEncoder il = new(code, controlFlow);
        LabelHandle usage = il.DefineLabel();

        // if (args.Length != 1) goto usage;
        il.LoadArgument(0);
        il.OpCode(ILOpCode.Ldlen);
        il.OpCode(ILOpCode.Conv_i4);
        il.LoadConstantI4(1);
        il.Branch(ILOpCode.Bne_un, usage);

        // if (!int.TryParse(args[0], out n)) goto usage;
        il.LoadArgument(0);
        il.LoadConstantI4(0);
        il.OpCode(ILOpCode.Ldelem_ref);        // args[0]
        il.LoadLocalAddress(0);                // &n (the `out` argument)
        il.Call(int32TryParse);
        il.Branch(ILOpCode.Brfalse, usage);

        // Console.Write("fib("); Console.Write(n); Console.Write(") = ");
        il.LoadString(metadata.GetOrAddUserString("fib("));
        il.Call(consoleWriteString);
        il.LoadLocal(0);
        il.Call(consoleWriteInt32);
        il.LoadString(metadata.GetOrAddUserString(") = "));
        il.Call(consoleWriteString);

        // Console.WriteLine(Fib(n)); return 0;
        il.LoadLocal(0);
        il.Call(fibHandle);
        il.Call(consoleWriteLineInt64);
        il.LoadConstantI4(0);
        il.OpCode(ILOpCode.Ret);

        // usage: Console.Error.WriteLine("usage: fib <n>"); return 1;
        il.MarkLabel(usage);
        il.Call(consoleGetError);
        il.LoadString(metadata.GetOrAddUserString("usage: fib <n>"));
        il.OpCode(ILOpCode.Callvirt);
        il.Token(textWriterWriteLineString);
        il.LoadConstantI4(1);
        il.OpCode(ILOpCode.Ret);

        return bodyStream.AddMethodBody(il, maxStack: 2, localVariablesSignature: mainLocals);
    }

    private static AssemblyReferenceHandle AddFrameworkReference(MetadataBuilder metadata, string name) =>
        metadata.AddAssemblyReference(
            metadata.GetOrAddString(name),
            FrameworkAssemblyVersion,
            culture: default,
            publicKeyOrToken: metadata.GetOrAddBlob(MicrosoftPublicKeyToken),
            flags: 0,
            hashValue: default);

    private static TypeReferenceHandle AddTypeReference(
        MetadataBuilder metadata, AssemblyReferenceHandle assembly, string @namespace, string name) =>
        metadata.AddTypeReference(
            assembly,
            metadata.GetOrAddString(@namespace),
            metadata.GetOrAddString(name));

    private static BlobHandle MethodSignature(
        MetadataBuilder metadata,
        bool isInstanceMethod,
        Action<ReturnTypeEncoder> returnType,
        int parameterCount,
        Action<ParametersEncoder> parameters)
    {
        BlobBuilder blob = new();
        new BlobEncoder(blob)
            .MethodSignature(isInstanceMethod: isInstanceMethod)
            .Parameters(parameterCount, returnType, parameters);
        return metadata.GetOrAddBlob(blob);
    }
}
