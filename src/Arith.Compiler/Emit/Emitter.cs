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
    private MemberReferenceHandle _consoleWriteLineString;
    private MemberReferenceHandle _consoleWriteLineBool;
    private MemberReferenceHandle _cultureGetInvariant;
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
            entryPoint: _methodHandles[program.EntryPoint!],
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

        _consoleWriteLineString = _metadata.AddMemberReference(
            console,
            _metadata.GetOrAddString("WriteLine"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().String()));
        _consoleWriteLineBool = _metadata.AddMemberReference(
            console,
            _metadata.GetOrAddString("WriteLine"),
            MethodSignature(
                isInstanceMethod: false,
                returnType: r => r.Void(),
                parameterCount: 1,
                parameters: p => p.AddParameter().Type().Boolean()));

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
        }
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
    /// stack depth. In the linear step-5 subset, statements after a return
    /// are unreachable and are skipped rather than emitted, so a body never
    /// falls off its end.
    /// </summary>
    private sealed class FunctionBodyEmitter(Emitter emitter, InstructionEncoder il)
    {
        private readonly Emitter _emitter = emitter;
        private readonly InstructionEncoder _il = il;
        private readonly Dictionary<LocalSymbol, int> _localSlots = [];
        private readonly Dictionary<ArithType, int> _printTemps = [];
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

        private void EmitPrint(BoundPrintStatement print)
        {
            ArithType type = print.Argument.Type;
            EmitExpression(print.Argument);
            if (type == ArithType.String)
            {
                _il.Call(_emitter._consoleWriteLineString);
                Pop();
                return;
            }

            if (type == ArithType.Bool)
            {
                _il.Call(_emitter._consoleWriteLineBool);
                Pop();
                return;
            }

            // Numeric: value.ToString(CultureInfo.InvariantCulture) then
            // WriteLine(string) — see AddRuntimeReferences for why. The
            // instance call needs the value's address, hence the temp local.
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
            _il.Call(_emitter._consoleWriteLineString);
            Pop();
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
                case BoundUnaryExpression unary:
                {
                    Debug.Assert(unary.OperatorKind == BoundUnaryOperatorKind.Negation, "only negation exists");
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
                    EmitExpression(binary.Left);
                    EmitExpression(binary.Right);
                    EmitBinaryOperator(binary.OperatorKind, binary.Type);
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

        /// <summary>Spec §11: integer add/sub/mul are checked; div/rem fault at runtime on their own.</summary>
        private void EmitBinaryOperator(BoundBinaryOperatorKind kind, ArithType type)
        {
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
            int slot = LocalTypes.Count;
            _localSlots.Add(local, slot);
            LocalTypes.Add(local.Type);
            return slot;
        }

        private int GetPrintTemp(ArithType type)
        {
            if (!_printTemps.TryGetValue(type, out int slot))
            {
                slot = LocalTypes.Count;
                LocalTypes.Add(type);
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
    }
}
