using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Arith.Compiler.Binding;

namespace Arith.Compiler.Emit;

/// <summary>
/// Turns a bound program into an in-memory .NET assembly (design §4.5) using
/// System.Reflection.Metadata, generalizing the techniques prototyped by the
/// FibCommandEmitter experiment (docs/il-emission-notes.md). The emitter may
/// only be handed an error-free program: every expression is concretely
/// typed and value-returning functions are known to return.
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

    private readonly MetadataBuilder _metadata = new();
    private readonly BlobBuilder _ilStream = new();
    private readonly MethodBodyStreamEncoder _bodyStream;
    private readonly Dictionary<FunctionSymbol, MethodDefinitionHandle> _methodHandles = [];
    private readonly Dictionary<ArithType, MemberReferenceHandle> _invariantToString = [];
    private readonly Dictionary<ArithType, MemberReferenceHandle> _invariantTryParse = [];
    private MemberReferenceHandle _consoleWriteLineString;
    private MemberReferenceHandle _cultureGetInvariant;
    private MemberReferenceHandle _stringEquals;
    private MemberReferenceHandle _stringConcat;
    private MemberReferenceHandle _booleanToString;
    private MemberReferenceHandle _booleanTryParse;
    private MemberReferenceHandle _consoleGetError;
    private MemberReferenceHandle _textWriterWriteLine;
    private TypeReferenceHandle _objectType;

    private Emitter() => _bodyStream = new MethodBodyStreamEncoder(_ilStream);

    /// <summary>Emits the PE image for an error-free bound program.</summary>
    public static ImmutableArray<byte> Emit(BoundProgram program, string assemblyName)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentException.ThrowIfNullOrEmpty(assemblyName);
        return new Emitter().EmitProgram(program, assemblyName);
    }

    private ImmutableArray<byte> EmitProgram(BoundProgram program, string assemblyName)
    {
        Debug.Assert(program.EntryPoint is not null, "an error-free program has an entry point");
        Debug.Assert(!program.Functions.IsEmpty, "an error-free program has at least main");

        _metadata.AddModule(
            generation: 0,
            moduleName: _metadata.GetOrAddString(assemblyName + ".dll"),
            mvid: _metadata.GetOrAddGuid(Guid.NewGuid()),
            encId: default,
            encBaseId: default);
        _metadata.AddAssembly(
            name: _metadata.GetOrAddString(assemblyName),
            version: new Version(1, 0, 0, 0),
            culture: default,
            publicKey: default,
            flags: 0,
            hashAlgorithm: AssemblyHashAlgorithm.Sha1);

        AddRuntimeReferences();

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

        // A `main` with parameters receives parsed command-line arguments
        // (spec §5.1) through a synthesized bridge entry point that owns the
        // string[] and the parsing; see EmitEntryPointBridgeBody.
        MethodDefinitionHandle entryPoint = _methodHandles[program.EntryPoint!];
        if (!program.EntryPoint!.Parameters.IsEmpty)
        {
            int bridgeOffset = EmitEntryPointBridgeBody(program.EntryPoint, assemblyName);
            ParameterHandle bridgeParameter = MetadataTokens.ParameterHandle(parameterRow);
            _metadata.AddParameter(
                ParameterAttributes.None, _metadata.GetOrAddString("args"), sequenceNumber: 1);
            entryPoint = _metadata.AddMethodDefinition(
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
        }

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

        ManagedPEBuilder peBuilder = new(
            PEHeaderBuilder.CreateExecutableHeader(),
            new MetadataRootBuilder(_metadata),
            _ilStream,
            entryPoint: entryPoint,
            flags: CorFlags.ILOnly);
        BlobBuilder peBlob = new();
        peBuilder.Serialize(peBlob);
        return [.. peBlob.ToArray()];
    }

    private void AddRuntimeReferences()
    {
        AssemblyReferenceHandle systemRuntime = AddFrameworkReference("System.Runtime");
        AssemblyReferenceHandle systemConsole = AddFrameworkReference("System.Console");

        _objectType = AddTypeReference(systemRuntime, "System", "Object");
        TypeReferenceHandle console = AddTypeReference(systemConsole, "System", "Console");
        TypeReferenceHandle cultureInfo = AddTypeReference(systemRuntime, "System.Globalization", "CultureInfo");
        TypeReferenceHandle formatProvider = AddTypeReference(systemRuntime, "System", "IFormatProvider");
        TypeReferenceHandle stringType = AddTypeReference(systemRuntime, "System", "String");

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

        // bool-to-string ("True"/"False") is culture-independent already.
        TypeReferenceHandle booleanType = AddTypeReference(systemRuntime, "System", "Boolean");
        _booleanToString = _metadata.AddMemberReference(
            booleanType,
            _metadata.GetOrAddString("ToString"),
            MethodSignature(
                isInstanceMethod: true,
                returnType: r => r.Type().String(),
                parameterCount: 0,
                parameters: _ => { }));

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
            _invariantToString[type] = _metadata.AddMemberReference(
                typeReference,
                _metadata.GetOrAddString("ToString"),
                MethodSignature(
                    isInstanceMethod: true,
                    returnType: r => r.Type().String(),
                    parameterCount: 1,
                    parameters: p => p.AddParameter().Type().Type(formatProvider, isValueType: false)));

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
        LabelHandle usage = il.DefineLabel();
        ImmutableArray<ParameterSymbol> parameters = main.Parameters;

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
        string usageLine = $"usage: {assemblyName} "
            + string.Join(" ", parameters.Select(p => $"<{p.Name}: {p.Type}>"));
        il.MarkLabel(usage);
        il.Call(_consoleGetError);
        il.LoadString(_metadata.GetOrAddUserString(usageLine));
        il.OpCode(ILOpCode.Callvirt);
        il.Token(_textWriterWriteLine);
        il.LoadConstantI4(2);
        il.OpCode(ILOpCode.Ret);

        // One local per parameter, in parameter order.
        BlobBuilder localsBlob = new();
        LocalVariablesEncoder locals =
            new BlobEncoder(localsBlob).LocalVariableSignature(parameters.Length);
        foreach (ParameterSymbol parameter in parameters)
        {
            EncodeType(locals.AddVariable().Type(), parameter.Type);
        }

        StandaloneSignatureHandle localSignature =
            _metadata.AddStandaloneSignature(_metadata.GetOrAddBlob(localsBlob));

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

    private static void EncodeType(SignatureTypeEncoder encoder, ArithType type)
    {
        if (type == ArithType.Bool)
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

        /// <summary>The type of each local slot, in slot order (lets first-come, then print temps).</summary>
        public List<ArithType> LocalTypes { get; } = [];

        public int MaxStack { get; private set; }

        public void Emit(BoundFunction function)
        {
            bool returned = EmitBlock(function.Body);
            if (!returned)
            {
                Debug.Assert(
                    function.Symbol.ReturnType == ArithType.Void,
                    "the binder guarantees value-returning functions contain a return");
                _il.OpCode(ILOpCode.Ret);
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
            switch (statement)
            {
                case BoundBlock block:
                    return EmitBlock(block);
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

        /// <summary>`i = i + 1` with a plain add — callers guarantee `i &lt; end` here.</summary>
        private void EmitVariableIncrement(int slot)
        {
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
        /// Replaces the value on the stack with its culture-invariant string
        /// form — the shared lowering behind `print` and `string(value)`.
        /// Numerics call ToString(CultureInfo.InvariantCulture), bool calls
        /// its (already culture-independent) ToString; both are instance
        /// calls on a value type, hence the temp local for the address.
        /// </summary>
        private void EmitConvertToString(ArithType type)
        {
            if (type == ArithType.String)
            {
                return;
            }

            int temp = GetPrintTemp(type);
            _il.StoreLocal(temp);
            Pop();
            _il.LoadLocalAddress(temp);
            Push();
            if (type == ArithType.Bool)
            {
                _il.Call(_emitter._booleanToString);
                Pop();
                Push();
                return;
            }

            _il.Call(_emitter._cultureGetInvariant);
            Push();
            _il.Call(_emitter._invariantToString[type]);
            Pop(2);
            Push();
        }

        private void EmitExpression(BoundExpression expression)
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

                case BoundBinaryExpression
                {
                    OperatorKind: BoundBinaryOperatorKind.LogicalAnd or BoundBinaryOperatorKind.LogicalOr,
                } binary:
                    EmitShortCircuit(binary);
                    break;
                case BoundBinaryExpression binary when binary.Type == ArithType.Bool:
                    EmitExpression(binary.Left);
                    EmitExpression(binary.Right);
                    EmitComparisonOperator(binary.OperatorKind, binary.Left.Type);
                    break;
                case BoundBinaryExpression binary:
                    EmitExpression(binary.Left);
                    EmitExpression(binary.Right);
                    EmitBinaryOperator(binary.OperatorKind, binary.Type);
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

                default:
                    throw new UnreachableException(
                        $"expression '{expression.GetType().Name}' cannot reach emission");
            }
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
        /// Short-circuit lowering (spec §8.3): the right operand is
        /// evaluated only when the left one does not decide the result.
        /// </summary>
        private void EmitShortCircuit(BoundBinaryExpression binary)
        {
            bool isAnd = binary.OperatorKind == BoundBinaryOperatorKind.LogicalAnd;
            LabelHandle decided = _il.DefineLabel();
            LabelHandle end = _il.DefineLabel();
            EmitExpression(binary.Left);
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
            return slot;
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
