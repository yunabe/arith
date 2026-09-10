using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Arith.Compiler.Binding;
using Arith.Compiler.Diagnostics;
using Arith.Compiler.Text;

namespace Arith.Compiler.Emit;

/// <summary>
/// Turns a bound program into an in-memory .NET assembly (design §4.5) using
/// System.Reflection.Metadata, generalizing the techniques prototyped by the
/// FibCommandEmitter experiment (docs/il-emission-notes.md). The emitter may
/// only be handed an error-free program: every expression is concretely
/// typed and value-returning functions are known to return. Its own
/// diagnostics are limited to target limits (ARITH4xxx) that a well-typed
/// program can still exceed, such as <see cref="MaxLocalsPerMethod"/>.
///
/// Emission runs a layout pass first — MethodDef rows are assigned in
/// declaration order before any body is written — so calls, including
/// recursive and forward calls, can reference their target's handle.
/// </summary>
public sealed class Emitter
{
    // The runtime assemblies are referenced by the identities the C# compiler
    // records when targeting net10.0; the host resolves them from the shared
    // framework named in the runtimeconfig.
    private static readonly byte[] MicrosoftPublicKeyToken =
        [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a];

    private static readonly Version FrameworkAssemblyVersion = new(10, 0, 0, 0);

    /// <summary>
    /// The most local variable slots one method may declare. The CLR rejects
    /// a locals signature whose count does not fit in 16 bits when the method
    /// is first called (<c>ConvToJitSig</c> throws
    /// <c>InvalidProgramException</c>), and NativeAOT truncates it, so a
    /// method past this limit compiles yet can never run. ECMA-335
    /// §II.23.2.6 states the cap as 0xFFFE; the runtime accepts 0xFFFF.
    /// Parameters are counted separately and do not consume local slots.
    /// </summary>
    public const int MaxLocalsPerMethod = ushort.MaxValue;

    private readonly MetadataBuilder _metadata = new();
    private readonly BlobBuilder _ilStream = new();
    private readonly MethodBodyStreamEncoder _bodyStream;
    private readonly PortablePdbEmitter _debug;
    private readonly DiagnosticBag _diagnostics;
    private readonly bool _debugMode;
    private readonly Dictionary<FunctionSymbol, MethodDefinitionHandle> _methodHandles = [];
    private readonly Dictionary<ArithType, TypeReferenceHandle> _primitiveTypeRefs = [];
    private readonly Dictionary<ArithType, TypeSpecificationHandle> _typeSpecs = [];
    private readonly Dictionary<ArithType, MemberReferenceHandle> _invariantToString = [];
    private readonly Dictionary<ArithType, MemberReferenceHandle> _invariantTryParse = [];
    private readonly Dictionary<ArithType, MemberReferenceHandle> _invariantParse = [];
    private readonly Dictionary<ArithType, MemberReferenceHandle> _isFinite = [];
    private MemberReferenceHandle _booleanParse;
    private MemberReferenceHandle _formatExceptionCtor;
    private MemberReferenceHandle _consoleWriteLineString;
    private MemberReferenceHandle _cultureGetInvariant;
    private MemberReferenceHandle _stringEquals;
    private MemberReferenceHandle _stringConcat;
    private MemberReferenceHandle _booleanTryParse;
    private MemberReferenceHandle _consoleGetError;
    private MemberReferenceHandle _textWriterWriteLine;
    private TypeReferenceHandle _objectType;

    private Emitter(SourceText source, string assemblyName, DiagnosticBag diagnostics, bool debug)
    {
        _bodyStream = new MethodBodyStreamEncoder(_ilStream);
        _debug = new PortablePdbEmitter(source, assemblyName);
        _diagnostics = diagnostics;
        _debugMode = debug;
    }

    /// <summary>
    /// Emits matching PE and Portable PDB images for an error-free bound
    /// program. A function that exceeds a target limit is reported to
    /// <paramref name="diagnostics"/> (ARITH4xxx) — every function is still
    /// checked so each offender gets its own diagnostic — and both images
    /// come back empty.
    /// </summary>
    public static (ImmutableArray<byte> PeImage, ImmutableArray<byte> PdbImage) Emit(
        BoundProgram program, string assemblyName, SourceText source, DiagnosticBag diagnostics, bool debug = false)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentException.ThrowIfNullOrEmpty(assemblyName);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new Emitter(source, assemblyName, diagnostics, debug).EmitProgram(program, assemblyName, debug);
    }

    private (ImmutableArray<byte> PeImage, ImmutableArray<byte> PdbImage) EmitProgram(
        BoundProgram program, string assemblyName, bool debug)
    {
        Debug.Assert(program.EntryPoint is not null, "an error-free program has an entry point");
        Debug.Assert(!program.Functions.IsEmpty, "an error-free program has at least main");

        _metadata.AddModule(
            generation: 0,
            moduleName: _metadata.GetOrAddString(assemblyName + ".dll"),
            mvid: _metadata.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);
        AssemblyDefinitionHandle assembly = _metadata.AddAssembly(
            name: _metadata.GetOrAddString(assemblyName),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: 0,
            hashAlgorithm: AssemblyHashAlgorithm.Sha1);

        AddRuntimeReferences(assembly, debug);

        // Layout pass (design §4.5): predict every function's MethodDef row
        // before writing bodies, in declaration order.
        for (int i = 0; i < program.Functions.Length; i++)
        {
            _methodHandles.Add(program.Functions[i].Symbol, MetadataTokens.MethodDefinitionHandle(i + 1));
        }

        // Bodies first (they need only handles and member refs), then the
        // MethodDef/Param rows that record each body's offset.
        int[] bodyOffsets = new int[program.Functions.Length];
        for (int i = 0; i < program.Functions.Length; i++)
        {
            bodyOffsets[i] = EmitFunctionBody(program.Functions[i]);
        }

        if (_diagnostics.HasErrors)
        {
            // Some function exceeded a target limit. Its body was reported
            // instead of written, so there is no assembly to finish.
            return ([], []);
        }

        int parameterRow = 1;
        for (int i = 0; i < program.Functions.Length; i++)
        {
            FunctionSymbol symbol = program.Functions[i].Symbol;
            ParameterHandle firstParameter = MetadataTokens.ParameterHandle(parameterRow);
            foreach (ParameterSymbol parameter in symbol.Parameters)
            {
                _metadata.AddParameter(
                    ParameterAttributes.None,
                    _metadata.GetOrAddString(parameter.Name),
                    sequenceNumber: parameter.Index + 1);
                parameterRow++;
            }

            MethodDefinitionHandle handle = _metadata.AddMethodDefinition(
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodImplAttributes.IL,
                _metadata.GetOrAddString(symbol.Name),
                FunctionSignature(symbol),
                bodyOffsets[i],
                firstParameter);
            if (handle != _methodHandles[symbol])
            {
                throw new InvalidOperationException(
                    $"'{symbol.Name}' was not assigned the MethodDef row the layout pass predicted.");
            }
        }

        // Every main runs behind a synthesized bridge entry point that owns
        // the string[] and enforces spec §5.1 — including a parameterless
        // main, which must still reject any argument with the usage message.
        int bridgeOffset = EmitEntryPointBridgeBody(program.EntryPoint!, assemblyName);
        _debug.AddMethod(default, []);
        ParameterHandle bridgeParameter = MetadataTokens.ParameterHandle(parameterRow);
        _metadata.AddParameter(
            ParameterAttributes.None, _metadata.GetOrAddString("args"), sequenceNumber: 1);
        MethodDefinitionHandle entryPoint = _metadata.AddMethodDefinition(
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            MethodImplAttributes.IL,
            _metadata.GetOrAddString("<Main>"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Type().Int32(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().SZArray().String()),
            bridgeOffset,
            bridgeParameter);

        // <Module> must be TypeDef row 1; "Program" (an `abstract sealed`,
        // i.e. static, class) owns every method from row 1 onward.
        _metadata.AddTypeDefinition(
            attributes: default,
            @namespace: default,
            name: _metadata.GetOrAddString("<Module>"),
            baseType: default,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));
        _metadata.AddTypeDefinition(
            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed |
                TypeAttributes.BeforeFieldInit,
            @namespace: default,
            name: _metadata.GetOrAddString("Program"),
            baseType: _objectType,
            fieldList: MetadataTokens.FieldDefinitionHandle(1),
            methodList: MetadataTokens.MethodDefinitionHandle(1));

        DebugDirectoryBuilder debugDirectory = new();
        ImmutableArray<byte> pdbImage = _debug.Serialize(
            _metadata.GetRowCounts(), entryPoint, assemblyName + ".pdb", debugDirectory);
        ManagedPEBuilder peBuilder = new(
            PEHeaderBuilder.CreateExecutableHeader(),
            new MetadataRootBuilder(_metadata),
            _ilStream,
            debugDirectoryBuilder: debugDirectory,
            entryPoint: entryPoint,
            flags: CorFlags.ILOnly);
        BlobBuilder peBlob = new();
        peBuilder.Serialize(peBlob);
        return ([.. peBlob.ToArray()], pdbImage);
    }

    private void AddRuntimeReferences(AssemblyDefinitionHandle assembly, bool debug)
    {
        AssemblyReferenceHandle systemRuntime = AddFrameworkReference("System.Runtime");
        AssemblyReferenceHandle systemConsole = AddFrameworkReference("System.Console");

        if (debug)
        {
            // DebuggableAttribute(true, true) is Default | DisableOptimizations.
            // PDBs alone cannot prevent shared throw helpers or inlining from
            // losing the fault's IL location and its Arith call frames.
            TypeReferenceHandle attribute = AddTypeReference(systemRuntime, "System.Diagnostics", "DebuggableAttribute");
            MemberReferenceHandle constructor = _metadata.AddMemberReference(
                attribute, _metadata.GetOrAddString(".ctor"),
                MethodSignature(
                    isInstanceMethod: true, returnType: r => r.Void(), parameterCount: 2,
                    parameters: p =>
                    {
                        p.AddParameter().Type().Boolean();
                        p.AddParameter().Type().Boolean();
                    }));
            BlobBuilder value = new();
            value.WriteUInt16(1); // Custom-attribute prolog.
            value.WriteBoolean(true); // isJITTrackingEnabled.
            value.WriteBoolean(true); // isJITOptimizerDisabled.
            value.WriteUInt16(0); // No named arguments.
            _metadata.AddCustomAttribute(assembly, constructor, _metadata.GetOrAddBlob(value));
        }

        _objectType = AddTypeReference(systemRuntime, "System", "Object");
        TypeReferenceHandle console = AddTypeReference(systemConsole, "System", "Console");
        TypeReferenceHandle cultureInfo = AddTypeReference(systemRuntime, "System.Globalization", "CultureInfo");
        TypeReferenceHandle formatProvider = AddTypeReference(systemRuntime, "System", "IFormatProvider");
        TypeReferenceHandle stringType = AddTypeReference(systemRuntime, "System", "String");
        _primitiveTypeRefs[ArithType.String] = stringType;

        // String == / != are ordinal content equality (spec §8.2, design
        // §4.5): lower to the static string.Equals(string, string), never to
        // reference equality via ceq.
        _stringEquals = _metadata.AddMemberReference(
            stringType,
            _metadata.GetOrAddString("Equals"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Type().Boolean(),
                parameterCount: 2,
                parameters: p =>
                {
                    p.AddParameter().Type().String();
                    p.AddParameter().Type().String();
                }));

        _consoleWriteLineString = _metadata.AddMemberReference(
            console,
            _metadata.GetOrAddString("WriteLine"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().String()));

        // String `+` is concatenation (spec §8.1).
        _stringConcat = _metadata.AddMemberReference(
            stringType,
            _metadata.GetOrAddString("Concat"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Type().String(),
                parameterCount: 2,
                parameters: p =>
                {
                    p.AddParameter().Type().String();
                    p.AddParameter().Type().String();
                }));

        TypeReferenceHandle booleanType = AddTypeReference(systemRuntime, "System", "Boolean");
        _primitiveTypeRefs[ArithType.Bool] = booleanType;

        // Numeric print must be culture-invariant (spec §10.1, design §4.5):
        // the typed Console.WriteLine overloads format through the current
        // culture, so numbers go through ToString(InvariantCulture) instead.
        _cultureGetInvariant = _metadata.AddMemberReference(
            cultureInfo,
            _metadata.GetOrAddString("get_InvariantCulture"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Type().Type(cultureInfo, isValueType: false),
                parameterCount: 0,
                parameters: _ => { }));

        TypeReferenceHandle numberStyles =
            AddTypeReference(systemRuntime, "System.Globalization", "NumberStyles");
        (ArithType Type, string Name)[] numericTypes =
        [
            (ArithType.I32, "Int32"),
            (ArithType.I64, "Int64"),
            (ArithType.F32, "Single"),
            (ArithType.F64, "Double"),
        ];
        foreach ((ArithType type, string name) in numericTypes)
        {
            TypeReferenceHandle typeReference = AddTypeReference(systemRuntime, "System", name);
            _primitiveTypeRefs[type] = typeReference;
            _invariantToString[type] = _metadata.AddMemberReference(
                typeReference,
                _metadata.GetOrAddString("ToString"),
                MethodSignature(
                    isInstanceMethod: true,
                    returnType: r => r.Type().String(),
                    parameterCount: 1,
                    parameters: p => p.AddParameter().Type().Type(formatProvider, isValueType: false)));

            // string(T) conversions (spec §7) parse with the invariant
            // culture and fail with the exception Parse throws.
            _invariantParse[type] = _metadata.AddMemberReference(
                typeReference,
                _metadata.GetOrAddString("Parse"),
                MethodSignature(
                    isInstanceMethod: false,
                    returnType: r => EncodeType(r.Type(), type),
                    parameterCount: 3,
                    parameters: p =>
                    {
                        p.AddParameter().Type().String();
                        p.AddParameter().Type().Type(numberStyles, isValueType: true);
                        p.AddParameter().Type().Type(formatProvider, isValueType: false);
                    }));

            // The entry-point bridge parses command-line arguments with the
            // invariant culture (spec §5.1): TryParse avoids exception
            // handling regions in the generated IL entirely.
            _invariantTryParse[type] = _metadata.AddMemberReference(
                typeReference,
                _metadata.GetOrAddString("TryParse"),
                MethodSignature(
                    isInstanceMethod: false,
                    returnType: r => r.Type().Boolean(),
                    parameterCount: 4,
                    parameters: p =>
                    {
                        p.AddParameter().Type().String();
                        p.AddParameter().Type().Type(numberStyles, isValueType: true);
                        p.AddParameter().Type().Type(formatProvider, isValueType: false);
                        EncodeType(p.AddParameter().Type(isByRef: true), type);
                    }));

            // Float TryParse happily returns NaN or infinity (an overflowing
            // exponent, or the literal spellings); spec §5.1 admits only
            // finite decimal values, so the bridge re-checks with IsFinite.
            if (type.IsFloat)
            {
                _isFinite[type] = _metadata.AddMemberReference(
                    typeReference,
                    _metadata.GetOrAddString("IsFinite"),
                    MethodSignature(
                        isInstanceMethod: false,
                        returnType: r => r.Type().Boolean(),
                        parameterCount: 1,
                        parameters: p => EncodeType(p.AddParameter().Type(), type)));
            }
        }

        _booleanTryParse = _metadata.AddMemberReference(
            booleanType,
            _metadata.GetOrAddString("TryParse"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Type().Boolean(),
                parameterCount: 2,
                parameters: p =>
                {
                    p.AddParameter().Type().String();
                    p.AddParameter().Type(isByRef: true).Boolean();
                }));
        _booleanParse = _metadata.AddMemberReference(
            booleanType,
            _metadata.GetOrAddString("Parse"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Type().Boolean(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().String()));

        // Thrown by string-to-float conversions whose parse result is not
        // finite (spec §7): .NET's Parse happily returns infinity for an
        // overflowing exponent and accepts the Infinity/NaN spellings.
        TypeReferenceHandle formatException = AddTypeReference(systemRuntime, "System", "FormatException");
        _formatExceptionCtor = _metadata.AddMemberReference(
            formatException,
            _metadata.GetOrAddString(".ctor"),
            MethodSignature(
                isInstanceMethod: true,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().String()));

        TypeReferenceHandle textWriter = AddTypeReference(systemRuntime, "System.IO", "TextWriter");
        _consoleGetError = _metadata.AddMemberReference(
            console,
            _metadata.GetOrAddString("get_Error"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Type().Type(textWriter, isValueType: false),
                parameterCount: 0,
                parameters: _ => { }));
        _textWriterWriteLine = _metadata.AddMemberReference(
            textWriter,
            _metadata.GetOrAddString("WriteLine"),
            MethodSignature(
                isInstanceMethod: true,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().String()));
    }

    // NumberStyles values used by the bridge (System.Globalization).
    private const int NumberStylesInteger = 7;  // AllowLeading/TrailingWhite | AllowLeadingSign.
    private const int NumberStylesFloat = 167;  // Integer | AllowDecimalPoint | AllowExponent.

    /// <summary>
    /// The synthesized entry point for a `main` with parameters (spec §5.1):
    /// `static int32 &lt;Main&gt;(string[] args)` checks the argument count,
    /// parses each argument invariantly (TryParse, so no exception-handling
    /// regions), and calls the user's main — or prints a usage line to
    /// stderr and returns 2. This is hand-shaped IL rather than a bound
    /// tree because it needs string[], byref locals, and BCL calls that
    /// Arith's own type system cannot express; if runtime support like this
    /// ever grows loops or shared helpers, the design's answer is a real
    /// C# runtime library, not more hand-emitted IL.
    /// </summary>
    private int EmitEntryPointBridgeBody(FunctionSymbol main, string assemblyName)
    {
        BlobBuilder code = new();
        ControlFlowBuilder controlFlow = new();
        InstructionEncoder il = new(code, controlFlow);
        ImmutableArray<ParameterSymbol> parameters = main.Parameters;

        // `main(args: []string)` receives the runtime's string[] verbatim
        // (spec §5.1): no count check, no parsing, no usage path — the
        // bridge only normalizes the exit code.
        if (parameters is [{ Type: var soleType }] && soleType == ArithType.String.ArrayOf())
        {
            il.LoadArgument(0);
            il.Call(_methodHandles[main]);
            if (main.ReturnType == ArithType.Void)
            {
                il.LoadConstantI4(0);
            }

            il.OpCode(ILOpCode.Ret);
            return _bodyStream.AddMethodBody(il, maxStack: 1, localVariablesSignature: default);
        }

        LabelHandle usage = il.DefineLabel();

        // if (args.Length != parameters.Length) goto usage;
        il.LoadArgument(0);
        il.OpCode(ILOpCode.Ldlen);
        il.OpCode(ILOpCode.Conv_i4);
        il.LoadConstantI4(parameters.Length);
        il.Branch(ILOpCode.Bne_un, usage);

        // Parse args[i] into local i, bailing to usage on the first failure.
        foreach (ParameterSymbol parameter in parameters)
        {
            il.LoadArgument(0);
            il.LoadConstantI4(parameter.Index);
            il.OpCode(ILOpCode.Ldelem_ref);
            if (parameter.Type == ArithType.String)
            {
                il.StoreLocal(parameter.Index);
                continue;
            }

            if (parameter.Type == ArithType.Bool)
            {
                il.LoadLocalAddress(parameter.Index);
                il.Call(_booleanTryParse);
            }
            else
            {
                il.LoadConstantI4(parameter.Type.IsInteger ? NumberStylesInteger : NumberStylesFloat);
                il.Call(_cultureGetInvariant);
                il.LoadLocalAddress(parameter.Index);
                il.Call(_invariantTryParse[parameter.Type]);
            }

            il.Branch(ILOpCode.Brfalse, usage);

            // TryParse accepts NaN/Infinity spellings and overflows to
            // infinity; spec §5.1 admits only finite values.
            if (parameter.Type.IsFloat)
            {
                il.LoadLocal(parameter.Index);
                il.Call(_isFinite[parameter.Type]);
                il.Branch(ILOpCode.Brfalse, usage);
            }
        }

        // Call the user's main; a void main means exit code 0 (spec §5.1).
        foreach (ParameterSymbol parameter in parameters)
        {
            il.LoadLocal(parameter.Index);
        }

        il.Call(_methodHandles[main]);
        if (main.ReturnType == ArithType.Void)
        {
            il.LoadConstantI4(0);
        }

        il.OpCode(ILOpCode.Ret);

        // usage: Console.Error.WriteLine("usage: ..."); return 2;
        string usageLine = string.Join(
            " ", ["usage:", assemblyName, .. parameters.Select(p => $"<{p.Name}: {p.Type}>")]);
        il.MarkLabel(usage);
        il.Call(_consoleGetError);
        il.LoadString(_metadata.GetOrAddUserString(usageLine));
        il.OpCode(ILOpCode.Callvirt);
        il.Token(_textWriterWriteLine);
        il.LoadConstantI4(2);
        il.OpCode(ILOpCode.Ret);

        // One local per parameter, in parameter order.
        StandaloneSignatureHandle localSignature = default;
        if (!parameters.IsEmpty)
        {
            BlobBuilder localsBlob = new();
            LocalVariablesEncoder locals =
                new BlobEncoder(localsBlob).LocalVariableSignature(parameters.Length);
            foreach (ParameterSymbol parameter in parameters)
            {
                EncodeType(locals.AddVariable().Type(), parameter.Type);
            }

            localSignature = _metadata.AddStandaloneSignature(_metadata.GetOrAddBlob(localsBlob));
        }

        // Peak depths per fixed shape: the numeric TryParse call site is 4
        // (string, styles, culture, address), the count check is 2, and the
        // final call holds one value per parameter.
        int maxStack = Math.Max(4, parameters.Length);
        return _bodyStream.AddMethodBody(il, maxStack, localVariablesSignature: localSignature);
    }

    private int EmitFunctionBody(BoundFunction function)
    {
        BlobBuilder code = new();
        ControlFlowBuilder controlFlow = new();
        InstructionEncoder il = new(code, controlFlow);
        FunctionBodyEmitter body = new(this, il);
        body.Emit(function);

        // Every slot the walk asked for is counted, `let`s and generated
        // temporaries alike; the runtime's limit is checked here rather than
        // trusted, because the encoder and ILVerify both accept a signature
        // the CLR then refuses to run (issue #33).
        if (body.LocalTypes.Count > MaxLocalsPerMethod)
        {
            _diagnostics.Report(
                ErrorCodes.TooManyLocals, function.NameSpan ?? function.Span ?? default,
                function.Symbol.Name, body.LocalTypes.Count, MaxLocalsPerMethod);
            return 0;
        }

        StandaloneSignatureHandle localSignature = default;
        if (body.LocalTypes.Count > 0)
        {
            BlobBuilder localsBlob = new();
            LocalVariablesEncoder locals =
                new BlobEncoder(localsBlob).LocalVariableSignature(body.LocalTypes.Count);
            foreach (ArithType type in body.LocalTypes)
            {
                EncodeType(locals.AddVariable().Type(), type);
            }

            localSignature = _metadata.AddStandaloneSignature(_metadata.GetOrAddBlob(localsBlob));
        }

        // maxStack is the tracked true depth, never the tiny-header default
        // (docs/il-emission-notes.md §4).
        _debug.AddMethod(localSignature, body.SequencePoints);
        return _bodyStream.AddMethodBody(il, body.MaxStack, localVariablesSignature: localSignature);
    }

    private BlobHandle FunctionSignature(FunctionSymbol symbol)
    {
        return MethodSignature(
            isInstanceMethod: false,
            returnType: r =>
            {
                if (symbol.ReturnType == ArithType.Void)
                {
                    r.Void();
                }
                else
                {
                    EncodeType(r.Type(), symbol.ReturnType);
                }
            },
            parameterCount: symbol.Parameters.Length,
            parameters: p =>
            {
                foreach (ParameterSymbol parameter in symbol.Parameters)
                {
                    EncodeType(p.AddParameter().Type(), parameter.Type);
                }
            });
    }

    /// <summary>
    /// The metadata token naming <paramref name="type"/>, as `newarr` needs
    /// for its element type: a TypeRef for a primitive, and — since only
    /// TypeSpec rows can name constructed types — a cached TypeSpec holding
    /// the SZArray signature for an array (design §7).
    /// </summary>
    private EntityHandle GetTypeHandle(ArithType type)
    {
        if (!type.IsArray)
        {
            return _primitiveTypeRefs[type];
        }

        if (!_typeSpecs.TryGetValue(type, out TypeSpecificationHandle handle))
        {
            BlobBuilder blob = new();
            EncodeType(new BlobEncoder(blob).TypeSpecificationSignature(), type);
            handle = _metadata.AddTypeSpecification(_metadata.GetOrAddBlob(blob));
            _typeSpecs.Add(type, handle);
        }

        return handle;
    }

    private static void EncodeType(SignatureTypeEncoder encoder, ArithType type)
    {
        if (type.IsArray)
        {
            // Arrays are single-dimension, zero-based .NET arrays; the
            // element encodes recursively, so `[][]i64` nests two SZArrays.
            EncodeType(encoder.SZArray(), type.ElementType!);
        }
        else if (type == ArithType.Bool)
        {
            encoder.Boolean();
        }
        else if (type == ArithType.I32)
        {
            encoder.Int32();
        }
        else if (type == ArithType.I64)
        {
            encoder.Int64();
        }
        else if (type == ArithType.F32)
        {
            encoder.Single();
        }
        else if (type == ArithType.F64)
        {
            encoder.Double();
        }
        else if (type == ArithType.String)
        {
            encoder.String();
        }
        else
        {
            throw new UnreachableException($"type '{type}' cannot appear in an emitted signature");
        }
    }

    private AssemblyReferenceHandle AddFrameworkReference(string name) =>
        _metadata.AddAssemblyReference(
            _metadata.GetOrAddString(name),
            FrameworkAssemblyVersion,
            culture: default,
            publicKeyOrToken: _metadata.GetOrAddBlob(MicrosoftPublicKeyToken),
            flags: 0,
            hashValue: default);

    private TypeReferenceHandle AddTypeReference(
        AssemblyReferenceHandle assembly, string @namespace, string name) =>
        _metadata.AddTypeReference(
            assembly,
            _metadata.GetOrAddString(@namespace),
            _metadata.GetOrAddString(name));

    private BlobHandle MethodSignature(
        bool isInstanceMethod,
        Action<ReturnTypeEncoder> returnType,
        int parameterCount,
        Action<ParametersEncoder> parameters)
    {
        BlobBuilder blob = new();
        new BlobEncoder(blob)
            .MethodSignature(isInstanceMethod: isInstanceMethod)
            .Parameters(parameterCount, returnType, parameters);
        return _metadata.GetOrAddBlob(blob);
    }

    /// <summary>
    /// Emits one function body, tracking local slots and the true evaluation
    /// stack depth. Statements after a return in the same block are
    /// unreachable and are skipped rather than emitted, so together with the
    /// binder's definite-return analysis a body never falls off its end.
    /// </summary>
    private sealed class FunctionBodyEmitter(Emitter emitter, InstructionEncoder il)
    {
        private readonly Emitter _emitter = emitter;
        private readonly InstructionEncoder _il = il;
        private readonly Dictionary<LocalSymbol, int> _localSlots = [];
        private readonly Dictionary<ArithType, int> _printTemps = [];
        private readonly List<(LabelHandle ContinueTarget, LabelHandle BreakTarget)> _loops = [];
        private int _depth;
        private TextSpan? _sourceSpan;

        /// <summary>
        /// The type of each local slot the body asked for, in slot order
        /// (lets first-come, then loop and print temps). The count is not
        /// capped: past <see cref="MaxLocalsPerMethod"/> it is what the
        /// diagnostic reports, and the body itself is discarded.
        /// </summary>
        public List<ArithType> LocalTypes { get; } = [];

        public int MaxStack { get; private set; }

        public List<SourceSequencePoint> SequencePoints { get; } = [];

        private void MarkSequencePoint(TextSpan? span, bool emitBoundary = false)
        {
            // Pending points own the next instruction. In optimized output,
            // nested nodes at the same offset keep only the innermost range.
            if (SequencePoints.Count > 0 && SequencePoints[^1].Offset == _il.Offset)
            {
                SequencePoints.RemoveAt(SequencePoints.Count - 1);
            }

            if (SequencePoints.Count == 0 || SequencePoints[^1].Span != span)
            {
                SequencePoints.Add(new SourceSequencePoint(_il.Offset, span));
                if (emitBoundary && span is not null)
                {
                    // In debug mode, preserve an IL boundary even with values
                    // on the stack (e.g. between a repeat count and conv.ovf.u).
                    _il.OpCode(ILOpCode.Nop);
                }
            }
        }

        public void Emit(BoundFunction function)
        {
            bool returned = EmitBlock(function.Body);
            if (!returned)
            {
                Debug.Assert(
                    function.Symbol.ReturnType == ArithType.Void,
                    "the binder guarantees value-returning functions contain a return");
                MarkSequencePoint(null);
                _il.OpCode(ILOpCode.Ret);
            }

            // Restoring the enclosing source scope can leave a pending point
            // after an explicit return. It owns no instruction and is omitted.
            if (SequencePoints.Count > 0 && SequencePoints[^1].Offset == _il.Offset)
            {
                SequencePoints.RemoveAt(SequencePoints.Count - 1);
            }
        }

        /// <summary>Emits statements until the block ends or returns; true when it returned.</summary>
        private bool EmitBlock(BoundBlock block)
        {
            foreach (BoundStatement statement in block.Statements)
            {
                if (EmitStatement(statement))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Emits one statement; true when it definitely returned.</summary>
        private bool EmitStatement(BoundStatement statement)
        {
            if (statement is BoundBlock block)
            {
                return EmitBlock(block);
            }

            TextSpan? enclosing = _sourceSpan;
            _sourceSpan = statement.Span;
            MarkSequencePoint(_sourceSpan, emitBoundary: _emitter._debugMode);
            bool returned = EmitStatementCore(statement);
            _sourceSpan = enclosing;
            MarkSequencePoint(enclosing);
            return returned;
        }

        private bool EmitStatementCore(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundLetStatement let:
                {
                    EmitExpression(let.Initializer);
                    int slot = AllocateLocal(let.Local);
                    _il.StoreLocal(slot);
                    Pop();
                    break;
                }

                case BoundAssignmentStatement assignment:
                {
                    if (assignment.CompoundOperator is { } op)
                    {
                        EmitVariableLoad(assignment.Variable);
                        EmitExpression(assignment.Value);
                        EmitBinaryOperator(op, assignment.Variable.Type);
                    }
                    else
                    {
                        EmitExpression(assignment.Value);
                    }

                    EmitVariableStore(assignment.Variable);
                    break;
                }

                case BoundElementAssignmentStatement assignment:
                    EmitElementAssignment(assignment);
                    break;
                case BoundExpressionStatement expression:
                {
                    EmitExpression(expression.Expression);
                    if (expression.Expression.Type != ArithType.Void)
                    {
                        _il.OpCode(ILOpCode.Pop); // Discard the unused value.
                        Pop();
                    }

                    break;
                }

                case BoundPrintStatement print:
                    EmitPrint(print);
                    break;
                case BoundIfStatement conditional:
                    return EmitIfStatement(conditional);
                case BoundWhileStatement loop:
                    EmitWhileStatement(loop);
                    break;
                case BoundForStatement loop:
                    EmitForStatement(loop);
                    break;
                case BoundForEachStatement loop:
                    EmitForEachStatement(loop);
                    break;
                case BoundBreakStatement:
                    _il.Branch(ILOpCode.Br, _loops[^1].BreakTarget);
                    break;
                case BoundContinueStatement:
                    _il.Branch(ILOpCode.Br, _loops[^1].ContinueTarget);
                    break;
                case BoundReturnStatement ret:
                {
                    if (ret.Value is not null)
                    {
                        EmitExpression(ret.Value);
                        Pop();
                    }

                    _il.OpCode(ILOpCode.Ret);
                    Debug.Assert(_depth == 0, "the stack must be empty at return");
                    return true;
                }

                default:
                    throw new UnreachableException(
                        $"statement '{statement.GetType().Name}' cannot reach emission");
            }

            Debug.Assert(_depth == 0, "the stack must be empty between statements");
            return false;
        }

        /// <summary>`cond; brfalse else; then; br end; else; end` — collapsed when there is no else.</summary>
        private bool EmitIfStatement(BoundIfStatement conditional)
        {
            EmitExpression(conditional.Condition);
            LabelHandle end = _il.DefineLabel();
            if (conditional.Else is null)
            {
                _il.Branch(ILOpCode.Brfalse, end);
                Pop();
                EmitStatement(conditional.Then);
                _il.MarkLabel(end);
                return false; // Without an else, the false path always continues.
            }

            LabelHandle elseLabel = _il.DefineLabel();
            _il.Branch(ILOpCode.Brfalse, elseLabel);
            Pop();
            bool thenReturned = EmitStatement(conditional.Then);
            if (!thenReturned)
            {
                _il.Branch(ILOpCode.Br, end);
            }

            _il.MarkLabel(elseLabel);
            bool elseReturned = EmitStatement(conditional.Else);
            _il.MarkLabel(end);
            return thenReturned && elseReturned;
        }

        /// <summary>Test-at-top loop: `br TEST; BODY: body; TEST: cond; brtrue BODY`.</summary>
        private void EmitWhileStatement(BoundWhileStatement loop)
        {
            MarkSequencePoint(null);
            LabelHandle body = _il.DefineLabel();
            LabelHandle test = _il.DefineLabel();
            LabelHandle exit = _il.DefineLabel();
            _il.Branch(ILOpCode.Br, test);
            _il.MarkLabel(body);
            _loops.Add((ContinueTarget: test, BreakTarget: exit));
            EmitStatement(loop.Body);
            _loops.RemoveAt(_loops.Count - 1);
            _il.MarkLabel(test);
            EmitExpression(loop.Condition);
            _il.Branch(ILOpCode.Brtrue, body);
            Pop();
            _il.MarkLabel(exit);
        }

        /// <summary>
        /// The overflow-safe range lowerings of design §4.5. Both increments
        /// only run while `i &lt; end`, so they can never overflow and emit a
        /// plain add; the closed form checks the endpoint after the body and
        /// before the increment, and `continue` targets that check.
        /// </summary>
        private void EmitForStatement(BoundForStatement loop)
        {
            int variableSlot = AllocateLocal(loop.Variable);
            int endSlot = AllocateSlot(ArithType.I64);

            // Spec §9.3: endpoints evaluate once, left to right, before the loop.
            EmitExpression(loop.Start);
            _il.StoreLocal(variableSlot);
            Pop();
            EmitExpression(loop.End);
            _il.StoreLocal(endSlot);
            Pop();

            MarkSequencePoint(null);
            LabelHandle body = _il.DefineLabel();
            LabelHandle exit = _il.DefineLabel();
            if (!loop.IsInclusive)
            {
                // ..  :  br TEST; BODY: body; INC: i += 1; TEST: if i < end goto BODY
                LabelHandle test = _il.DefineLabel();
                LabelHandle increment = _il.DefineLabel();
                _il.Branch(ILOpCode.Br, test);
                _il.MarkLabel(body);
                _loops.Add((ContinueTarget: increment, BreakTarget: exit));
                EmitStatement(loop.Body);
                _loops.RemoveAt(_loops.Count - 1);
                _il.MarkLabel(increment);
                EmitVariableIncrement(variableSlot);
                _il.MarkLabel(test);
                _il.LoadLocal(variableSlot);
                Push();
                _il.LoadLocal(endSlot);
                Push();
                _il.Branch(ILOpCode.Blt, body);
                Pop(2);
            }
            else
            {
                // ..= :  if i > end goto EXIT; BODY: body;
                //        CHECK: if i == end goto EXIT; i += 1; br BODY
                LabelHandle check = _il.DefineLabel();
                _il.LoadLocal(variableSlot);
                Push();
                _il.LoadLocal(endSlot);
                Push();
                _il.Branch(ILOpCode.Bgt, exit);
                Pop(2);
                _il.MarkLabel(body);
                _loops.Add((ContinueTarget: check, BreakTarget: exit));
                EmitStatement(loop.Body);
                _loops.RemoveAt(_loops.Count - 1);
                _il.MarkLabel(check);
                MarkSequencePoint(null);
                _il.LoadLocal(variableSlot);
                Push();
                _il.LoadLocal(endSlot);
                Push();
                _il.Branch(ILOpCode.Beq, exit);
                Pop(2);
                EmitVariableIncrement(variableSlot);
                _il.Branch(ILOpCode.Br, body);
            }

            _il.MarkLabel(exit);
        }

        /// <summary>
        /// `for x in a` lowers to an index loop over temps holding the array
        /// and its length (spec §9.3, design §7): the element loads at the
        /// start of each iteration — so element writes are visible to later
        /// iterations — and `continue` targets the increment.
        /// </summary>
        private void EmitForEachStatement(BoundForEachStatement loop)
        {
            ArithType elementType = loop.Array.Type.ElementType!;
            int variableSlot = AllocateLocal(loop.Variable);
            int arraySlot = AllocateSlot(loop.Array.Type);
            int lengthSlot = AllocateSlot(ArithType.I64);
            int indexSlot = AllocateSlot(ArithType.I64);

            // Spec §9.3: the array expression evaluates once, before the loop.
            EmitExpression(loop.Array);
            MarkSequencePoint(null);
            _il.OpCode(ILOpCode.Dup);
            Push();
            _il.StoreLocal(arraySlot);
            Pop();
            _il.OpCode(ILOpCode.Ldlen);
            _il.OpCode(ILOpCode.Conv_u8);
            _il.StoreLocal(lengthSlot);
            Pop();
            _il.LoadConstantI8(0);
            Push();
            _il.StoreLocal(indexSlot);
            Pop();

            // br TEST; BODY: x = a[i]; body; INC: i += 1; TEST: if i < len goto BODY
            LabelHandle body = _il.DefineLabel();
            LabelHandle increment = _il.DefineLabel();
            LabelHandle test = _il.DefineLabel();
            LabelHandle exit = _il.DefineLabel();
            _il.Branch(ILOpCode.Br, test);
            _il.MarkLabel(body);
            _il.LoadLocal(arraySlot);
            Push();
            _il.LoadLocal(indexSlot);
            Push();
            _il.OpCode(ILOpCode.Conv_i); // 0 <= i < length always fits native int.
            EmitLoadElement(elementType);
            Pop();
            _il.StoreLocal(variableSlot);
            Pop();
            _loops.Add((ContinueTarget: increment, BreakTarget: exit));
            EmitStatement(loop.Body);
            _loops.RemoveAt(_loops.Count - 1);
            _il.MarkLabel(increment);
            EmitVariableIncrement(indexSlot);
            _il.MarkLabel(test);
            _il.LoadLocal(indexSlot);
            Push();
            _il.LoadLocal(lengthSlot);
            Push();
            _il.Branch(ILOpCode.Blt, body);
            Pop(2);
            _il.MarkLabel(exit);
        }

        /// <summary>`i = i + 1` with a plain add — callers guarantee `i &lt; end` here.</summary>
        private void EmitVariableIncrement(int slot)
        {
            MarkSequencePoint(null);
            _il.LoadLocal(slot);
            Push();
            _il.LoadConstantI8(1);
            Push();
            _il.OpCode(ILOpCode.Add);
            Pop();
            _il.StoreLocal(slot);
            Pop();
        }

        /// <summary>`print(x)` is "convert to string, WriteLine" for every type (spec §10.1).</summary>
        private void EmitPrint(BoundPrintStatement print)
        {
            EmitExpression(print.Argument);
            EmitConvertToString(print.Argument.Type);
            _il.Call(_emitter._consoleWriteLineString);
            Pop();
        }

        /// <summary>
        /// Replaces the value on the stack with its string form — the shared
        /// lowering behind `print` and `string(value)`. A bool becomes the
        /// language's own literal spelling, "true" or "false" (spec §7).
        /// Numerics call ToString(CultureInfo.InvariantCulture), an instance
        /// call on a value type, hence the temp local for the address.
        /// </summary>
        private void EmitConvertToString(ArithType type)
        {
            if (type == ArithType.String)
            {
                return;
            }

            if (type == ArithType.Bool)
            {
                LabelHandle trueLabel = _il.DefineLabel();
                LabelHandle end = _il.DefineLabel();
                _il.Branch(ILOpCode.Brtrue, trueLabel);
                Pop();
                _il.LoadString(_emitter._metadata.GetOrAddUserString("false"));
                Push();
                _il.Branch(ILOpCode.Br, end);
                SetDepth(_depth - 1);
                _il.MarkLabel(trueLabel);
                _il.LoadString(_emitter._metadata.GetOrAddUserString("true"));
                Push();
                _il.MarkLabel(end);
                return;
            }

            int temp = GetPrintTemp(type);
            _il.StoreLocal(temp);
            Pop();
            _il.LoadLocalAddress(temp);
            Push();
            _il.Call(_emitter._cultureGetInvariant);
            Push();
            _il.Call(_emitter._invariantToString[type]);
            Pop(2);
            Push();
        }

        private void EmitExpression(BoundExpression expression)
        {
            // Every operand restores its parent's source scope before the
            // parent emits its own instructions, including checked operations.
            TextSpan? enclosing = _sourceSpan;
            EnterSourceScope(expression.Span);
            EmitExpressionCore(expression);
            EnterSourceScope(enclosing);
        }

        private void EnterSourceScope(TextSpan? span)
        {
            _sourceSpan = span;
            MarkSequencePoint(span, emitBoundary: _emitter._debugMode);
        }

        private void EmitExpressionCore(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLiteralExpression literal:
                    EmitLiteral(literal);
                    break;
                case BoundVariableExpression variable:
                    EmitVariableLoad(variable.Variable);
                    break;
                case BoundUnaryExpression { OperatorKind: BoundUnaryOperatorKind.LogicalNegation } unary:
                    EmitExpression(unary.Operand);
                    EmitBooleanNegation();
                    break;
                case BoundUnaryExpression unary:
                {
                    Debug.Assert(unary.OperatorKind == BoundUnaryOperatorKind.Negation, "the other kind is handled above");
                    if (unary.Type.IsInteger)
                    {
                        // Spec §11: negation is checked; IL has no neg.ovf,
                        // so emit `0 - operand` with sub.ovf.
                        if (unary.Type == ArithType.I32)
                        {
                            _il.LoadConstantI4(0);
                        }
                        else
                        {
                            _il.LoadConstantI8(0);
                        }

                        Push();
                        EmitExpression(unary.Operand);
                        _il.OpCode(ILOpCode.Sub_ovf);
                        Pop();
                    }
                    else
                    {
                        EmitExpression(unary.Operand);
                        _il.OpCode(ILOpCode.Neg);
                    }

                    break;
                }

                case BoundBinaryExpression binary:
                    EmitBinaryExpression(binary);
                    break;
                case BoundConversionExpression conversion:
                    EmitExpression(conversion.Operand);
                    EmitConversion(conversion.Operand.Type, conversion.Type);
                    break;
                case BoundCallExpression call:
                {
                    foreach (BoundExpression argument in call.Arguments)
                    {
                        EmitExpression(argument);
                    }

                    _il.Call(_emitter._methodHandles[call.Function]);
                    Pop(call.Arguments.Length);
                    if (call.Function.ReturnType != ArithType.Void)
                    {
                        Push();
                    }

                    break;
                }

                case BoundArrayLiteralExpression array:
                    EmitArrayLiteral(array);
                    break;
                case BoundArrayRepeatExpression repeat:
                    EmitArrayRepeat(repeat);
                    break;
                case BoundIndexExpression index:
                    EmitExpression(index.Array);
                    EmitExpression(index.Index);
                    EmitIndexToNativeInt();
                    EmitLoadElement(index.Type);
                    Pop(); // Array and index in, element out.
                    break;
                case BoundLenExpression len:
                    // ldlen pushes a native unsigned int; a .NET array length
                    // always fits i64, so widen unsigned (design §7).
                    EmitExpression(len.Array);
                    _il.OpCode(ILOpCode.Ldlen);
                    _il.OpCode(ILOpCode.Conv_u8);
                    break;

                default:
                    throw new UnreachableException(
                        $"expression '{expression.GetType().Name}' cannot reach emission");
            }
        }

        /// <summary>`newarr` then one dup/index/value/stelem sequence per element (spec §4.5).</summary>
        private void EmitArrayLiteral(BoundArrayLiteralExpression array)
        {
            ArithType elementType = array.Type.ElementType!;
            _il.LoadConstantI4(array.Elements.Length);
            Push();
            _il.OpCode(ILOpCode.Newarr);
            _il.Token(_emitter.GetTypeHandle(elementType)); // Count in, array out.
            for (int i = 0; i < array.Elements.Length; i++)
            {
                _il.OpCode(ILOpCode.Dup);
                Push();
                _il.LoadConstantI4(i);
                Push();
                EmitExpression(array.Elements[i]);
                EmitStoreElement(elementType);
                Pop(3);
            }
        }

        /// <summary>
        /// `[value; count]` (spec §4.5): the one value and the count evaluate
        /// once, in that order, into temps; `conv.ovf.u` before `newarr`
        /// makes a negative count fault as OverflowException; a fill loop
        /// stores the shared value into every slot.
        /// </summary>
        private void EmitArrayRepeat(BoundArrayRepeatExpression repeat)
        {
            ArithType elementType = repeat.Type.ElementType!;

            // Fresh slots every time: an inner repeat may evaluate while an
            // outer one's temps are live, so slots cannot be shared by type.
            int valueSlot = AllocateSlot(elementType);
            int countSlot = AllocateSlot(ArithType.I64);
            int arraySlot = AllocateSlot(repeat.Type);
            int indexSlot = AllocateSlot(ArithType.I64);

            EmitExpression(repeat.Value);
            _il.StoreLocal(valueSlot);
            Pop();
            EmitExpression(repeat.Count);
            _il.OpCode(ILOpCode.Dup);
            Push();
            _il.StoreLocal(countSlot);
            Pop();
            _il.OpCode(ILOpCode.Conv_ovf_u);
            _il.OpCode(ILOpCode.Newarr);
            _il.Token(_emitter.GetTypeHandle(elementType));
            _il.StoreLocal(arraySlot);
            Pop();

            // for (i = 0; i < count; i += 1) array[i] = value;
            MarkSequencePoint(null);
            _il.LoadConstantI8(0);
            Push();
            _il.StoreLocal(indexSlot);
            Pop();
            // Test before the body so its incoming stack is known on a
            // forward scan (ECMA-335 III.1.7.5, enforced by ILVerify).
            // Enclosing expressions may have operands on the stack; jumping
            // over the body first would give it an assumed empty stack.
            // Statement-level while/for/foreach loops always enter with an
            // empty stack, so their loop layouts need no such change.
            LabelHandle test = _il.DefineLabel();
            LabelHandle exit = _il.DefineLabel();
            _il.MarkLabel(test);
            _il.LoadLocal(indexSlot);
            Push();
            _il.LoadLocal(countSlot);
            Push();
            _il.Branch(ILOpCode.Bge, exit);
            Pop(2);

            _il.LoadLocal(arraySlot);
            Push();
            _il.LoadLocal(indexSlot);
            Push();
            _il.OpCode(ILOpCode.Conv_i); // 0 <= i < count always fits native int.
            _il.LoadLocal(valueSlot);
            Push();
            EmitStoreElement(elementType);
            Pop(3);
            EmitVariableIncrement(indexSlot);
            _il.Branch(ILOpCode.Br, test);
            _il.MarkLabel(exit);

            MarkSequencePoint(repeat.Span);
            _il.LoadLocal(arraySlot);
            Push();
        }

        /// <summary>
        /// `array[index] op= value` — array and index evaluate once, into
        /// temps, before the right-hand side (spec §8.4); a plain `=` needs
        /// no temps because each evaluates directly in stelem order.
        /// </summary>
        private void EmitElementAssignment(BoundElementAssignmentStatement assignment)
        {
            if (assignment.CompoundOperator is not { } op)
            {
                EmitExpression(assignment.Array);
                EmitExpression(assignment.Index);
                EmitIndexToNativeInt();
                EmitExpression(assignment.Value);
                EmitStoreElement(assignment.ElementType);
                Pop(3);
                return;
            }

            int arraySlot = AllocateSlot(assignment.Array.Type);
            int indexSlot = AllocateSlot(ArithType.I64);
            EmitExpression(assignment.Array);
            _il.StoreLocal(arraySlot);
            Pop();
            EmitExpression(assignment.Index);
            _il.StoreLocal(indexSlot);
            Pop();

            // array[i] = array[i] op value — the element read is the start
            // of the right-hand side, so it precedes the value (spec §8.4).
            _il.LoadLocal(arraySlot);
            Push();
            _il.LoadLocal(indexSlot);
            Push();
            EmitIndexToNativeInt();
            _il.LoadLocal(arraySlot);
            Push();
            _il.LoadLocal(indexSlot);
            Push();
            EmitIndexToNativeInt();
            EmitLoadElement(assignment.ElementType);
            Pop();
            EmitExpression(assignment.Value);
            EmitBinaryOperator(op, assignment.ElementType);
            EmitStoreElement(assignment.ElementType);
            Pop(3);
        }

        /// <summary>
        /// Narrows the i64 index on the stack to the native int the element
        /// opcodes take; `conv.ovf.i` keeps the checked semantics on 32-bit
        /// hosts, where an out-of-native-range index cannot silently wrap
        /// (design §7).
        /// </summary>
        private void EmitIndexToNativeInt() => _il.OpCode(ILOpCode.Conv_ovf_i);

        /// <summary>
        /// The typed ldelem for an element type. Bool loads zero-extended
        /// (`ldelem.u1`) so a stored 0/1 byte reads back as valid bool;
        /// strings and nested arrays are object references.
        /// </summary>
        private void EmitLoadElement(ArithType elementType)
        {
            ILOpCode opCode = elementType == ArithType.Bool ? ILOpCode.Ldelem_u1
                : elementType == ArithType.I32 ? ILOpCode.Ldelem_i4
                : elementType == ArithType.I64 ? ILOpCode.Ldelem_i8
                : elementType == ArithType.F32 ? ILOpCode.Ldelem_r4
                : elementType == ArithType.F64 ? ILOpCode.Ldelem_r8
                : ILOpCode.Ldelem_ref;
            _il.OpCode(opCode);
        }

        /// <summary>The typed stelem for an element type; bool stores as a byte (`stelem.i1`).</summary>
        private void EmitStoreElement(ArithType elementType)
        {
            ILOpCode opCode = elementType == ArithType.Bool ? ILOpCode.Stelem_i1
                : elementType == ArithType.I32 ? ILOpCode.Stelem_i4
                : elementType == ArithType.I64 ? ILOpCode.Stelem_i8
                : elementType == ArithType.F32 ? ILOpCode.Stelem_r4
                : elementType == ArithType.F64 ? ILOpCode.Stelem_r8
                : ILOpCode.Stelem_ref;
            _il.OpCode(opCode);
        }

        private void EmitLiteral(BoundLiteralExpression literal)
        {
            if (literal.Type == ArithType.I32)
            {
                _il.LoadConstantI4((int)literal.Value!);
            }
            else if (literal.Type == ArithType.I64)
            {
                _il.LoadConstantI8((long)literal.Value!);
            }
            else if (literal.Type == ArithType.F32)
            {
                _il.LoadConstantR4((float)literal.Value!);
            }
            else if (literal.Type == ArithType.F64)
            {
                _il.LoadConstantR8((double)literal.Value!);
            }
            else if (literal.Type == ArithType.Bool)
            {
                _il.LoadConstantI4((bool)literal.Value! ? 1 : 0);
            }
            else if (literal.Type == ArithType.String)
            {
                _il.LoadString(_emitter._metadata.GetOrAddUserString((string)literal.Value!));
            }
            else
            {
                throw new UnreachableException($"literal of type '{literal.Type}' cannot reach emission");
            }

            Push();
        }

        /// <summary>
        /// Converts the stack top from one Arith type to another (spec §7).
        /// Widening and float conversions are plain; narrowing integer and
        /// float-to-integer conversions use conv.ovf, which faults at
        /// runtime on out-of-range values, NaN, and infinity.
        /// </summary>
        private void EmitConversion(ArithType from, ArithType to)
        {
            if (from == to)
            {
                return; // Identity, including string(string).
            }

            if (to == ArithType.String)
            {
                EmitConvertToString(from);
                return;
            }

            if (from == ArithType.String)
            {
                EmitParseString(to);
                return;
            }

            ILOpCode opCode;
            if (to == ArithType.I32)
            {
                opCode = ILOpCode.Conv_ovf_i4; // From i64 or a float: checked.
            }
            else if (to == ArithType.I64)
            {
                opCode = from.IsFloat ? ILOpCode.Conv_ovf_i8 : ILOpCode.Conv_i8; // i32 → i64 always fits.
            }
            else if (to == ArithType.F32)
            {
                opCode = ILOpCode.Conv_r4; // Precision loss is allowed (spec §7).
            }
            else if (to == ArithType.F64)
            {
                opCode = ILOpCode.Conv_r8;
            }
            else
            {
                throw new UnreachableException($"no conversion from '{from}' to '{to}' should have bound");
            }

            _il.OpCode(opCode);
        }

        /// <summary>
        /// Converts the string on the stack to a primitive (spec §7) with
        /// the invariant Parse; the exception it throws on bad input is the
        /// specified runtime error. A float result is additionally checked
        /// with IsFinite — .NET's Parse returns infinity for an overflowing
        /// exponent and accepts the Infinity/NaN spellings — and a
        /// FormatException is thrown explicitly (no exception-handling
        /// regions are needed for a throw).
        /// </summary>
        private void EmitParseString(ArithType to)
        {
            if (to == ArithType.Bool)
            {
                _il.Call(_emitter._booleanParse);
                return; // One value in, one out.
            }

            _il.LoadConstantI4(to.IsInteger ? NumberStylesInteger : NumberStylesFloat);
            Push();
            _il.Call(_emitter._cultureGetInvariant);
            Push();
            _il.Call(_emitter._invariantParse[to]);
            Pop(3);
            Push();
            if (to.IsFloat)
            {
                LabelHandle finite = _il.DefineLabel();
                _il.OpCode(ILOpCode.Dup);
                Push();
                _il.Call(_emitter._isFinite[to]);
                _il.Branch(ILOpCode.Brtrue, finite);
                Pop();
                _il.LoadString(_emitter._metadata.GetOrAddUserString(
                    $"The string does not represent a finite {to} value."));
                Push();
                _il.OpCode(ILOpCode.Newobj);
                _il.Token(_emitter._formatExceptionCtor);
                _il.OpCode(ILOpCode.Throw);
                Pop();
                _il.MarkLabel(finite);
            }
        }

        /// <summary>Spec §11: integer add/sub/mul are checked; div/rem fault at runtime on their own.</summary>
        private void EmitBinaryOperator(BoundBinaryOperatorKind kind, ArithType type)
        {
            if (type == ArithType.String)
            {
                Debug.Assert(kind == BoundBinaryOperatorKind.Addition, "only + binds on strings");
                _il.Call(_emitter._stringConcat);
                Pop(2);
                Push();
                return;
            }

            ILOpCode opCode = kind switch
            {
                BoundBinaryOperatorKind.Addition => type.IsInteger ? ILOpCode.Add_ovf : ILOpCode.Add,
                BoundBinaryOperatorKind.Subtraction => type.IsInteger ? ILOpCode.Sub_ovf : ILOpCode.Sub,
                BoundBinaryOperatorKind.Multiplication => type.IsInteger ? ILOpCode.Mul_ovf : ILOpCode.Mul,
                BoundBinaryOperatorKind.Division => ILOpCode.Div,
                BoundBinaryOperatorKind.Remainder => ILOpCode.Rem,
                _ => throw new UnreachableException($"unhandled binary operator {kind}"),
            };
            _il.OpCode(opCode);
            Pop();
        }

        /// <summary>
        /// Comparison and equality over two pushed operands. `&lt;=` and `&gt;=`
        /// negate the opposite strict comparison; on floats the negated form
        /// uses the unordered opcode so NaN compares false either way, per
        /// .NET IEEE 754 semantics (spec §8.2). String equality calls
        /// string.Equals; bool and numerics use ceq.
        /// </summary>
        private void EmitComparisonOperator(BoundBinaryOperatorKind kind, ArithType operandType)
        {
            switch (kind)
            {
                case BoundBinaryOperatorKind.Less:
                    _il.OpCode(ILOpCode.Clt);
                    Pop();
                    break;
                case BoundBinaryOperatorKind.Greater:
                    _il.OpCode(ILOpCode.Cgt);
                    Pop();
                    break;
                case BoundBinaryOperatorKind.LessOrEqual:
                    _il.OpCode(operandType.IsFloat ? ILOpCode.Cgt_un : ILOpCode.Cgt);
                    Pop();
                    EmitBooleanNegation();
                    break;
                case BoundBinaryOperatorKind.GreaterOrEqual:
                    _il.OpCode(operandType.IsFloat ? ILOpCode.Clt_un : ILOpCode.Clt);
                    Pop();
                    EmitBooleanNegation();
                    break;
                case BoundBinaryOperatorKind.Equals or BoundBinaryOperatorKind.NotEquals:
                {
                    if (operandType == ArithType.String)
                    {
                        _il.Call(_emitter._stringEquals);
                        Pop(2);
                        Push();
                    }
                    else
                    {
                        _il.OpCode(ILOpCode.Ceq);
                        Pop();
                    }

                    if (kind == BoundBinaryOperatorKind.NotEquals)
                    {
                        EmitBooleanNegation();
                    }

                    break;
                }

                default:
                    throw new UnreachableException($"unhandled comparison operator {kind}");
            }
        }

        /// <summary>Replaces the bool on top of the stack with its negation (`x == 0`).</summary>
        private void EmitBooleanNegation()
        {
            _il.LoadConstantI4(0);
            Push();
            _il.OpCode(ILOpCode.Ceq);
            Pop();
        }

        /// <summary>
        /// Emits a binary expression. A flat `a + b + … + z` is a left-deep
        /// chain, so recursing into Left would cost one frame set per
        /// operator and overflow the stack on long chains (issue #34). The
        /// left spine is collected iteratively instead, the leftmost operand
        /// is emitted, and each operator's tail follows from the innermost
        /// out. Source scopes are entered and left in the exact order the
        /// recursive walk would have used, so sequence points are unchanged.
        /// </summary>
        private void EmitBinaryExpression(BoundBinaryExpression binary)
        {
            List<BoundBinaryExpression> spine = [binary];
            while (spine[^1].Left is BoundBinaryExpression left)
            {
                EnterSourceScope(left.Span);
                spine.Add(left);
            }

            EmitExpression(spine[^1].Left);
            for (int i = spine.Count - 1; i >= 0; i--)
            {
                EmitBinaryTail(spine[i]);
                if (i > 0)
                {
                    EnterSourceScope(spine[i - 1].Span);
                }
            }
        }

        /// <summary>Emits everything after the left operand is on the stack.</summary>
        private void EmitBinaryTail(BoundBinaryExpression binary)
        {
            if (binary.OperatorKind is BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr)
            {
                EmitShortCircuitTail(binary);
            }
            else if (binary.Type == ArithType.Bool)
            {
                EmitExpression(binary.Right);
                EmitComparisonOperator(binary.OperatorKind, binary.Left.Type);
            }
            else
            {
                EmitExpression(binary.Right);
                EmitBinaryOperator(binary.OperatorKind, binary.Type);
            }
        }

        /// <summary>
        /// Short-circuit lowering (spec §8.3), entered with the left operand
        /// on the stack: the right operand is evaluated only when the left
        /// one does not decide the result.
        /// </summary>
        private void EmitShortCircuitTail(BoundBinaryExpression binary)
        {
            bool isAnd = binary.OperatorKind == BoundBinaryOperatorKind.LogicalAnd;
            LabelHandle decided = _il.DefineLabel();
            LabelHandle end = _il.DefineLabel();
            _il.Branch(isAnd ? ILOpCode.Brfalse : ILOpCode.Brtrue, decided);
            Pop();
            EmitExpression(binary.Right);
            _il.Branch(ILOpCode.Br, end);

            // The decided path enters with one less value on the stack than
            // the merge point; rewind the tracker before pushing the result.
            SetDepth(_depth - 1);
            _il.MarkLabel(decided);
            _il.LoadConstantI4(isAnd ? 0 : 1);
            Push();
            _il.MarkLabel(end);
        }

        private void EmitVariableLoad(VariableSymbol variable)
        {
            if (variable is ParameterSymbol parameter)
            {
                _il.LoadArgument(parameter.Index);
            }
            else
            {
                _il.LoadLocal(_localSlots[(LocalSymbol)variable]);
            }

            Push();
        }

        private void EmitVariableStore(VariableSymbol variable)
        {
            if (variable is ParameterSymbol parameter)
            {
                _il.StoreArgument(parameter.Index);
            }
            else
            {
                _il.StoreLocal(_localSlots[(LocalSymbol)variable]);
            }

            Pop();
        }

        private int AllocateLocal(LocalSymbol local)
        {
            int slot = AllocateSlot(local.Type);
            _localSlots.Add(local, slot);
            return slot;
        }

        /// <summary>A fresh, anonymous local slot (for-loop end temps and the like).</summary>
        private int AllocateSlot(ArithType type)
        {
            int slot = LocalTypes.Count;
            LocalTypes.Add(type);

            // Past the limit the body is unusable and gets reported, not
            // written. Keep walking so the total is known and every other
            // function is still checked, but never hand the encoder an index
            // its 16-bit ldloc/stloc operands cannot hold.
            return slot < MaxLocalsPerMethod ? slot : 0;
        }

        private int GetPrintTemp(ArithType type)
        {
            if (!_printTemps.TryGetValue(type, out int slot))
            {
                slot = AllocateSlot(type);
                _printTemps.Add(type, slot);
            }

            return slot;
        }

        private void Push(int count = 1)
        {
            _depth += count;
            MaxStack = Math.Max(MaxStack, _depth);
        }

        private void Pop(int count = 1)
        {
            _depth -= count;
            Debug.Assert(_depth >= 0, "the evaluation stack cannot underflow");
        }

        /// <summary>
        /// Rewinds the tracker to a branch target's actual entry depth. Only
        /// merge points inside short-circuit lowering need this; MaxStack
        /// already accounts for the deeper path.
        /// </summary>
        private void SetDepth(int depth)
        {
            Debug.Assert(depth >= 0, "the evaluation stack cannot underflow");
            _depth = depth;
        }
    }
}
