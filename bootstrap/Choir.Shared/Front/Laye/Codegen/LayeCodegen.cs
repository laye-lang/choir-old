using System;
using System.Diagnostics;
using System.Text;

using Choir.CommandLine;
using Choir.Front.Laye.Sema;

using LLVMSharp;
using LLVMSharp.Interop;

namespace Choir.Front.Laye.Codegen;

internal enum ValueClass
{
    NoClass,
    Integer,
    Sse,
    SseUp,
    X87,
    X87Up,
    ComplexX87,
    // NOTE(local): might need to add a separate memory class depending on how the code
    // generator is able to handle Laye's const ref semantics
    Memory,
}

internal enum ParameterSemantics : byte
{
    Value,
    Reference,
    ConstReference,
}

public sealed class LayeCodegen(LayeModule module, LLVMModuleRef llvmModule)
{
    private record class ParameterInfo
    {
        public LLVMValueRef Value { get; set; }
        public (ValueClass Lo, ValueClass Hi) Class { get; set; }
        public ParameterSemantics Semantics { get; set; }
        public int BeginIndex { get; set; }
    }

    private record class FunctionInfo
    {
        public ParameterInfo? SretParameter { get; set; }
        public bool IsSret => SretParameter is not null;

        public ParameterInfo[] ParameterInfos { get; set; } = [];

        public FunctionInfo()
        {
        }
    }

    public static LLVMModuleRef GenerateIR(LayeModule module)
    {
        var llvmContext = LLVMContextRef.Create();
        var llvmModule = llvmContext.CreateModuleWithName(module.ModuleName);

        var cg = new LayeCodegen(module, llvmModule);

        void GenerateDeclarations(IEnumerable<SemaDeclNamed> decls)
        {
            //foreach (var decl in decls)
            //{
            //    if (decl is SemaDeclStruct @struct)
            //        cg.GenerateStructDeclaration(@struct);
            //}

            foreach (var decl in decls)
            {
                if (decl is SemaDeclFunction function)
                    cg.GenerateFunctionDeclaration(function);
            }
        }

        void GenerateDefinitions(IEnumerable<SemaDeclNamed> decls)
        {
            //foreach (var decl in module.Declarations)
            //{
            //    if (decl is SemaDeclStruct @struct)
            //        cg.GenerateStructDefinition(@struct);
            //}

            foreach (var decl in module.Declarations)
            {
                if (decl is SemaDeclFunction function)
                {
                    if (function.Body is not null)
                        cg.GenerateFunctionDefinition(function);
                }
            }
        }

        foreach (var dependency in module.Dependencies)
            GenerateDeclarations(dependency.ExportedDeclarations);

        GenerateDeclarations(module.Declarations);
        GenerateDefinitions(module.Declarations);

        byte[] moduleData = module.Serialize();
        string sectionName = LayeConstants.GetModuleDescriptionSectionName(module.ModuleName);
        llvmModule.EmbedBuffer(moduleData, sectionName);

        return llvmModule;
    }

    public ChoirContext Context { get; } = module.Context;
    public LayeModule Module { get; } = module;
    public LLVMContextRef LlvmContext { get; } = llvmModule.Context;
    public LLVMModuleRef LlvmModule { get; } = llvmModule;
    public LayeNameMangler Mangler { get; } = new(module.Context, module);
    public Colors Colors { get; } = new(module.Context.UseColor);

    private readonly Dictionary<SemaDeclNamed, LLVMValueRef> _declaredValues = [];
    private readonly Dictionary<SemaDeclNamed, LLVMTypeRef> _declaredTypes = [];
    private readonly Dictionary<SemaDeclFunction, FunctionInfo> _functionInfos = [];
    private readonly Dictionary<SemaStmt, LLVMBasicBlockRef> _breakTargets = [];
    private readonly Dictionary<SemaStmt, LLVMBasicBlockRef> _continueTargets = [];

    private LLVMValueRef _assertHandlerFunction = default;
    private LLVMValueRef _doNothingFunction = default;

    private SemaDeclFunction? CurrentFunction { get; set; }
    private LLVMValueRef CurrentFunctionValue => CurrentFunction is null ? default : _declaredValues[CurrentFunction];

    private int _nameCounter = 0;
    private string NextName(string name = "") => $"{name}{_nameCounter++}";

    private ValueClass Merge(ValueClass accum, ValueClass field)
    {
        if (accum == field || field == ValueClass.NoClass)
            return accum;
        else if (field == ValueClass.Memory)
            return ValueClass.Memory;
        else if (accum == ValueClass.NoClass)
            return field;
        else if (accum == ValueClass.Integer || field == ValueClass.Integer)
            return ValueClass.Integer;
        // NOTE(local): not handling X87 et al
        else return ValueClass.Sse;
    }

    private void PostMerge(int bitWidth, ref ValueClass lo, ref ValueClass hi)
    {
        Context.Assert(bitWidth >= 0, "bit width cannot be less than 0");
        
        if (hi == ValueClass.Memory)
            lo = ValueClass.Memory;

        if (hi == ValueClass.X87Up && lo != ValueClass.X87)
            lo = ValueClass.Memory;

        if (bitWidth > 128 && (lo != ValueClass.Sse && hi != ValueClass.SseUp))
            lo = ValueClass.Memory;

        if (hi == ValueClass.SseUp && lo != ValueClass.Sse)
            lo = ValueClass.Sse;
    }

    private (ValueClass Lo, ValueClass Hi) Classify(SemaTypeQual typeQual, int offsetBase = 0) => Classify(typeQual.Type, offsetBase);
    private (ValueClass Lo, ValueClass Hi) Classify(SemaType type, int offsetBase = 0)
    {
        ValueClass lo = ValueClass.NoClass, hi = ValueClass.NoClass;
        Classify(type, offsetBase, ref lo, ref hi);
        return (lo, hi);
    }

    private void Classify(SemaTypeQual typeQual, int offsetBase, ref ValueClass lo, ref ValueClass hi) => Classify(typeQual.Type, offsetBase, ref lo, ref hi);
    private void Classify(SemaType type, int offsetBase, ref ValueClass lo, ref ValueClass hi)
    {
        type = type.CanonicalType;

        int bitWidth = type.Size.Bits;
        lo = ValueClass.NoClass;
        hi = ValueClass.NoClass;

        if (offsetBase < 64)
            ClassifyInternal(ref lo, ref hi, ref lo);
        else ClassifyInternal(ref lo, ref hi, ref hi);

        void ClassifyInternal(ref ValueClass lo, ref ValueClass hi, ref ValueClass current)
        {
            if (type.IsVoid || type.IsNoReturn)
                current = ValueClass.NoClass;
            else if (type.IsInteger || type.IsBool)
            {
                if (bitWidth <= 64)
                    current = ValueClass.Integer;
                else
                {
                    lo = ValueClass.Integer;
                    hi = ValueClass.Integer;
                }
                // else pass in memory
            }
            else if (type.IsFloat)
            {
                if (bitWidth is 16 or 32 or 64)
                    current = ValueClass.Sse;
                else if (bitWidth == 128)
                {
                    lo = ValueClass.Sse;
                    hi = ValueClass.SseUp;
                }
                else
                {
                    Context.Assert(bitWidth == 80, "unsupported float bit width");
                    lo = ValueClass.X87;
                    hi = ValueClass.X87Up;
                }
            }
            else if (type is SemaTypePointer or SemaTypeBuffer)
                current = ValueClass.Integer;
            else if (type is SemaTypeSlice)
            {
                lo = ValueClass.Integer;
                hi = ValueClass.Integer;
            }
            else if (type is SemaTypeStruct typeStruct)
            {
                // a struct that is more than eight eightbytes just goes in MEMORY
                if (bitWidth >= 8 * 64)
                    return;

                // We need to break up structs into "eightbytes".
                current = ValueClass.NoClass;

                var fieldDecls = typeStruct.DeclStruct.FieldDecls;
                for (int i = 0, currentOffset = offsetBase; i < fieldDecls.Count; i++)
                {
                    var field = fieldDecls[i];
                    var fieldType = field.FieldType;

                    int fieldOffset = Align.AlignTo(currentOffset, fieldType.Align.Bytes);
                    currentOffset = fieldOffset + fieldType.Size.Bits;

                    // TODO(local): any place here where bitfields can go? not sure, might need special C handling for stuff like that.
                    if (0 != (fieldOffset % fieldType.Align.Bits))
                    {
                        lo = ValueClass.Memory;
                        PostMerge(bitWidth, ref lo, ref hi);
                        return;
                    }

                    var (fieldLo, fieldHi) = Classify(fieldType, fieldOffset);

                    lo = Merge(lo, fieldLo);
                    hi = Merge(hi, fieldHi);

                    if (lo == ValueClass.Memory || hi == ValueClass.Memory)
                        break;
                }

                PostMerge(bitWidth, ref lo, ref hi);
            }
            else
            {
                Context.Todo($"Classify {type.ToDebugString(Colors)} ({type.GetType().FullName})");
            }
        }
    }

    private LLVMTypeRef GenerateType(SemaTypeQual typeQual) => GenerateType(typeQual.Type, typeQual.Location);
    private LLVMTypeRef GenerateType(SemaType type, Location location = default)
    {
        type = type.CanonicalType;
        switch (type)
        {
            default:
            {
                Context.Diag.ICE(location, $"Unimplemented Laye type in Choir codegen: {type.GetType().FullName}");
                throw new UnreachableException();
            }

            case SemaTypeNil:
            {
                Context.Diag.ICE(location, "The 'nil' type should not make it to code generation, it should always be converted to a proper type.");
                throw new UnreachableException();
            }

            case SemaTypeBuiltIn builtIn:
            {
                switch (builtIn.Kind)
                {
                    default:
                    {
                        Context.Diag.ICE(location, $"Unimplemented Laye built in type kind in Choir codegen: {builtIn.Kind}");
                        throw new UnreachableException();
                    }

                    case BuiltinTypeKind.NoReturn:
                    case BuiltinTypeKind.Void: return LLVMTypeRef.Void;
                    case BuiltinTypeKind.Bool: return LLVMTypeRef.Int8;
                    case BuiltinTypeKind.BoolSized: return LLVMTypeRef.CreateInt((uint)builtIn.Size.Bits);
                    case BuiltinTypeKind.Int: return LLVMTypeRef.CreateInt((uint)Context.Target.SizeOfSizeType.Bits);
                    case BuiltinTypeKind.IntSized: return LLVMTypeRef.CreateInt((uint)builtIn.Size.Bits);
                    case BuiltinTypeKind.FloatSized:
                    {
                        switch (builtIn.Size.Bits)
                        {
                            default:
                            {
                                Context.Diag.ICE($"Unimplemented Laye float size in Choir codegen: {builtIn.ToDebugString(Colors)}");
                                throw new UnreachableException();
                            }

                            case 32: return LLVMTypeRef.Float;
                            case 64: return LLVMTypeRef.Double;
                            case 80: return LLVMTypeRef.X86FP80;
                            case 128: return LLVMTypeRef.FP128;
                        }
                    }

                    case BuiltinTypeKind.FFIInt: return LLVMTypeRef.CreateInt((uint)builtIn.Size.Bits);
                }
            }

            //case SemaTypeAlias typeAlias: return GenerateType(typeAlias.CanonicalType);
            case SemaTypeStruct typeStruct:
            {
                return LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)type.Size.Bytes);
            }

            case SemaTypeArray typeArray:
            {
                var elementType = GenerateType(typeArray.ElementType);
                long flatLength = typeArray.FlatLength;
                Context.Assert(flatLength <= uint.MaxValue, $"The length of an array exceeded LLVM's max array length. ({flatLength} > {uint.MaxValue})");
                return LLVMTypeRef.CreateArray(elementType, (uint)flatLength);
            }

            case SemaTypePointer: return LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            case SemaTypeBuffer: return LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

            case SemaTypeSlice typeSlice:
            {
                return LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)(Context.Target.SizeOfPointer.Bytes + Context.Types.LayeTypeInt.Size.AlignedTo(Context.Target.AlignOfPointer).Bytes));
            }

            case SemaTypeRange typeRange:
            {
                return LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)type.Size.Bytes);
            }
        }
    }

    private void GenerateFunctionDeclaration(SemaDeclFunction function)
    {
        var cc = function.CallingConvention;
        Context.Assert(cc is CallingConvention.CDecl or CallingConvention.Laye, $"Unsupported calling convention {cc} in codegen.");

        string functionName = Mangler.GetMangledName(function);

        var existingFunction = LlvmModule.GetNamedFunction(functionName);
        if (existingFunction.Handle != IntPtr.Zero)
        {
            // TODO(local): assert some invariants to make sure sema did the right thing
            _declaredValues[function] = existingFunction;
            return;
        }

        var returnClass = Classify(function.ReturnType.Type);
        var functionInfo = _functionInfos[function] = new();

        var parameterTypes = new List<LLVMTypeRef>(function.ParameterDecls.Count * 2 + 1);
        if (returnClass.Lo == ValueClass.Memory)
        {
            functionInfo.SretParameter = new()
            {
                Class = returnClass,
                Semantics = ParameterSemantics.Reference,
            };

            var sretType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            parameterTypes.Add(sretType);
        }

        functionInfo.ParameterInfos = new ParameterInfo[function.ParameterDecls.Count];
        for (int i = 0; i < function.ParameterDecls.Count; i++)
        {
            var paramDecl = function.ParameterDecls[i];
            var paramInfo = functionInfo.ParameterInfos[i] = new()
            {
                Class = paramDecl.IsRefParam ? (ValueClass.Integer, ValueClass.NoClass) : Classify(paramDecl.ParamType),
            };

            if (paramDecl.IsRefParam)
            {
                paramInfo.Semantics = ParameterSemantics.Value;
                //if (cc == CallingConvention.Laye && !paramDecl.ParamType.IsMutable)
                //    paramInfo.Semantics = ParameterSemantics.ConstReference;
                //else if (Context.Abi.PassMemoryValuesByAddress)
                //    paramInfo.Semantics = ParameterSemantics.Reference;
            }
            else if (paramInfo.Class.Lo == ValueClass.Memory)
            {
                if (cc == CallingConvention.Laye && !paramDecl.ParamType.IsMutable)
                    paramInfo.Semantics = ParameterSemantics.ConstReference;
                else if (Context.Abi.PassMemoryValuesByAddress)
                    paramInfo.Semantics = ParameterSemantics.Reference;
            }

            LLVMTypeRef paramType;
            if (paramDecl.IsRefParam)
                paramType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            else if (paramInfo.Semantics is ParameterSemantics.Reference or ParameterSemantics.ConstReference)
                paramType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
            else paramType = GenerateType(paramDecl.ParamType);
            parameterTypes.Add(paramType);
        }

        var returnType = functionInfo.IsSret ? LLVMTypeRef.Void : GenerateType(function.ReturnType);
        var functionType = LLVMTypeRef.CreateFunction(returnType, [.. parameterTypes]);

        var f = LlvmModule.AddFunction(functionName, functionType);
        _declaredValues[function] = f;

        if (functionInfo.IsSret)
        {
            f.AddSRetAttributeAtIndex(LlvmContext, (LLVMAttributeIndex)1, GenerateType(function.ReturnType));
            f.AddNoAliasAttributeAtIndex(LlvmContext, (LLVMAttributeIndex)1);
            f.AddWritableAttributeAtIndex(LlvmContext, (LLVMAttributeIndex)1);
            f.AddDeadOnUnwindAttributeAtIndex(LlvmContext, (LLVMAttributeIndex)1);

            var sretParam = f.GetParam(0);
            functionInfo.SretParameter!.Value = sretParam;
            sretParam.Name = "sret";
        }

        int paramStartIndex = functionInfo.IsSret ? 1 : 0;
        for (int i = 0; i < function.ParameterDecls.Count; i++)
        {
            var paramDecl = function.ParameterDecls[i];
            var paramValue = f.GetParam((uint)(i + paramStartIndex));
            functionInfo.ParameterInfos[i].Value = paramValue;
            paramValue.Name = paramDecl.Name;
        }
    }

    private void GenerateFunctionDefinition(SemaDeclFunction function)
    {
        Context.Assert(function.Body is not null, function.Location, "Attempt to generate code for a function definition when only a declaration is present.");
        Context.Assert(_declaredValues.ContainsKey(function), function.Location, "No LLVM function declaration was generated for this function.");
        Context.Assert(_functionInfos.ContainsKey(function), function.Location, "No function info was generated for this function.");

        CurrentFunction = function;

        var f = _declaredValues[function];
        var fInfo = _functionInfos[function];
        var startBlock = f.AppendBasicBlock("start");

        var builder = LlvmContext.CreateBuilder();
        EnterBlock(builder, startBlock);

        uint paramOffset = fInfo.IsSret ? 1u : 0;
        for (int i = 0; i < function.ParameterDecls.Count; i++)
        {
            var paramType = f.Params[i].TypeOf;
            if (function.ParameterDecls[i].IsRefParam)
            {
                _declaredValues[function.ParameterDecls[i]] = f.GetParam((uint)i + paramOffset);
                //var paramLocal = builder.BuildAlloca(paramType, "ref.param");
                //builder.BuildStore(f.GetParam((uint)i + paramOffset), paramLocal);
                //_declaredValues[function.ParameterDecls[i]] = paramLocal;
            }
            else
            {
                var paramLocal = builder.BuildAlloca(paramType, "param");
                builder.BuildStore(f.GetParam((uint)i + paramOffset), paramLocal);
                _declaredValues[function.ParameterDecls[i]] = paramLocal;
            }
        }

        BuildStmt(builder, function.Body);

        if (builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
        {
            //Context.Assert(function.ReturnType.IsVoid, function.Location, "There was no explicit return in the final block of this function. Sema should have validated it could only return void.");
            if (function.ReturnType.IsVoid)
                builder.BuildRetVoid();
            else builder.BuildUnreachable();
        }
    }

    private LLVMBasicBlockRef EnterBlock(LLVMBuilderRef builder, LLVMBasicBlockRef bb)
    {
        Context.Assert(CurrentFunction is not null, "Not currently within a function definition.");
        Context.Assert(builder != default, CurrentFunction.Location, "Invalid builder.");
        Context.Assert(bb != default, CurrentFunction.Location, "Invalid basic block.");
        Context.Assert(bb.Parent != default, CurrentFunction!.Location, "Block is not inside a function.");
        Context.Assert(bb.Parent == CurrentFunctionValue, CurrentFunction!.Location, "Block is not inside this function.");

        if (builder.InsertBlock.Handle != IntPtr.Zero && builder.InsertBlock.Terminator.Handle == IntPtr.Zero && bb != CurrentFunctionValue.EntryBasicBlock)
        {
            builder.BuildBr(bb);
        }

        builder.PositionAtEnd(bb);
        return bb;
    }

    private LLVMValueRef GetAssertHandlerFunction()
    {
        if (_assertHandlerFunction.Handle != IntPtr.Zero)
            return _assertHandlerFunction;

        var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        var assertHandlerType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [ptrType, ptrType, LLVMTypeRef.Int64], false);
        _assertHandlerFunction = LlvmModule.AddFunction("__laye_assert_handler", assertHandlerType);

        return _assertHandlerFunction;
    }

    private LLVMValueRef GetDoNothingFunction()
    {
        if (_doNothingFunction.Handle != IntPtr.Zero)
            return _doNothingFunction;

        var doNothingType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [], false);
        _doNothingFunction = LlvmModule.AddFunction("llvm.donothing", doNothingType);
        _doNothingFunction.AddNoUnwindAttributeAtIndex(LlvmContext, LLVMAttributeIndex.LLVMAttributeFunctionIndex);
        _doNothingFunction.AddReadNoneAttributeAtIndex(LlvmContext, LLVMAttributeIndex.LLVMAttributeFunctionIndex);

        return _doNothingFunction;
    }

    private void GenerateDefers(LLVMBuilderRef builder, SemaDeferStackNode? current, SemaDeferStackNode? last)
    {
        while (current != last)
        {
            Context.Assert(current is not null, "Well, somehow current != last but current == null, so something did a fuckey.");
            BuildStmt(builder, current!.Defer.DeferredStatement);
            current = current.Previous;
        }
    }

    private void ForwardDeclareIfAllowedOutOfOrder(SemaDeclNamed decl)
    {
        switch (decl)
        {
            case SemaDeclFunction declFunc:
            {
                GenerateFunctionDeclaration(declFunc);
                GenerateFunctionDefinition(declFunc);
            } break;
        }
    }

    private void BuildStmt(LLVMBuilderRef builder, SemaStmt stmt)
    {
        switch (stmt)
        {
            default:
            {
                Context.Diag.ICE(stmt.Location, $"Unimplemented Laye node in Choir builder/codegen: {stmt.GetType().FullName}");
                throw new UnreachableException();
            }

            case SemaDeclFunction declFunc:
            {
                Context.Assert(_declaredValues.ContainsKey(declFunc), declFunc.Location, "This function was not forward declared/defined");
            } break;

            case SemaDeclBinding binding:
            {
                var type = GenerateType(binding.BindingType);
                var storage = builder.BuildAlloca(type, $"{binding.Name}.alloca");
                storage.SetAlignment((uint)binding.BindingType.Align.Bytes);
                _declaredValues[binding] = storage;

                builder.BuildMemSet(storage, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0), binding.BindingType.Size, binding.BindingType.Align);
                if (binding.InitialValue is not null)
                    BuildExprIntoMemory(builder, binding.InitialValue, storage);
            } break;

            case SemaStmtXyzzy:
            {
                var doNothing = GetDoNothingFunction();

                unsafe
                {
                    // Nothing happens.
                    builder.BuildCall2(LLVM.GlobalGetValueType(doNothing), doNothing, []);
                }
            } break;

            case SemaStmtDefer:
            {
                // Skip it, and let the defer generation handle it at the appropriate time
            } break;

            case SemaStmtCompound compound:
            {
                foreach (var child in compound.Statements)
                {
                    if (child is SemaDeclNamed decl)
                        ForwardDeclareIfAllowedOutOfOrder(decl);
                }

                foreach (var child in compound.Statements)
                {
                    BuildStmt(builder, child);
                    if (child.ControlFlow != StmtControlFlow.Fallthrough)
                        break;
                }

                if (compound.ControlFlow == StmtControlFlow.Fallthrough)
                    GenerateDefers(builder, compound.EndDefer, compound.StartDefer);
            } break;

            case SemaStmtReturnVoid @return:
            {
                GenerateDefers(builder, @return.Defer, null);
                builder.BuildRetVoid();
            } break;

            case SemaStmtReturnValue @return:
            {
                var functionInfo = _functionInfos[CurrentFunction!];
                if (functionInfo.IsSret)
                {
                    var sretStorage = CurrentFunctionValue.GetParam(0);
                    Context.Assert(sretStorage.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, "The first parameter of an SRet function must be a pointer, the address to store the return value in.");
                    BuildExprIntoMemory(builder, @return.Value, sretStorage);

                    GenerateDefers(builder, @return.Defer, null);
                    builder.BuildRetVoid();
                }
                else
                {
                    var value = BuildExpr(builder, @return.Value);

                    GenerateDefers(builder, @return.Defer, null);
                    builder.BuildRet(value);
                }
            } break;

            case SemaStmtUnreachable:
            {
                builder.BuildUnreachable();
            } break;

            case SemaStmtIf @if:
            {
                Context.Assert(CurrentFunction is not null, @if.Location, "`if` should be within a function.");
                Context.Assert(@if.Conditions.Count > 0, @if.Location, "`if` should have at least 1 condition.");

                var f = CurrentFunctionValue;
                string ifName = NextName("if");

                var conditionBlocks = new LLVMBasicBlockRef[@if.Conditions.Count];
                var bodyBlocks = new LLVMBasicBlockRef[@if.Conditions.Count + (@if.ElseBody is null ? 0 : 1)];

                for (int i = 0; i < @if.Conditions.Count; i++)
                {
                    conditionBlocks[i] = f.AppendBasicBlock($".{ifName}.cond{i}");
                    bodyBlocks[i] = f.AppendBasicBlock($".{ifName}.pass{i}");
                }

                if (@if.ElseBody is not null)
                    bodyBlocks[@if.Conditions.Count] = f.AppendBasicBlock($".{ifName}.fail");

                var joinBlock = @if.ControlFlow == StmtControlFlow.Fallthrough ? f.AppendBasicBlock($".{ifName}.join") : default;

                builder.BuildBr(conditionBlocks[0]);
                for (int i = 0; i < @if.Conditions.Count; i++)
                {
                    var conditionBlock = conditionBlocks[i];
                    var passBlock = bodyBlocks[i];
                    var failBlock = i < @if.Conditions.Count - 1 ? conditionBlocks[i + 1] : (@if.ElseBody is not null ? bodyBlocks[@if.Conditions.Count] : joinBlock);
                    Context.Assert(failBlock != default, @if.Location, "Invalid control flow semantics for if statement made it to code generation.");

                    EnterBlock(builder, conditionBlock);
                    var condition = BuildExpr(builder, @if.Conditions[i].Condition);

                    // ensure condition is *always* i1
                    condition = builder.BuildTrunc(condition, LLVMTypeRef.Int1, "conv2i1");
                    builder.BuildCondBr(condition, passBlock, failBlock);

                    EnterBlock(builder, passBlock);
                    BuildStmt(builder, @if.Conditions[i].Body);

                    if (builder.InsertBlock.Terminator.Handle != IntPtr.Zero && @if.Conditions[i].ControlFlow == StmtControlFlow.Fallthrough)
                    {
                        Context.Assert(joinBlock != default, @if.Conditions[i].Body.Location, "Invalid control flow semantics for if statement made it to code generation.");
                        builder.BuildBr(joinBlock);
                    }
                }

                if (@if.ElseBody is { } elseBody)
                {
                    EnterBlock(builder, bodyBlocks[@if.Conditions.Count]);
                    BuildStmt(builder, elseBody);

                    if (builder.InsertBlock.Terminator.Handle != IntPtr.Zero && elseBody.ControlFlow == StmtControlFlow.Fallthrough)
                    {
                        Context.Assert(joinBlock != default, elseBody.Location, "Invalid control flow semantics for if statement made it to code generation.");
                        builder.BuildBr(joinBlock);
                    }
                }

                if (joinBlock != default)
                    EnterBlock(builder, joinBlock);
            } break;

            case SemaStmtWhileLoop @while:
            {
                Context.Assert(CurrentFunction is not null, @while.Location, "`if` should be within a function.");

                var f = CurrentFunctionValue;
                string whileName = NextName("while");

                if (@while.ElseBody is { } elseBody)
                {
                    Context.Todo(@while.Location, "Implement else body for while loop.");
                    throw new UnreachableException();
                }
                else
                {
                    var conditionBlock = f.AppendBasicBlock($".{whileName}.loopcond");
                    var bodyBlock = f.AppendBasicBlock($".{whileName}.body");
                    var joinBlock = f.AppendBasicBlock($".{whileName}.join");

                    _breakTargets[@while] = joinBlock;
                    _continueTargets[@while] = conditionBlock;

                    EnterBlock(builder, conditionBlock);
                    var condition = BuildExpr(builder, @while.Condition!);

                    // ensure condition is *always* i1
                    condition = builder.BuildTrunc(condition, LLVMTypeRef.Int1, "conv2i1");
                    builder.BuildCondBr(condition, bodyBlock, joinBlock);

                    EnterBlock(builder, bodyBlock);
                    BuildStmt(builder, @while.Body!);

                    if (@while.Body!.ControlFlow == StmtControlFlow.Fallthrough)
                        builder.BuildBr(conditionBlock);

                    EnterBlock(builder, joinBlock);
                }
            } break;

            case SemaStmtForLoop @for:
            {
                Context.Assert(CurrentFunction is not null, @for.Location, "`if` should be within a function.");

                var f = CurrentFunctionValue;
                string forName = NextName("cfor");

                var initializerBlock = f.AppendBasicBlock($".{forName}.loopinit");
                var conditionBlock = f.AppendBasicBlock($".{forName}.loopcond");
                var incrementBlock = f.AppendBasicBlock($".{forName}.loopinc");
                var bodyBlock = f.AppendBasicBlock($".{forName}.body");
                var joinBlock = f.AppendBasicBlock($".{forName}.join");

                _breakTargets[@for] = joinBlock;
                _continueTargets[@for] = incrementBlock;

                EnterBlock(builder, initializerBlock);
                BuildStmt(builder, @for.Initializer!);

                if (@for.Initializer!.ControlFlow == StmtControlFlow.Fallthrough)
                    builder.BuildBr(conditionBlock);

                EnterBlock(builder, conditionBlock);
                var condition = BuildExpr(builder, @for.Condition!);

                // ensure condition is *always* i1
                condition = builder.BuildTrunc(condition, LLVMTypeRef.Int1, "conv2i1");
                builder.BuildCondBr(condition, bodyBlock, joinBlock);

                EnterBlock(builder, bodyBlock);
                BuildStmt(builder, @for.Body!);

                if (@for.Body!.ControlFlow == StmtControlFlow.Fallthrough)
                    builder.BuildBr(incrementBlock);

                EnterBlock(builder, incrementBlock);
                BuildStmt(builder, @for.Increment!);

                if (@for.Increment!.ControlFlow == StmtControlFlow.Fallthrough)
                    builder.BuildBr(conditionBlock);

                EnterBlock(builder, joinBlock);
            } break;

            case SemaStmtBreak @break:
            {
                Context.Assert(@break.BreakTarget is not null, "Sema should not have left a break target null with no errors");
                Context.Assert(_breakTargets.ContainsKey(@break.BreakTarget), "Codegen should have stored a break target for this");
                builder.BuildBr(_breakTargets[@break.BreakTarget]);
            } break;

            case SemaStmtContinue @continue:
            {
                Context.Assert(@continue.ContinueTarget is not null, "Sema should not have left a continue target null with no errors");
                Context.Assert(_continueTargets.ContainsKey(@continue.ContinueTarget), "Codegen should have stored a continue target for this");
                builder.BuildBr(_continueTargets[@continue.ContinueTarget]);
            } break;

            case SemaStmtAssign assign:
            {
                if (assign.Target is SemaExprLookup { ValueCategory: ValueCategory.Register } register)
                {
                    Context.Assert(register.ReferencedEntity is SemaDeclRegister, register.Location, "Should refrence a register decl");
                    var value = BuildExpr(builder, assign.Value);
                    BuildStoreToRegister(builder, register.Location, (SemaDeclRegister)register.ReferencedEntity, value);
                    break;
                }

                // LValue and Reference LHS targets will both be the same pointer, they're just semantically different in the source language.
                var target = BuildExpr(builder, assign.Target);
                Context.Assert(target.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, assign.Target.Location, "Can't assign to non-pointer target.");
                BuildExprIntoMemory(builder, assign.Value, target);
            } break;

            case SemaStmtDiscard discard:
            {
                BuildExpr(builder, discard.Expr);
            } break;

            case SemaStmtAssert assert:
            {
                var f = CurrentFunctionValue;

                var failBlock = f.AppendBasicBlock("assert.fail");
                var continueBlock = f.AppendBasicBlock("assert.continue");

                var condition = BuildExpr(builder, assert.Condition);
                condition = builder.BuildTrunc(condition, LLVMTypeRef.Int1, "conv2i1");
                builder.BuildCondBr(condition, continueBlock, failBlock);

                EnterBlock(builder, failBlock);

                var source = Context.GetSourceFileById(assert.Location.FileId);
                var locInfo = assert.Location.SeekLineColumn(Context);

                var message = builder.BuildGlobalStringPtr(assert.FailureMessage, "assert.message");
                var fileName = builder.BuildGlobalStringPtr(source?.FilePath ?? "<no-file>", "assert.file_name");
                var lineNumber = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)(locInfo?.Line ?? 0));
                var assertHandler = GetAssertHandlerFunction();

                unsafe
                {
                    builder.BuildCall2(LLVM.GlobalGetValueType(assertHandler), assertHandler, [message, fileName, lineNumber]);
                }

                builder.BuildUnreachable();

                EnterBlock(builder, continueBlock);
            } break;

            case SemaStmtIncrement stmtInc:
            {
                var targetType = stmtInc.Operand.Type.CanonicalType.Type;
                var target = BuildExpr(builder, stmtInc.Operand);
                Context.Assert(target.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, "Target should be an l-value, needs to be 'ptr'.");

                if (targetType is SemaTypeBuffer)
                {
                    var intType = LLVMTypeRef.Int64;
                    var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                    builder.BuildStore(builder.BuildPtrAdd(builder.BuildLoad2(ptrType, target, "load"), LLVMValueRef.CreateConstInt(intType, 1)), target);
                }
                else if (targetType.IsInteger)
                {
                    var intType = GenerateType(targetType);
                    builder.BuildStore(builder.BuildAdd(builder.BuildLoad2(intType, target, "load"), LLVMValueRef.CreateConstInt(intType, 1)), target);
                }
                else
                {
                    Context.Todo(stmtInc.Location, $"Implement increment for type {stmtInc.Operand.Type.ToDebugString(Colors)}");
                }
            } break;

            case SemaStmtDecrement stmtDec:
            {
                var targetType = stmtDec.Operand.Type.CanonicalType.Type;
                var target = BuildExpr(builder, stmtDec.Operand);
                Context.Assert(target.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, "Target should be an l-value, needs to be 'ptr'.");

                if (targetType is SemaTypeBuffer)
                {
                    var intType = LLVMTypeRef.Int64;
                    var ptrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
                    builder.BuildStore(builder.BuildPtrAdd(builder.BuildLoad2(ptrType, target, "load"), LLVMValueRef.CreateConstInt(intType, unchecked((ulong)-1), true)), target);
                }
                else if (targetType.IsInteger)
                {
                    var intType = GenerateType(targetType);
                    builder.BuildStore(builder.BuildSub(builder.BuildLoad2(intType, target, "load"), LLVMValueRef.CreateConstInt(intType, 1)), target);
                }
                else
                {
                    Context.Todo(stmtDec.Location, $"Implement increment for type {stmtDec.Operand.Type.ToDebugString(Colors)}");
                }
            } break;

            case SemaStmtExpr expr:
            {
                BuildExpr(builder, expr.Expr);
            } break;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "API Consistency")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "API Consistency")]
    private LLVMValueRef GetRangeBeginFieldAddr(LLVMBuilderRef builder, LLVMValueRef rangeAddr, SemaTypeRange rangeType)
    {
        return rangeAddr;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "API Consistency")]
    private LLVMValueRef GetRangeEndFieldAddr(LLVMBuilderRef builder, LLVMValueRef rangeAddr, SemaTypeRange rangeType)
    {
        var fieldOffset = rangeType.ElementType.Size.AlignedTo(rangeType.ElementType.Align);
        return builder.BuildPtrAdd(rangeAddr, fieldOffset, "range.end");
    }

    private LLVMValueRef LoadRangeBeginField(LLVMBuilderRef builder, LLVMValueRef rangeAddr, SemaTypeRange rangeType)
    {
        var fieldAddr = GetRangeBeginFieldAddr(builder, rangeAddr, rangeType);
        var load = builder.BuildLoad2(GenerateType(rangeType.ElementType), fieldAddr, "range.begin");
        load.SetAlignment((uint)rangeType.ElementType.Align.Bytes);
        return load;
    }

    private LLVMValueRef LoadRangeEndField(LLVMBuilderRef builder, LLVMValueRef rangeAddr, SemaTypeRange rangeType)
    {
        var fieldAddr = GetRangeEndFieldAddr(builder, rangeAddr, rangeType);
        var load = builder.BuildLoad2(GenerateType(rangeType.ElementType), fieldAddr, "range.end");
        load.SetAlignment((uint)rangeType.ElementType.Align.Bytes);
        return load;
    }

    private LLVMValueRef CalculateRangeCount(LLVMBuilderRef builder, LLVMValueRef rangeAddr, SemaTypeRange rangeType, SemaTypeQual? destType = null)
    {
        var rangeBeginValue = LoadRangeBeginField(builder, rangeAddr, rangeType);
        var rangeEndValue = LoadRangeEndField(builder, rangeAddr, rangeType);
        var rangeCount = builder.BuildSub(rangeEndValue, rangeBeginValue, "range.count");

        if (destType is not null && !destType.TypeEquals(rangeType.ElementType, TypeComparison.TypeOnly))
        {
            if (rangeType.ElementType.Size.Bits < Context.Types.LayeTypeInt.Size.Bits)
                rangeCount = builder.BuildSExt(rangeCount, GenerateType(Context.Types.LayeTypeInt));
            else rangeCount = builder.BuildTrunc(rangeCount, GenerateType(Context.Types.LayeTypeInt));
        }

        return rangeCount;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "API Consistency")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "API Consistency")]
    private LLVMValueRef GetSlicePointerFieldAddr(LLVMBuilderRef builder, LLVMValueRef sliceAddr, SemaTypeSlice sliceType)
    {
        return sliceAddr;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "API Consistency")]
    private LLVMValueRef GetSliceCountFieldAddr(LLVMBuilderRef builder, LLVMValueRef sliceAddr, SemaTypeSlice sliceType)
    {
        var fieldOffset = Context.Types.LayeTypeInt.Size.AlignedTo(Context.Target.AlignOfPointer);
        return builder.BuildPtrAdd(sliceAddr, fieldOffset, "slice.count");
    }

    private LLVMValueRef LoadSlicePointerField(LLVMBuilderRef builder, LLVMValueRef sliceAddr, SemaTypeSlice sliceType)
    {
        var fieldAddr = GetSlicePointerFieldAddr(builder, sliceAddr, sliceType);
        var load = builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), fieldAddr, "slice.ptr");
        load.SetAlignment((uint)Context.Target.AlignOfPointer.Bytes);
        return load;
    }

    private LLVMValueRef LoadSliceCountField(LLVMBuilderRef builder, LLVMValueRef sliceAddr, SemaTypeSlice sliceType)
    {
        var fieldAddr = GetSliceCountFieldAddr(builder, sliceAddr, sliceType);
        var load = builder.BuildLoad2(GenerateType(Context.Types.LayeTypeInt), fieldAddr, "slice.count");
        load.SetAlignment((uint)Context.Types.LayeTypeInt.Align.Bytes);
        return load;
    }

    private void BuildExprIntoMemory(LLVMBuilderRef builder, SemaExpr expr, LLVMValueRef tempAddr)
    {
        unsafe
        {
            switch (expr)
            {
                case SemaExprGrouped grouped: BuildExprIntoMemory(builder, grouped.Inner, tempAddr); break;

                case SemaExprConstructor ctor:
                {
                    foreach (var init in ctor.Inits)
                    {
                        var storage = builder.BuildPtrAdd(tempAddr, init.Offset, "ctor.fieldinit");
                        BuildExprIntoMemory(builder, init.Value, storage);
                    }
                } break;

                case SemaExprRange range:
                {
                    var rangeType = (SemaTypeRange)range.Type.CanonicalType.Type;
                    BuildExprIntoMemory(builder, range.Left, GetRangeBeginFieldAddr(builder, tempAddr, rangeType));
                    BuildExprIntoMemory(builder, range.Right, GetRangeEndFieldAddr(builder, tempAddr, rangeType));
                } break;

                case SemaExprEvaluatedConstant { Value: { Kind: EvaluatedConstantKind.Range, RangeValue: { } rangeValue } }:
                {
                    var rangeType = (SemaTypeRange)expr.Type.CanonicalType.Type;
                    var rangeElementType = GenerateType(rangeType.ElementType);

                    var leftAddr = GetRangeBeginFieldAddr(builder, tempAddr, rangeType);
                    builder.BuildStore(LLVMValueRef.CreateConstInt(rangeElementType, (ulong)rangeValue.Begin, true), leftAddr)
                        .SetAlignment((uint)rangeType.ElementType.Align.Bytes);

                    var rightAddr = GetRangeEndFieldAddr(builder, tempAddr, rangeType);
                    builder.BuildStore(LLVMValueRef.CreateConstInt(rangeElementType, (ulong)rangeValue.End, true), rightAddr)
                        .SetAlignment((uint)rangeType.ElementType.Align.Bytes);
                } break;

                case SemaExprSlice slice:
                {
                    Context.Assert(slice.Operand.IsLValue, slice.Location, "Slice operand must be an l-value.");

                    var sliceType = (SemaTypeSlice)slice.Type.CanonicalType.Type;
                    var rangeType = (SemaTypeRange)slice.Range.Type.CanonicalType.Type;

                    var operandType = slice.Operand.Type.CanonicalType.Type;
                    var operand = BuildExpr(builder, slice.Operand);

                    // the range value is an rvalue semantically, so build it into a temporary so we can do lvalue math on it.
                    var rangeAlloca = builder.BuildAlloca(GenerateType(slice.Range.Type), "slice.range.temp.storage");
                    BuildExprIntoMemory(builder, slice.Range, rangeAlloca);

                    var rangeBeginValue = LoadRangeBeginField(builder, rangeAlloca, rangeType);
                    var rangeCount = CalculateRangeCount(builder, rangeAlloca, rangeType, Context.Types.LayeTypeInt.Qualified(Location.Nowhere));

                    var ptrAddr = GetSlicePointerFieldAddr(builder, tempAddr, sliceType);
                    switch (operandType)
                    {
                        default:
                        {
                            Context.Todo(slice.Operand.Location, $"Unimplemented slice operand type `{operandType.ToDebugString(Colors)}{Colors.Default}`.");
                            throw new UnreachableException();
                        }

                        case SemaTypeArray typeArray:
                        {
                            var elementSize = typeArray.ElementType.Size;
                            var pointerOffset = builder.BuildMul(rangeBeginValue, LLVMValueRef.CreateConstInt(GenerateType(Context.Types.LayeTypeInt), (ulong)elementSize.Bytes), "array.slice.offset");
                            var slicePointer = builder.BuildPtrAdd(operand, pointerOffset, "array.slice.addr");
                            builder.BuildStore(slicePointer, ptrAddr).SetAlignment((uint)Context.Target.AlignOfPointer.Bytes);
                        } break;
                    }

                    var countAddr = GetSliceCountFieldAddr(builder, tempAddr, sliceType);
                    builder.BuildStore(rangeCount, countAddr).SetAlignment(rangeCount.Alignment);
                } break;

                case SemaExprCall call:
                {
                    Context.Assert(call.Callee.Type.CanonicalType.Type is SemaTypeFunction, "The call expression only works on functions");

                    switch (call.Callee)
                    {
                        default:
                        {
                            Context.Todo($"Handle calling (into memory) a callee of kind {call.Callee.GetType().FullName}");
                            throw new UnreachableException();
                        }

                        case SemaExprLookup { ReferencedEntity: SemaDeclFunction staticFunction }:
                        {
                            Context.Assert(staticFunction.VarargsKind != VarargsKind.Laye, call.Location, "Generating code for varargs Laye functions is not supported yet.");

                            var functionInfo = _functionInfos[staticFunction];
                            if (!functionInfo.IsSret)
                            {
                                LLVMValueRef returnValue = BuildExpr(builder, expr);
                                var store = builder.BuildStore(returnValue, tempAddr);
                                store.SetAlignment((uint)staticFunction.ReturnType.Align.Bytes);
                            }
                            else
                            {
#pragma warning disable IDE0028 // Simplify collection initialization
                                var arguments = new List<LLVMValueRef>(1 + (call.Arguments.Count * 2));
                                arguments.Add(tempAddr); // the sret address;
#pragma warning restore IDE0028 // Simplify collection initialization

                                for (int i = 0; i < staticFunction.ParameterDecls.Count; i++)
                                    BuildArgumentForFunctionCall(builder, arguments, staticFunction.ParameterDecls[i], functionInfo.ParameterInfos[i], call.Arguments[i]);

                                if (staticFunction.VarargsKind == VarargsKind.C)
                                {
                                    for (int i = staticFunction.ParameterDecls.Count; i < call.Arguments.Count; i++)
                                        BuildArgumentForFunctionCallAsCVarargs(builder, arguments, call.Arguments[i]);
                                }

                                // no need to BuildExpr when I can look it up manually right now
                                var callee = _declaredValues[staticFunction];
                                builder.BuildCall2(LLVM.GlobalGetValueType(callee), callee, arguments.ToArray(), "");
                            }
                        } break;
                    }
                } break;

                default:
                {
                    LLVMValueRef exprValue = BuildExpr(builder, expr);
                    var store = builder.BuildStore(exprValue, tempAddr);
                    store.SetAlignment((uint)expr.Type.Align.Bytes);
                } break;
            }
        }
    }

    private LLVMValueRef BuildExpr(LLVMBuilderRef builder, SemaExpr expr)
    {
        var exprType = expr.Type.Type.CanonicalType;

        unsafe
        {
            switch (expr)
            {
                default:
                {
                    Context.Diag.ICE(expr.Location, $"Unimplemented Laye node in Choir builder/codegen: {expr.GetType().FullName}");
                    throw new UnreachableException();
                }

                case SemaExprGrouped grouped: return BuildExpr(builder, grouped.Inner);

                case SemaExprConstructor ctor:
                {
                    Context.Diag.ICE(ctor.Location, $"Should not encounter a constructor here; the code generator needs to instantiate a temporary and call {nameof(BuildExprIntoMemory)} instead.");
                    throw new UnreachableException();
                }

                case SemaExprLiteralBool literalBool:
                {
                    var type = GenerateType(expr.Type);
                    return LLVMValueRef.CreateConstInt(type, literalBool.LiteralValue ? 1ul : 0ul, false);
                }

                case SemaExprLiteralNil literalNil when exprType is SemaTypePointer or SemaTypeBuffer:
                    return LLVMValueRef.CreateConstPointerNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));

                case SemaExprLiteralNil literalNil:
                {
                    Context.Diag.ICE(literalNil.Location, $"Unimplemented type for literal 'nil' value: ${expr.Type.ToDebugString(Colors)} (canonical: {exprType.ToDebugString(Colors)})");
                    throw new UnreachableException();
                }

                case SemaExprEvaluatedConstant constant:
                {
                    var type = GenerateType(constant.Type);
                    switch (constant.Value.Kind)
                    {
                        default:
                        {
                            Context.Diag.ICE(expr.Location, $"Unimplemented Laye constant kind in Choir builder/codegen: {constant.Value.Kind}");
                            throw new UnreachableException();
                        }

                        case EvaluatedConstantKind.Bool: return LLVMValueRef.CreateConstInt(type, constant.Value.BoolValue ? 1ul : 0ul, false);
                        case EvaluatedConstantKind.Integer: return LLVMValueRef.CreateConstInt(type, (ulong)(long)constant.Value.IntegerValue, true);
                        case EvaluatedConstantKind.String:
                        {
                            if (expr.Type.Type == Context.Types.LayeTypeI8Buffer)
                                return builder.BuildGlobalStringPtr(constant.Value.StringValue, "str");
                            else
                            {
                                Context.Assert(false, $"Unhandled type for string literal {expr.Type.ToDebugString(Colors)} in LLVM code generator.");
                                throw new UnreachableException();
                            }
                        }
                    }
                }

                case SemaExprFieldStructIndex structField:
                {
                    var lvalue = BuildExpr(builder, structField.Operand);
                    Context.Assert(lvalue.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, "Struct field lookup requires an lvalue, which will be of type `ptr`.");

                    var ptradd = builder.BuildPtrAdd(lvalue, structField.FieldOffset, "field.ptradd");
                    ptradd.Alignment = (uint)structField.Type.Align.Bytes;
                    return ptradd;
                }

                case SemaExprFieldRange rangeField:
                {
                    var rangeElementType = rangeField.Type;

                    var lvalue = BuildExpr(builder, rangeField.Operand);
                    Context.Assert(lvalue.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, "Range field lookup requires an lvalue, which will be of type `ptr`.");

                    var fieldPtr = rangeField.RangeField switch
                    {
                        RangeField.Begin => lvalue,
                        RangeField.End => builder.BuildPtrAdd(lvalue, rangeElementType.Size.AlignedTo(rangeElementType.Align), "range.end.ptradd"),
                        _ => throw new UnreachableException(),
                    };

                    fieldPtr.Alignment = (uint)rangeElementType.Align.Bytes;
                    return fieldPtr;
                }

                case SemaExprIndexBuffer bufferIndex:
                {
                    var lvalue = BuildExpr(builder, bufferIndex.Operand);
                    Context.Assert(lvalue.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, "Buffer index lookup requires a value of type `ptr`.");

                    var typeInt = GenerateType(Context.Types.LayeTypeInt);
                    var typeBuffer = (SemaTypeBuffer)bufferIndex.Operand.Type.CanonicalType.Type;

                    var indexValue = BuildExpr(builder, bufferIndex.Index);

                    var elementSize = typeBuffer.ElementType.Size;
                    var totalOffset = builder.BuildMul(LLVMValueRef.CreateConstInt(typeInt, (ulong)elementSize.Bytes), indexValue);

                    var ptradd = builder.BuildPtrAdd(lvalue, totalOffset, "buff.idx.ptradd");
                    ptradd.Alignment = (uint)bufferIndex.Type.Align.Bytes;
                    return ptradd;
                }

                case SemaExprIndexArray arrayIndex:
                {
                    var lvalue = BuildExpr(builder, arrayIndex.Operand);
                    Context.Assert(lvalue.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, "Array index lookup requires an lvalue, which will be of type `ptr`.");

                    /*

                    int[2, 3, 4] foo;
                    foo[1, 1, 1] = 69;

                    can be either A:

                    foo_flat[1 + (1 * 3) + (1 * 3 * 4)] = 69;

                    or B:

                    foo_flat[(1 * 2) + (1 * 3) + 1] = 69;

                    I think...

                    */

                    var typeInt = GenerateType(Context.Types.LayeTypeInt);
                    Context.Assert(arrayIndex.Operand.Type.CanonicalType.Type is SemaTypeArray, $"An array index expression should have an array type operator, but got {arrayIndex.Operand.Type.ToDebugString(Colors)}.");
                    var typeArray = (SemaTypeArray)arrayIndex.Operand.Type.CanonicalType.Type;
                    var strides = new LLVMValueRef[typeArray.Arity];

                    for (int i = 0; i < strides.Length; i++)
                    {
                        if (i == 0)
                            strides[i] = LLVMValueRef.CreateConstInt(typeInt, 1);
                        else
                        {
                            long strideAgg = typeArray.Lengths.Take(i).Aggregate(1L, (agg, l) => agg * l);
                            strides[i] = LLVMValueRef.CreateConstInt(typeInt, (ulong)strideAgg);
                        }
                    }

                    var totalOffset = BuildExpr(builder, arrayIndex.Indices[0]);
                    for (int i = 1; i < typeArray.Arity; i++)
                    {
                        var indexOffset = BuildExpr(builder, arrayIndex.Indices[i]);
                        Context.Assert(indexOffset.TypeOf == typeInt, $"Index was built with an unexpected integer type ({indexOffset.TypeOf}).");
                        var timesStride = builder.BuildMul(strides[i], indexOffset);
                        totalOffset = builder.BuildAdd(totalOffset, timesStride);
                    }

                    var elementSize = typeArray.ElementType.Size;
                    totalOffset = builder.BuildMul(LLVMValueRef.CreateConstInt(typeInt, (ulong)elementSize.Bytes), totalOffset);

                    var ptradd = builder.BuildPtrAdd(lvalue, totalOffset, "array.idx.ptradd");
                    ptradd.Alignment = (uint)arrayIndex.Type.Align.Bytes;
                    return ptradd;
                }

                case SemaExprIndexSlice sliceIndex:
                {
                    var typeSlice = (SemaTypeSlice)sliceIndex.Operand.Type.CanonicalType.Type;

                    var lvalue = BuildExpr(builder, sliceIndex.Operand);
                    Context.Assert(lvalue.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind, "Slice index lookup requires a value of type `ptr`.");
                    lvalue = LoadSlicePointerField(builder, lvalue, typeSlice);

                    var typeInt = GenerateType(Context.Types.LayeTypeInt);
                    var indexValue = BuildExpr(builder, sliceIndex.Index);

                    var elementSize = typeSlice.ElementType.Size;
                    var totalOffset = builder.BuildMul(LLVMValueRef.CreateConstInt(typeInt, (ulong)elementSize.Bytes), indexValue);

                    var ptradd = builder.BuildPtrAdd(lvalue, totalOffset, "slice.idx.ptradd");
                    ptradd.Alignment = (uint)sliceIndex.Type.Align.Bytes;
                    return ptradd;
                }

                case SemaExprCountof countof:
                {
                    var resultType = GenerateType(countof.Type);
                    if (countof.Operand is SemaTypeQual { Type: { } typeOperand } typeOperandQual)
                    {
                        switch (typeOperand.CanonicalType)
                        {
                            default:
                            {
                                Context.Todo(typeOperandQual.Location, $"Unimplemented type {typeOperandQual.ToDebugString(Colors)}{Colors.Default} in countof.");
                                throw new UnreachableException();
                            }

                            case SemaTypeArray typeArray:
                            {
                                Context.Assert(typeArray.Arity == 1, "Should only be taking the count of a 1-dimensional array type.");
                                return LLVMValueRef.CreateConstInt(resultType, (ulong)typeArray.FlatLength);
                            }
                        }
                    }
                    else if (countof.Operand is SemaExpr exprOperand)
                    {
                        var operandType = exprOperand.Type.CanonicalType.Type;
                        switch (operandType)
                        {
                            default:
                            {
                                Context.Todo(exprOperand.Location, $"Unimplemented expression of type {exprOperand.Type.ToDebugString(Colors)}{Colors.Default} in countof.");
                                throw new UnreachableException();
                            }

                            case SemaTypeArray typeArray:
                            {
                                Context.Assert(typeArray.Arity == 1, "Should only be taking the count of a 1-dimensional array type.");
                                return LLVMValueRef.CreateConstInt(resultType, (ulong)typeArray.FlatLength);
                            }

                            case SemaTypeSlice typeSlice:
                            {
                                var sliceAddr = BuildExpr(builder, exprOperand);
                                return LoadSliceCountField(builder, sliceAddr, typeSlice);
                            }

                            case SemaTypeRange typeRange:
                            {
                                var rangeAlloca = builder.BuildAlloca(GenerateType(typeRange), "countof.range.temp.storage");
                                BuildExprIntoMemory(builder, exprOperand, rangeAlloca);
                                return CalculateRangeCount(builder, rangeAlloca, typeRange, countof.Type);
                            }
                        }
                    }

                    Context.Todo(countof.Location, $"Unimplemented sema node of .NET type {countof.Operand.GetType().FullName} in countof.");
                    throw new UnreachableException();
                }

                case SemaExprNegate negate:
                {
                    var operand = BuildExpr(builder, negate.Operand);
                    if (negate.Operand.Type.CanonicalType.IsInteger)
                        return builder.BuildNeg(operand, "neg");
                    else
                    {
                        Context.Todo($"Unsupported type to negate in LLVM codegen: {negate.Operand.Type.ToDebugString(Colors)}");
                        throw new UnreachableException();
                    }
                }

                case SemaExprComplement complement:
                {
                    var operand = BuildExpr(builder, complement.Operand);
                    if (complement.Operand.Type.CanonicalType.IsInteger)
                        return builder.BuildNot(operand, "compl");
                    else
                    {
                        Context.Todo($"Unsupported type to complement in LLVM codegen: {complement.Operand.Type.ToDebugString(Colors)}");
                        throw new UnreachableException();
                    }
                }

                case SemaExprLogicalNot lognot:
                {
                    var operand = BuildExpr(builder, lognot.Operand);
                    var operandType = GenerateType(lognot.Operand.Type);
                    if (lognot.Operand.Type.CanonicalType.IsBool)
                    {
                        var oTrunc = builder.BuildTrunc(operand, LLVMTypeRef.Int1);
                        var not = builder.BuildNot(oTrunc);
                        return builder.BuildZExt(not, operandType);
                    }
                    else
                    {
                        Context.Todo($"Unsupported type to lognot in LLVM codegen: {lognot.Operand.Type.ToDebugString(Colors)}");
                        throw new UnreachableException();
                    }
                }

                case SemaExprBinaryBuiltIn { Kind: BinaryOperatorKind.LogAnd | BinaryOperatorKind.Bool } logand:
                {
                    var f = CurrentFunctionValue;

                    var boolType = GenerateType(logand.Type);

                    var leftBlock = f.AppendBasicBlock("logand.left");
                    var rightBlock = f.AppendBasicBlock("logand.right");
                    var joinBlock = f.AppendBasicBlock("logand.join");

                    builder.BuildBr(leftBlock);
                    EnterBlock(builder, leftBlock);

                    var left = BuildExpr(builder, logand.Left);
                    left = builder.BuildTrunc(left, LLVMTypeRef.Int1, "conv2i1");
                    builder.BuildCondBr(left, rightBlock, joinBlock);

                    EnterBlock(builder, rightBlock);

                    var right = BuildExpr(builder, logand.Right);
                    right = builder.BuildTrunc(right, LLVMTypeRef.Int1, "conv2i1");
                    builder.BuildBr(joinBlock);

                    EnterBlock(builder, joinBlock);
                    var result = builder.BuildPhi(boolType, "logand.phi");
                    result.AddIncoming([left, right], [leftBlock, rightBlock], 2);

                    return result;
                }

                case SemaExprBinaryBuiltIn { Kind: BinaryOperatorKind.LogOr | BinaryOperatorKind.Bool } logor:
                {
                    var f = CurrentFunctionValue;

                    var boolType = GenerateType(logor.Type);

                    var leftBlock = f.AppendBasicBlock("logor.left");
                    var rightBlock = f.AppendBasicBlock("logor.right");
                    var joinBlock = f.AppendBasicBlock("logor.join");

                    builder.BuildBr(leftBlock);
                    EnterBlock(builder, leftBlock);

                    var left = BuildExpr(builder, logor.Left);
                    left = builder.BuildTrunc(left, LLVMTypeRef.Int1, "conv2i1");
                    builder.BuildCondBr(left, joinBlock, rightBlock);

                    EnterBlock(builder, rightBlock);

                    var right = BuildExpr(builder, logor.Right);
                    right = builder.BuildTrunc(right, LLVMTypeRef.Int1, "conv2i1");
                    builder.BuildBr(joinBlock);

                    EnterBlock(builder, joinBlock);
                    var result = builder.BuildPhi(boolType, "logor.phi");
                    result.AddIncoming([left, right], [leftBlock, rightBlock], 2);

                    return result;
                }

                case SemaExprBinaryBuiltIn binaryBuiltIn:
                {
                    var type = GenerateType(binaryBuiltIn.Type);
                    var left = BuildExpr(builder, binaryBuiltIn.Left);
                    var right = BuildExpr(builder, binaryBuiltIn.Right);

                    switch (binaryBuiltIn.Kind)
                    {
                        default:
                        {
                            Context.Diag.ICE(expr.Location, $"Unimplemented Laye built-in binary operator kind in Choir builder/codegen: {binaryBuiltIn.Kind}");
                            throw new UnreachableException();
                        }

                        case BinaryOperatorKind.Eq | BinaryOperatorKind.Bool: return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, "beq");
                        case BinaryOperatorKind.Neq | BinaryOperatorKind.Bool: return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, "bne");
                        case BinaryOperatorKind.And | BinaryOperatorKind.Bool: return builder.BuildAnd(left, right, "band");
                        case BinaryOperatorKind.Or | BinaryOperatorKind.Bool: return builder.BuildOr(left, right, "bor");

                        case BinaryOperatorKind.Eq | BinaryOperatorKind.Pointer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, "peq");
                        case BinaryOperatorKind.Neq | BinaryOperatorKind.Pointer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, "pne");
                        
                        case BinaryOperatorKind.Add | BinaryOperatorKind.Buffer:
                        {
                            if (binaryBuiltIn.Left.Type.CanonicalType.IsBuffer)
                                return builder.BuildPtrAdd(left, right, ((SemaTypeBuffer)binaryBuiltIn.Left.Type.CanonicalType.Type).ElementType.CanonicalType.Size, "padd");
                            else return builder.BuildPtrAdd(right, left, ((SemaTypeBuffer)binaryBuiltIn.Right.Type.CanonicalType.Type).ElementType.CanonicalType.Size, "padd");
                        }

                        case BinaryOperatorKind.Eq | BinaryOperatorKind.Buffer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, "peq");
                        case BinaryOperatorKind.Neq | BinaryOperatorKind.Buffer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, "pne");
                        case BinaryOperatorKind.Lt | BinaryOperatorKind.Buffer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right, "plt");
                        case BinaryOperatorKind.Le | BinaryOperatorKind.Buffer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right, "ple");
                        case BinaryOperatorKind.Gt | BinaryOperatorKind.Buffer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right, "pgt");
                        case BinaryOperatorKind.Ge | BinaryOperatorKind.Buffer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right, "pge");

                        case BinaryOperatorKind.Eq | BinaryOperatorKind.Integer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, "ieq");
                        case BinaryOperatorKind.Neq | BinaryOperatorKind.Integer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, "ine");
                        case BinaryOperatorKind.Lt | BinaryOperatorKind.Integer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right, "slt");
                        case BinaryOperatorKind.Le | BinaryOperatorKind.Integer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right, "sle");
                        case BinaryOperatorKind.Gt | BinaryOperatorKind.Integer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right, "sgt");
                        case BinaryOperatorKind.Ge | BinaryOperatorKind.Integer: return builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right, "sge");

                        case BinaryOperatorKind.Add | BinaryOperatorKind.Integer: return builder.BuildAdd(left, right, "iadd");
                        case BinaryOperatorKind.Sub | BinaryOperatorKind.Integer: return builder.BuildSub(left, right, "isub");
                        case BinaryOperatorKind.Mul | BinaryOperatorKind.Integer: return builder.BuildMul(left, right, "imul");
                        case BinaryOperatorKind.Div | BinaryOperatorKind.Integer: return builder.BuildSDiv(left, right, "isdiv");
                        case BinaryOperatorKind.UDiv | BinaryOperatorKind.Integer: return builder.BuildUDiv(left, right, "iudiv");
                        case BinaryOperatorKind.Rem | BinaryOperatorKind.Integer: return builder.BuildSRem(left, right, "isrem");
                        case BinaryOperatorKind.URem | BinaryOperatorKind.Integer: return builder.BuildURem(left, right, "iurem");

                        case BinaryOperatorKind.And | BinaryOperatorKind.Integer: return builder.BuildAnd(left, right, "iand");
                        case BinaryOperatorKind.Or | BinaryOperatorKind.Integer: return builder.BuildOr(left, right, "ior");
                        case BinaryOperatorKind.Xor | BinaryOperatorKind.Integer: return builder.BuildXor(left, right, "ixor");
                        case BinaryOperatorKind.Shl | BinaryOperatorKind.Integer: return builder.BuildShl(left, right, "ishl");
                        case BinaryOperatorKind.Shr | BinaryOperatorKind.Integer: return builder.BuildAShr(left, right, "iashr");
                        case BinaryOperatorKind.LShr | BinaryOperatorKind.Integer: return builder.BuildLShr(left, right, "ilshr");
                    }
                }

                case SemaExprCast cast:
                {
                    switch (cast.CastKind)
                    {
                        default:
                        {
                            Context.Diag.ICE(expr.Location, $"Unimplemented Laye cast kind in Choir builder/codegen: {cast.CastKind}");
                            throw new UnreachableException();
                        }

                        case CastKind.ReferenceToLValue: return BuildExpr(builder, cast.Operand);
                        //case CastKind.ReferenceToLValue: return builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), BuildExpr(builder, cast.Operand), "ref2lval");
                        case CastKind.LValueToReference: return BuildExpr(builder, cast.Operand);
                        case CastKind.PointerToLValue: return BuildExpr(builder, cast.Operand);
                        case CastKind.Implicit: return BuildExpr(builder, cast.Operand);

                        case CastKind.IntegralSignExtend:
                        {
                            var type = GenerateType(cast.Type);
                            return builder.BuildSExt(BuildExpr(builder, cast.Operand), type, "intsext");
                        }

                        case CastKind.IntegralZeroExtend:
                        {
                            var type = GenerateType(cast.Type);
                            return builder.BuildZExt(BuildExpr(builder, cast.Operand), type, "intzext");
                        }

                        case CastKind.IntegralTruncate:
                        {
                            var type = GenerateType(cast.Type);
                            return builder.BuildTrunc(BuildExpr(builder, cast.Operand), type, "inttrunc");
                        }

                        case CastKind.LValueToRValue:
                        {
                            var type = GenerateType(cast.Type);
                            var load = builder.BuildLoad2(type, BuildExpr(builder, cast.Operand), "lv2rv");
                            load.SetAlignment((uint)cast.Type.Align.Bytes);
                            return load;
                        }
                    }
                }

                case SemaExprCall call:
                {
                    Context.Assert(call.Callee.Type.CanonicalType.Type is SemaTypeFunction, "The call expression only works on functions");

                    switch (call.Callee)
                    {
                        default:
                        {
                            Context.Todo($"Handle calling a callee of kind {call.Callee.GetType().FullName}");
                            throw new UnreachableException();
                        }

                        case SemaExprLookup { ReferencedEntity: SemaDeclFunction staticFunction }:
                        {
                            Context.Assert(staticFunction.VarargsKind != VarargsKind.Laye, call.Location, "Generating code for Laye varargs functions is not supported yet.");

                            var functionInfo = _functionInfos[staticFunction];
                            Context.Assert(!functionInfo.IsSret, $"Calling of SRet functions cannot be compiled outside of {nameof(BuildExprIntoMemory)}; this case must be handled there.");

                            var arguments = new List<LLVMValueRef>(call.Arguments.Count * 2);
                            for (int i = 0; i < staticFunction.ParameterDecls.Count; i++)
                            {
                                BuildArgumentForFunctionCall(builder, arguments, staticFunction.ParameterDecls[i], functionInfo.ParameterInfos[i], call.Arguments[i]);
                            }

                            if (staticFunction.VarargsKind == VarargsKind.C)
                            {
                                for (int i = staticFunction.ParameterDecls.Count; i < call.Arguments.Count; i++)
                                    BuildArgumentForFunctionCallAsCVarargs(builder, arguments, call.Arguments[i]);
                            }

                            var functionReturnType = GenerateType(staticFunction.ReturnType);
                            string callValueName = staticFunction.ReturnType.IsVoid ? "" : "call";
                            // no need to BuildExpr when I can look it up manually right now
                            var callee = _declaredValues[staticFunction];
                            return builder.BuildCall2(LLVM.GlobalGetValueType(callee), callee, arguments.ToArray(), callValueName);
                        }
                    }
                }

                case SemaExprOverloadSet:
                {
                    Context.Unreachable("Overload set expressions should never escape analysis.");
                    throw new UnreachableException();
                }

                case SemaExprLookup lookup:
                {
                    Context.Assert(lookup.ReferencedEntity is not null, lookup.Location, "Lookup was not resolved to an entity during sema, should not get to codegen.");

                    if (lookup.IsRegister)
                    {
                        Context.Assert(lookup.ReferencedEntity is SemaDeclRegister, lookup.Location, "Lookup of register value category did not reference a register declaration.");
                        return BuildLoadFromRegister(builder, lookup.Location, (SemaDeclRegister)lookup.ReferencedEntity);
                    }

                    Context.Assert(_declaredValues.ContainsKey(lookup.ReferencedEntity), lookup.Location, "Entity to lookup was not declared in codegen yet. May have been generated incorrectly.");
                    return _declaredValues[lookup.ReferencedEntity];
                }
            }
        }
    }

    private LLVMValueRef BuildLoadFromRegister(LLVMBuilderRef builder, Location location, SemaDeclRegister declRegister)
    {
        Context.Assert(declRegister.Type.IsInteger && declRegister.Type.Size.Bits is 32 or 64, location, "Register load can only be 32 or 64 bits for now.");

        string mov = declRegister.Type.Size.Bits == 32 ? "movl" : "movq";

        var functy = LLVMTypeRef.CreateFunction(declRegister.Type.Size.Bits == 32 ? LLVMTypeRef.Int32 : LLVMTypeRef.Int64, []);
        var asmcall = LLVMValueRef.CreateConstInlineAsm(functy, $"{mov} %{declRegister.RegisterName}, $0;", "=r", true, true);

        return builder.BuildCall2(functy, asmcall, new ReadOnlySpan<LLVMValueRef>(), $"load.{declRegister.RegisterName}");
    }

    private LLVMValueRef BuildStoreToRegister(LLVMBuilderRef builder, Location location, SemaDeclRegister declRegister, LLVMValueRef value)
    {
        Context.Assert(declRegister.Type.IsInteger && declRegister.Type.Size.Bits is 32 or 64, location, "Register load can only be 32 or 64 bits for now.");

        string mov = declRegister.Type.Size.Bits == 32 ? "movl" : "movq";

        var functy = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [declRegister.Type.Size.Bits == 32 ? LLVMTypeRef.Int32 : LLVMTypeRef.Int64]);
        var asmcall = LLVMValueRef.CreateConstInlineAsm(functy, $"{mov} $0, %{declRegister.RegisterName}", "r", true, true);

        return builder.BuildCall2(functy, asmcall, new ReadOnlySpan<LLVMValueRef>(ref value), "");
    }

    private ParameterInfo BuildParameterForFunctionDecl(SemaDeclParam paramDecl, List<LLVMTypeRef> outParameterTypes, CallingConvention cc)
    {
        var paramInfo = new ParameterInfo()
        {
            Class = Classify(paramDecl.ParamType),
            BeginIndex = outParameterTypes.Count,
        };

        if (paramInfo.Class.Lo == ValueClass.Memory)
        {
            if (cc == CallingConvention.Laye && !paramDecl.ParamType.IsMutable)
                paramInfo.Semantics = ParameterSemantics.ConstReference;
            else if (Context.Abi.PassMemoryValuesByAddress)
                paramInfo.Semantics = ParameterSemantics.Reference;
        }

        LLVMTypeRef paramType;
        if (paramInfo.Semantics is ParameterSemantics.Reference or ParameterSemantics.ConstReference)
            paramType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
        else paramType = GenerateType(paramDecl.ParamType);
        outParameterTypes.Add(paramType);

        return paramInfo;
    }

    private LLVMValueRef BuildParameterReconstitutionForFunctionHeader(LLVMBuilderRef builder, ParameterInfo paramInfo)
    {
        throw new NotImplementedException();
    }

    private void BuildArgumentForFunctionCall(LLVMBuilderRef builder, List<LLVMValueRef> outArguments, SemaDeclParam declParam, ParameterInfo paramInfo, SemaExpr arg)
    {
        int beginArgumentCount = outArguments.Count;

        var argumentType = arg.Type.CanonicalType.Type;
        var argumentValue = BuildExpr(builder, arg);

        switch (paramInfo.Semantics)
        {
            case ParameterSemantics.Value:
            {
                switch (argumentType)
                {
                    default:
                    {
                        Context.Todo(arg.Location, $"Explicitly handle argument passing for value of type {arg.Type.ToDebugString(Colors.Off)} (canonical: {argumentType.ToDebugString(Colors)})");
                        throw new UnreachableException();
                    }

                    case SemaTypeBuiltIn typeBuiltIn when typeBuiltIn.IsInteger && typeBuiltIn.Size.Bits <= 64:
                    case SemaTypePointer or SemaTypeBuffer:
                    {
                        Context.Assert(paramInfo.Class.Lo == ValueClass.Integer, "Expected integer class");
                        Context.Assert(paramInfo.Class.Hi == ValueClass.NoClass, "Expected no class");
                        outArguments.Add(argumentValue);
                    } break;
                }
            } break;

            case ParameterSemantics.Reference:
            case ParameterSemantics.ConstReference:
            {
                // instantiate a temporary
                var argumentAlloca = builder.BuildAlloca(GenerateType(arg.Type), $"{declParam.Name}.argref.storage");
                builder.BuildStore(argumentValue, argumentAlloca);
                // and pass the address of the temporary as the argument
                outArguments.Add(argumentAlloca);
            } break;
        }

        Context.Assert(beginArgumentCount < outArguments.Count, "Building an argument to a function call did not properly generate any argument values.");
    }

    private void BuildArgumentForFunctionCallAsCVarargs(LLVMBuilderRef builder, List<LLVMValueRef> outArguments, SemaExpr arg)
    {
        int beginArgumentCount = outArguments.Count;

        var argumentType = arg.Type.CanonicalType.Type;
        var argumentValue = BuildExpr(builder, arg);

        switch (argumentType)
        {
            default:
            {
                Context.Todo(arg.Location, $"Explicitly handle argument passing for value of type {arg.Type.ToDebugString(Colors.Off)} (canonical: {argumentType.ToDebugString(Colors)})");
                throw new UnreachableException();
            }

            case SemaTypeBuiltIn typeBuiltIn when typeBuiltIn.IsNumeric && typeBuiltIn.Size.Bits <= 64:
            case SemaTypePointer or SemaTypeBuffer:
            {
                outArguments.Add(argumentValue);
            } break;
        }

        Context.Assert(beginArgumentCount < outArguments.Count, "Building an argument to a function call did not properly generate any argument values.");
    }
}
