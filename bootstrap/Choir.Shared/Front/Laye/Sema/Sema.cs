using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.CompilerServices;

using Choir.CommandLine;
using Choir.Front.Laye.Syntax;

using LLVMSharp;

namespace Choir.Front.Laye.Sema;

#pragma warning disable CA1822 // Mark members as static
public partial class Sema
{
    public static void AnalyseModule(LayeModule module, SyntaxDeclModuleUnit[] unitDecls)
    {
        var context = module.Context;

        var sema = new Sema(module);
        sema.ResolveModuleImports(unitDecls);

        foreach (var unitDecl in unitDecls)
        {
            context.Assert(sema._fileImports.ContainsKey(unitDecl.SourceFile), $"No file import table found for this unit's source file ('{unitDecl.SourceFile.FilePath}').");
            context.Assert(sema._fileScopes.ContainsKey(unitDecl.SourceFile), $"No file scope found for this unit's source file ('{unitDecl.SourceFile.FilePath}').");
            context.Assert(sema._scopeStack.Count == 1 && sema.CurrentScope == module.ModuleScope, "Sema should be at module scope right now.");

            var fileScope = sema._fileScopes[unitDecl.SourceFile];
            using var _ = sema.EnterScope(fileScope);

            sema._currentFileImports = sema._fileImports[unitDecl.SourceFile];

            foreach (var topLevelNode in unitDecl.TopLevelDeclarations)
            {
                if (sema.ForwardDeclareIfAllowedOutOfOrder(topLevelNode, out var declNamed, sema.ModuleScope))
                {
                    if (declNamed.Linkage == Linkage.Exported)
                    {
                        // We're not checking the result of this call here, since it *should* produce the same results
                        // as the forward declaration to module scope.
                        // when it doesn't it should be because it was more permissive, not conflicting with other exports only.
                        bool __ = module.ExportScope.AddDecl(declNamed);
                    }
                }
            }

            sema._currentFileImports = [];
        }

        if (module.Context.HasIssuedError) return;

        foreach (var unitDecl in unitDecls)
        {
            var fileScope = sema._fileScopes[unitDecl.SourceFile];
            using var _ = sema.EnterScope(fileScope);

            sema._currentFileImports = sema._fileImports[unitDecl.SourceFile];

            foreach (var topLevelNode in unitDecl.TopLevelDeclarations)
            {
                sema.PopulateForwardDeclarationIfAvailable(topLevelNode);
            }

            sema._currentFileImports = [];
        }

        if (module.Context.HasIssuedError) return;

        foreach (var unitDecl in unitDecls)
        {
            var fileScope = sema._fileScopes[unitDecl.SourceFile];
            using var _ = sema.EnterScope(fileScope);

            sema._currentFileImports = sema._fileImports[unitDecl.SourceFile];

            foreach (var topLevelNode in unitDecl.TopLevelDeclarations)
            {
                var decl = sema.AnalyseTopLevelDecl(topLevelNode);

                if (decl is SemaDeclFunction declFunction && declFunction.Name == "main" &&
                    declFunction.ReturnType.CanonicalType.Type == context.Types.LayeTypeInt &&
                    declFunction.ParameterDecls.Count == 0 &&
                    declFunction.Body is not null &&
                    !declFunction.IsForeign &&
                    module.ModuleName == LayeConstants.ProgramModuleName)
                {
                    if (declFunction.Linkage is not Linkage.Internal and not Linkage.Exported)
                        module.Context.Diag.Error("The Laye 'main' function must either be defined with no linkage or as 'export'.");

                    declFunction.Linkage = Linkage.Exported;
                    declFunction.ForeignSymbolName = LayeConstants.EntryFunctionName;
                    declFunction.IsForeign = true;
                }

                if (decl is SemaDeclNamed declNamed)
                    module.AddDecl(declNamed);
            }

            sema._currentFileImports = [];
        }
    }

    [Flags]
    private enum BreakContinueFlags
    {
        Break = 1 << 0,
        Continue = 1 << 1,
        BreakAndContinue = Break | Continue,
    }

    private readonly record struct BreakContinueTarget(SemaStmt Stmt, BreakContinueFlags Flags);

    public LayeModule Module { get; }
    public ChoirContext Context { get; }
    public Colors Colors { get; }

    private readonly Dictionary<SourceFile, Dictionary<string, Scope>> _fileImports = [];
    private readonly Dictionary<SourceFile, Scope> _fileScopes = [];
    private readonly Dictionary<SyntaxNode, SemaDeclNamed> _forwardDeclNodes = [];
    private readonly Stack<Scope> _scopeStack = [];
    private readonly Stack<SemaDeclFunction> _functionStack = [];
    private readonly List<BreakContinueTarget> _breakContinueStack = [];

    private Dictionary<string, Scope> _currentFileImports = [];

    private Scope ModuleScope => Module.ModuleScope;
    private Scope CurrentScope => _scopeStack.Peek();
    private SemaDeclFunction CurrentFunction
    {
        get
        {
            if (_functionStack.TryPeek(out var function))
                return function;

            Context.Diag.ICE("Attempt to access the current function during sema from outside any function.");
            throw new UnreachableException();
        }
    }

    private Sema(LayeModule module)
    {
        Module = module;
        Context = module.Context;
        Colors = new(module.Context.UseColor);

        _scopeStack.Push(module.ModuleScope);
        foreach (var sourceFile in module.SourceFiles)
            _fileScopes[sourceFile] = new(module.ModuleScope);
    }

    private void ResolveModuleImports(SyntaxDeclModuleUnit[] unitDecls)
    {
        foreach (var unitDecl in unitDecls)
        {
            var sourceFile = unitDecl.SourceFile;
            var importScopes = _fileImports[sourceFile] = [];

            foreach (var importDecl in unitDecl.Header.ImportDeclarations)
            {
                var referencedModule = Module.Dependencies.Where(m => m.ModuleName == importDecl.ModuleNameText).SingleOrDefault();
                if (referencedModule is null)
                {
                    Context.Diag.Error(importDecl.TokenModuleName.Location, $"Module '{importDecl.ModuleNameText}' not found.");
                    continue;
                }

                if (importDecl.Queries.Count == 0)
                {
                    string scopeName = importDecl.IsAliased ? importDecl.AliasNameText : importDecl.ModuleNameText;
                    importScopes[scopeName] = referencedModule.ExportScope;
                }
                else if (importDecl.Queries.Any(q => q is SyntaxImportQueryWildcard))
                {
                    if (importDecl.Queries.Count != 1)
                    {
                        Context.Diag.Error(importDecl.Location, "An import declaration cannot have both a wildcard '*' and named queries.");
                        Context.Diag.Note(importDecl.Location, "This may change if there is a good enough reason, but it is currently restricted.");
                    }

                    var scope = importDecl.IsExported ? Module.ExportScope : Module.ModuleScope;
                    Context.Assert(!importDecl.IsExported, importDecl.Location, "We need to be careful about how 'export import' works, so it is currently not allowed.");

                    foreach (var (syntaxName, exportedDecls) in referencedModule.ExportScope)
                    {
                        foreach (var decl in exportedDecls)
                            scope.AddDecl(decl);
                    }
                }
                else
                {
                    Context.Todo(importDecl.Location, "If import queries are used, only the wildcard '*' is currently supported.");
                }
            }
        }
    }

    private string TransformOperatorNameToSymbolName(SyntaxOperatorName operatorName)
    {
        switch (operatorName)
        {
            default:
            {
                Context.Unreachable("unhandled operator name type");
                throw new UnreachableException();
            }

            case SyntaxOperatorSimple simple: return $"operator {simple.TokenOperator.Kind.CanonicalOperatorName()}";
            case SyntaxOperatorDelete: return $"operator delete";
            case SyntaxOperatorDeleteArray: return $"operator delete[]";
            case SyntaxOperatorNew: return $"operator new";
            case SyntaxOperatorNewArray: return $"operator new[]";
            case SyntaxOperatorCast @cast:
            {
                var type = AnalyseType(@cast.Type);
                if (type.IsPoison) return "operator cast(poison)";
                return $"operator cast(type {type.Id})";
            }
        }
    }

    private void SetForeignStatusForDeclaration(SemaDeclNamed declNamed, IReadOnlyList<SyntaxAttrib> attribs)
    {
        foreach (var attrib in attribs)
        {
            switch (attrib)
            {
                case SyntaxAttribForeign attribForeign:
                {
                    if (declNamed.IsForeign)
                    {
                        Context.Diag.Error(attrib.Location, "Duplicate `foreign` attribute.");
                        break;
                    }

                    declNamed.IsForeign = true;
                    if (attribForeign.HasForeignName)
                        declNamed.ForeignSymbolName = attribForeign.ForeignNameText;
                    else declNamed.ForeignSymbolName = declNamed.Name;
                } break;
            }
        }
    }

    private Linkage GetLinkageFromAttributeList(IReadOnlyList<SyntaxAttrib> attribs)
    {
        var exportAttribs = attribs.Where(attrib => attrib is SyntaxAttribExport).ToArray();
        if (exportAttribs.Length >= 2)
            Context.Diag.Error(exportAttribs[1].Location, "Duplicate `export` attributes.");

        if (exportAttribs.Length != 0)
            return Linkage.Exported;

        return Linkage.Internal;
    }

    private bool ForwardDeclareIfAllowedOutOfOrder(SyntaxNode node, [NotNullWhen(true)] out SemaDeclNamed? forwardDecl, Scope? scope = null)
    {
        forwardDecl = null;

        var declaringScope = scope ?? CurrentScope;
        bool isAtModuleScope = declaringScope == ModuleScope;

        IReadOnlyList<SyntaxAttrib> attribs;

        switch (node)
        {
            default: return false;

            case SyntaxDeclAlias declAlias:
            {
                attribs = declAlias.Attribs;
                forwardDecl = new SemaDeclAlias(declAlias.Location, declAlias.TokenName.TextValue, declAlias.IsStrict);
            } break;

            case SyntaxDeclStruct declStruct:
            {
                attribs = declStruct.Attribs;

                var declScope = new Scope(declaringScope);
                forwardDecl = new SemaDeclStruct(declStruct.Location, declStruct.TokenName.TextValue)
                {
                    ParentStruct = null,
                    Scope = declScope,
                };
            } break;

            case SyntaxDeclEnum declEnum:
            {
                attribs = declEnum.Attribs;

                var declScope = new Scope(declaringScope);
                forwardDecl = new SemaDeclEnum(declEnum.Location, declEnum.TokenName.TextValue)
                {
                    Scope = declScope,
                };
            } break;

            case SyntaxDeclBinding declBinding:
            {
                if (!isAtModuleScope)
                    return false;

                attribs = declBinding.Attribs;
                forwardDecl = new SemaDeclBinding(declBinding.Location, declBinding.TokenName.TextValue);
            } break;

            case SyntaxDeclFunction declFunction:
            {
                // operators will have an empty name, you can't look them up normally
                string functionName = "";
                if (declFunction.Name is SyntaxToken { Kind: TokenKind.Identifier } tokenIdent)
                    functionName = tokenIdent.TextValue;

                attribs = declFunction.Attribs;
                forwardDecl = new SemaDeclFunction(declFunction.Location, functionName)
                {
                    Scope = new Scope(scope),
                    IsDiscardable = declFunction.Attribs.Any(a => a is SyntaxAttribDiscardable),
                    IsInline = declFunction.Attribs.Any(a => a is SyntaxAttribInline),
                    VarargsKind = declFunction.VarargsKind,
                };
            } break;

            case SyntaxDeclRegister declRegister:
            {
                if (!isAtModuleScope)
                    return false;

                attribs = declRegister.Attribs;
                forwardDecl = new SemaDeclRegister(declRegister.Location, declRegister.TokenRegisterName.TextValue, declRegister.TokenDeclName.TextValue);
            } break;
        }

        Context.Assert(forwardDecl is not null, "Didn't generate the a forward declaration node.");

        forwardDecl.Linkage = GetLinkageFromAttributeList(attribs);
        SetForeignStatusForDeclaration(forwardDecl, attribs);

        if (forwardDecl is SemaDeclFunction f && f.IsForeign && f.ForeignSymbolName is null &&
            ((SyntaxDeclFunction)node).Name is SyntaxOperatorName functionNameOperator)
        {
            Context.Diag.Error(f.Location, "Operator functions cannot be marked as foreign without an explicit foreign name provided.");
        }

        _forwardDeclNodes[node] = forwardDecl;
        DeclareInScope(forwardDecl, declaringScope);

        return true;
    }

    private void PopulateForwardDeclarationIfAvailable(SyntaxNode decl)
    {
        if (!_forwardDeclNodes.TryGetValue(decl, out var forwardDecl))
            return;

        switch (decl)
        {
            case SyntaxDeclStruct declStruct:
            {
                Context.Assert(forwardDecl is SemaDeclStruct, declStruct.Location, "struct declaration did not have sema node of struct type");
                var semaNode = (SemaDeclStruct)forwardDecl;

                AnalyseStruct(declStruct, semaNode);
            } break;

            case SyntaxDeclFunction declFunction:
            {
                var semaNode = (SemaDeclFunction)forwardDecl;
                using var _s = EnterScope(semaNode.Scope);

                semaNode.ReturnType = AnalyseType(declFunction.ReturnType);

                var paramDecls = new List<SemaDeclParam>();
                foreach (var param in declFunction.Params)
                {
                    var paramType = AnalyseType(param.ParamType);
                    var paramDecl = new SemaDeclParam(param.Location, param.TokenName.TextValue, param.IsRefParam, paramType);
                    DeclareInScope(paramDecl);
                    paramDecls.Add(paramDecl);
                }

                if (declFunction.VarargsKind == VarargsKind.Laye && paramDecls.Count > 0 && paramDecls[^1].IsRefParam)
                    Context.Diag.Error(paramDecls[^1].Location, "A variadic parameter may not be marked as 'ref'.");

                semaNode.ParameterDecls = [.. paramDecls];
            } break;
        }
    }

    private SemaDecl AnalyseTopLevelDecl(SyntaxNode decl)
    {
        switch (decl)
        {
            default:
            {
                Context.Unreachable($"invalid top level declaration node {decl.GetType().Name}");
                throw new UnreachableException();
            }

            case SyntaxDeclAlias or SyntaxDeclStruct or SyntaxDeclEnum or SyntaxDeclRegister or SyntaxDeclBinding or SyntaxDeclFunction:
                return (SemaDecl)AnalyseStmtOrDecl(decl);
        }
    }

    private SemaStmt AnalyseStmtOrDecl(SyntaxNode stmt, bool inheritCurrentScope = false)
    {
        if (stmt is SyntaxTypeBuffer or SyntaxTypeBuiltIn or SyntaxTypeNilable or SyntaxTypePointer or SyntaxTypeSlice)
        {
            Context.Assert(false, $"shouldn't ever call {nameof(AnalyseStmtOrDecl)} on nodes which can only be types; those should only exist in a type context and go through {nameof(AnalyseType)}");
            throw new UnreachableException();
        }

        switch (stmt)
        {
            default:
            {
                Context.Assert(false, $"TODO: implement {stmt.GetType().Name} for {nameof(AnalyseStmtOrDecl)}");
                throw new UnreachableException();
            }

            case SyntaxDeclAlias declAlias:
            {
                if (!_forwardDeclNodes.TryGetValue(stmt, out var semaNodeCheck))
                    Context.Unreachable("Alias declarations should have been forward declared.");

                Context.Assert(semaNodeCheck is SemaDeclAlias, declAlias.Location, "alias declaration did not have sema node of alias type");
                var semaNode = (SemaDeclAlias)semaNodeCheck;

                return AnalyseAlias(declAlias, semaNode);
            }

            case SyntaxDeclStruct declStruct:
            {
                if (!_forwardDeclNodes.TryGetValue(stmt, out var semaNodeCheck))
                    Context.Unreachable("Struct declarations should have been forward declared.");

                Context.Assert(semaNodeCheck is SemaDeclStruct, declStruct.Location, "struct declaration did not have sema node of struct type");
                var semaNode = (SemaDeclStruct)semaNodeCheck;

                //AnalyseStruct(declStruct, semaNode);
                return semaNode;
            }

            case SyntaxDeclEnum declEnum:
            {
                if (!_forwardDeclNodes.TryGetValue(stmt, out var semaNodeCheck))
                    Context.Unreachable("Enum declarations should have been forward declared.");

                Context.Assert(semaNodeCheck is SemaDeclEnum, declEnum.Location, "enum declaration did not have sema node of enum type");
                var semaNode = (SemaDeclEnum)semaNodeCheck;

                return AnalyseEnum(declEnum, semaNode);
            }

            case SyntaxDeclBinding declBinding:
            {
                if (!_forwardDeclNodes.TryGetValue(stmt, out var semaNodeCheck))
                {
                    semaNodeCheck = new SemaDeclBinding(declBinding.Location, declBinding.TokenName.TextValue);
                    DeclareInScope((SemaDeclBinding)semaNodeCheck);
                }

                Context.Assert(semaNodeCheck is SemaDeclBinding, declBinding.Location, "binding declaration did not have sema node of binding type");
                var semaNode = (SemaDeclBinding)semaNodeCheck;

                bool isVar = declBinding.BindingType is SyntaxToken { Kind: TokenKind.Var };
                if (!isVar)
                {
                    semaNode.BindingType = AnalyseType(declBinding.BindingType);
                }

                if (declBinding.Initializer is { } syntaxInit)
                {
                    var typeHint = isVar ? null : semaNode.BindingType;
                    semaNode.InitialValue = EvaluateIfPossible(AnalyseExpr(syntaxInit, typeHint));
                    if (typeHint is not null)
                        semaNode.InitialValue = ConvertOrError(semaNode.InitialValue, typeHint);
                    else
                    {
                        bool isImplicitlyTypedConstantExpr = semaNode.InitialValue.Type.CanonicalType.IsLiteral;
                        if (isImplicitlyTypedConstantExpr)
                        {
                            Context.Assert(isVar, declBinding.BindingType.Location, "how did we get here?");
                            Context.Assert(semaNode.InitialValue is SemaExprEvaluatedConstant, semaNode.InitialValue.Location, "Right-hand side of this assignment was flagged as a literal value, but was not a constant expression.");

                            var evalConst = (SemaExprEvaluatedConstant)semaNode.InitialValue;
                            Context.Diag.Error(semaNode.InitialValue.Location, "Constant or literal expressions require type information. A type will not be assumed.");

                            string typeWhereMessage;
                            if (evalConst.Value.Kind == EvaluatedConstantKind.Integer)
                                typeWhereMessage = $"Specify a type here, such as {Context.Types.LayeTypeInt.ToDebugString(Colors)}{Colors.White}, instead of {Colors.LayeKeyword()}var{Colors.White} to fix this error.";
                            else if (evalConst.Value.Kind == EvaluatedConstantKind.Bool)
                                typeWhereMessage = $"Specify a type here, such as {Context.Types.LayeTypeBool.ToDebugString(Colors)}{Colors.White}, instead of {Colors.LayeKeyword()}var{Colors.White} to fix this error.";
                            else if (evalConst.Value.Kind == EvaluatedConstantKind.Float)
                                typeWhereMessage = $"Specify a type here, such as {Context.Types.LayeTypeFloatSized(64).ToDebugString(Colors)}{Colors.White}, instead of {Colors.LayeKeyword()}var{Colors.White} to fix this error.";
                            else if (evalConst.Value.Kind == EvaluatedConstantKind.String)
                                typeWhereMessage = $"Specify a type here, such as {Context.Types.LayeTypeI8Slice.ToDebugString(Colors)}{Colors.White}, instead of {Colors.LayeKeyword()}var{Colors.White} to fix this error.";
                            else if (evalConst.Value.Kind == EvaluatedConstantKind.Range)
                                typeWhereMessage = $"Specify a type here, such as {Context.Types.LayeTypeInt.ToDebugString(Colors)}{Colors.White}.., instead of {Colors.LayeKeyword()}var{Colors.White} to fix this error.";
                            else typeWhereMessage = $"Specify a type here instead of {Colors.LayeKeyword()}var{Colors.White} to fix this error.";

                            Context.Diag.Note(declBinding.BindingType.Location, typeWhereMessage);
                            semaNode.BindingType = SemaTypePoison.Instance.Qualified(semaNode.InitialValue.Location);
                        }
                        else semaNode.BindingType = semaNode.InitialValue.Type;
                    }
                }

                Context.Assert(semaNode.BindingType is not null, declBinding.Location, "Failed to assign a valid type to this declaration");
                return semaNode;
            }

            case SyntaxDeclFunction declFunction:
            {
                if (!_forwardDeclNodes.TryGetValue(stmt, out var semaNodeCheck))
                    Context.Unreachable("Function declarations should have been forward declared.");

                Context.Assert(semaNodeCheck is SemaDeclFunction, declFunction.Location, "function declaration did not have sema node of function type");
                var semaNode = (SemaDeclFunction)semaNodeCheck;

                using var _s = EnterScope(semaNode.Scope);
                using var _f = EnterFunction(semaNode);

                if (declFunction.Body is SyntaxCompound bodyCompound)
                {
                    var body = (SemaStmtCompound)AnalyseStmtOrDecl(bodyCompound, inheritCurrentScope: true);
                    semaNode.Body = body;

                    if (body.ControlFlow < StmtControlFlow.Return && !semaNode.ReturnType.IsVoid)
                    {
                        Context.Diag.Error(semaNode.Location, "Not all code paths return a value");
                    }
                }
                else if (declFunction.Body is not null)
                {
                    Context.Assert(false, declFunction.Body.Location, $"unsupported syntax as function body: {declFunction.Body.GetType().Name}");
                    throw new UnreachableException();
                }

                return semaNode;
            }

            case SyntaxDeclRegister declRegister:
            {
                if (!_forwardDeclNodes.TryGetValue(stmt, out var semaNodeCheck))
                    Context.Unreachable("Register declarations should have been forward declared.");

                Context.Assert(semaNodeCheck is SemaDeclRegister, declRegister.Location, "register declaration did not have sema node of enum type");
                var semaNode = (SemaDeclRegister)semaNodeCheck;

                AnalyseRegister(declRegister, semaNode);
                return semaNode;
            }

            case SyntaxStmtXyzzy: return new SemaStmtXyzzy(stmt.Location);

            case SyntaxCompound stmtCompound:
            {
                var startDefer = CurrentScope.CurrentDefer;

                using var _ = EnterScope(!inheritCurrentScope);
                foreach (var node in stmtCompound.Body)
                    ForwardDeclareIfAllowedOutOfOrder(node, out var _);
                // TODO(local): handle forward declarations in compound statements
                // TODO(local): create scopes in compound statements.
                var childStatements = stmtCompound.Body.Select(node => AnalyseStmtOrDecl(node)).ToArray();

                var endDefer = CurrentScope.CurrentDefer;
                return new SemaStmtCompound(stmtCompound.Location, childStatements)
                {
                    StartDefer = startDefer,
                    EndDefer = endDefer,
                };
            }

            case SyntaxStmtReturn stmtReturn:
            {
                var currentDefer = CurrentScope.CurrentDefer;

                bool hasErroredOnNoReturn = false;
                if (CurrentFunction.ReturnType.IsNoReturn)
                {
                    hasErroredOnNoReturn = true;
                    Context.Diag.Error("Cannot return from a noreturn function.");
                }

                if (stmtReturn.Value is null)
                {
                    if (!hasErroredOnNoReturn && !CurrentFunction.ReturnType.IsVoid)
                        Context.Diag.Error("Must return a value from a non-void function.");

                    return new SemaStmtReturnVoid(stmtReturn.Location)
                    {
                        Defer = currentDefer,
                    };
                }

                if (!hasErroredOnNoReturn && CurrentFunction.ReturnType.IsVoid)
                    Context.Diag.Error("Cannot return a value from a void function.");

                var returnValue = AnalyseExpr(stmtReturn.Value, CurrentFunction.ReturnType);
                if (!CurrentFunction.ReturnType.IsVoid)
                    returnValue = ConvertOrError(returnValue, CurrentFunction.ReturnType);

                return new SemaStmtReturnValue(stmtReturn.Location, returnValue)
                {
                    Defer = currentDefer,
                };
            }

            case SyntaxStmtBreak stmtBreak:
            {
                SemaStmt? target = null;
                for (int i = _breakContinueStack.Count - 1; i >= 0 && target is null; i--)
                {
                    if (_breakContinueStack[i].Flags.HasFlag(BreakContinueFlags.Break))
                        target = _breakContinueStack[i].Stmt;
                }

                if (target is null)
                {
                    Context.Diag.Error(stmtBreak.Location, "'break' statement must be within a loop or switch statement.");
                }

                return new SemaStmtBreak(stmtBreak.Location, target);
            }

            case SyntaxStmtContinue stmtContinue:
            {
                SemaStmt? target = null;
                for (int i = _breakContinueStack.Count - 1; i >= 0 && target is null; i--)
                {
                    if (_breakContinueStack[i].Flags.HasFlag(BreakContinueFlags.Continue))
                        target = _breakContinueStack[i].Stmt;
                }

                if (target is null)
                {
                    Context.Diag.Error(stmtContinue.Location, "'continue' statement must be within a loop statement.");
                }

                return new SemaStmtContinue(stmtContinue.Location, target);
            }

            case SyntaxStmtUnreachable: return new SemaStmtUnreachable(stmt.Location);

            case SyntaxStmtDefer stmtDefer:
            {
                var deferred = AnalyseStmtOrDecl(stmtDefer.Stmt);
                Context.Assert(deferred is not SemaDecl, deferred.Location, "Should not have parsed a declaration in defer context.");
                var semaNode = new SemaStmtDefer(stmtDefer.Location, deferred);

                var deferNode = new SemaDeferStackNode()
                {
                    Previous = CurrentScope.CurrentDefer,
                    Defer = semaNode,
                };

                CurrentScope.CurrentDefer = deferNode;

                return semaNode;
            }

            case SyntaxStmtIf stmtIf:
            {
                Context.Assert(stmtIf.Conditions.Count > 0, stmtIf.Location, "The parser gave us an `if` statement with no conditions.");
                var boolType = Context.Types.LayeTypeBool.Qualified(Location.Nowhere);

                SemaStmtIfPrimary[] conditions = new SemaStmtIfPrimary[stmtIf.Conditions.Count];
                for (int i = 0; i < stmtIf.Conditions.Count; i++)
                {
                    var condition = AnalyseExpr(stmtIf.Conditions[i].Condition, boolType);
                    condition = ConvertOrError(condition, boolType);

                    var body = AnalyseStmtOrDecl(stmtIf.Conditions[i].Body);
                    conditions[i] = new(stmtIf.Conditions[i].Location, condition, body);
                }

                SemaStmt? elseBody = null;
                if (stmtIf.ElseBody is { } syntaxElseBody)
                    elseBody = AnalyseStmtOrDecl(syntaxElseBody);

                return new SemaStmtIf(conditions, elseBody);
            }

            case SyntaxStmtWhileLoop stmtWhile: return AnalyseWhileLoop(stmtWhile);
            case SyntaxStmtForLoop stmtFor: return AnalyseForLoop(stmtFor);

            case SyntaxStmtAssign stmtAssign:
            {
                Context.Assert(stmtAssign.TokenAssignOp.Kind == TokenKind.Equal, stmtAssign.TokenAssignOp.Location, "Fancy assignments are not currently supported in sema.");

                var target = AnalyseExpr(stmtAssign.Left);
                if (!target.IsLValue && !target.IsRegister && !target.IsReference)
                    Context.Diag.Error(target.Location, $"Cannot assign to {target.ValueCategory.ToHumanString(includeArticle: true)}.");

                var value = AnalyseExpr(stmtAssign.Right, target.Type);
                if (target.IsLValue || target.IsRegister || target.IsReference) value = ConvertOrError(value, target.Type);

                return new SemaStmtAssign(target, value);
            }

            case SyntaxStmtDiscard stmtDiscard:
            {
                var expr = AnalyseExpr(stmtDiscard.Expr);
                return new SemaStmtDiscard(stmtDiscard.Location, expr);
            }

            case SyntaxStmtAssert stmtAssert:
            {
                var exprCondition = AnalyseExpr(stmtAssert.Condition, Context.Types.LayeTypeBool.Qualified(Location.Nowhere));
                return new SemaStmtAssert(stmtAssert.Location, exprCondition, stmtAssert.TokenMessage?.TextValue ?? "Assertion failed.");
            }

            case SyntaxStmtExpr { Expr: SyntaxExprUnaryPostfix { TokenOperator.Kind: TokenKind.PlusPlus } exprPostInc } stmtPostInc:
            {
                var operand = AnalyseExpr(exprPostInc.Operand);
                if (operand.Type.IsPoison)
                    return new SemaStmtIncrement(operand);

                if (!operand.IsLValue)
                    Context.Diag.Error(operand.Location, $"Cannot increment {operand.ValueCategory.ToHumanString(includeArticle: true)}.");
                else if (!operand.Type.CanonicalType.IsNumeric && operand.Type.CanonicalType.Type is not SemaTypeBuffer)
                    Context.Diag.Error(operand.Location, $"Cannot increment a value of type {operand.Type.ToDebugString(Colors)}.");
                return new SemaStmtIncrement(operand);
            }

            case SyntaxStmtExpr { Expr: SyntaxExprUnaryPostfix { TokenOperator.Kind: TokenKind.MinusMinus } exprPostDec } stmtPostDec:
            {
                var operand = AnalyseExpr(exprPostDec.Operand);
                if (operand.Type.IsPoison)
                    return new SemaStmtDecrement(operand);

                if (!operand.IsLValue)
                    Context.Diag.Error(operand.Location, $"Cannot decrement {operand.ValueCategory.ToHumanString(includeArticle: true)}.");
                else if (!operand.Type.CanonicalType.IsNumeric && operand.Type.CanonicalType.Type is not SemaTypeBuffer)
                    Context.Diag.Error(operand.Location, $"Cannot decrement a value of type {operand.Type.ToDebugString(Colors)}.");
                return new SemaStmtDecrement(operand);
            }

            case SyntaxStmtExpr stmtExpr:
            {
                var expr = AnalyseExpr(stmtExpr.Expr);
                if (!expr.IsDiscardable)
                {
                    if (!expr.Type.IsVoid && !expr.Type.IsNoReturn && !expr.Type.IsPoison)
                        Context.Diag.Error(stmtExpr.Location, "The result of this expression is implicitly discarded. Use the `discard` keyword to mark this as intentional.");
                }

                return new SemaStmtExpr(expr);
            }
        }
    }

    private void DeclareInScope(SemaDeclNamed decl, Scope? thisScope = null)
    {
        thisScope ??= CurrentScope;
        if (!thisScope.AddDecl(decl))
            Context.Diag.Error(decl.Location, $"Redeclaration of '{decl.Name}' in a non-overloadable context.");
    }

    private SemaDeclAlias AnalyseAlias(SyntaxDeclAlias declAlias, SemaDeclAlias semaNode)
    {
        var aliasedType = AnalyseType(declAlias.Type);
        semaNode.AliasedType = aliasedType;
        return semaNode;
    }

    private void AnalyseStruct(SyntaxDeclStruct declStruct, SemaDeclStruct semaNode)
    {
        const int NOT_A_LEAF = -1;

        var fieldDecls = new SemaDeclField[declStruct.Fields.Count];
        for (int i = 0; i < declStruct.Fields.Count; i++)
        {
            var syntaxDeclField = declStruct.Fields[i];
            var fieldType = AnalyseType(syntaxDeclField.FieldType);
            fieldDecls[i] = new SemaDeclField(syntaxDeclField.Location, syntaxDeclField.TokenName.TextValue, semaNode, fieldType, i);
        }

        var variantDecls = new SemaDeclStruct[declStruct.Variants.Count];
        if (variantDecls.Length != 0)
        {
            int nLeaves = 0;
            for (int i = 0; i < declStruct.Variants.Count; i++)
            {
                var syntaxDeclVariant = declStruct.Variants[i];

                var variantScope = new Scope(semaNode.Scope);
                var variantNode = new SemaDeclStruct(syntaxDeclVariant.Location, syntaxDeclVariant.TokenName.TextValue)
                {
                    ParentStruct = semaNode,
                    Scope = variantScope,
                };

                VariantImpl(syntaxDeclVariant, variantNode, ref nLeaves);
                variantDecls[i] = variantNode;
            }
        }

        semaNode.FieldDecls = fieldDecls;
        semaNode.VariantDecls = variantDecls;

        void VariantImpl(SyntaxDeclStruct declStruct, SemaDeclStruct semaNode, ref int nLeaves)
        {
            Context.Assert(semaNode.IsVariant, "This should be a variant node.");

            // if this is a leaf variant, mark it and increase the leaf count.
            if (semaNode.IsLeaf)
            {
                // let's never let a tag be 0
                semaNode.VariantTag = nLeaves + 1;
                nLeaves += 1;
            }
            // in any other case, this is not a leaf variant.
            else
            {
                semaNode.VariantTag = NOT_A_LEAF;
            }

            var fieldDecls = new SemaDeclField[declStruct.Fields.Count];
            for (int i = 0; i < declStruct.Fields.Count; i++)
            {
                var syntaxDeclField = declStruct.Fields[i];
                var fieldType = AnalyseType(syntaxDeclField.FieldType);

                fieldDecls[i] = new SemaDeclField(syntaxDeclField.Location, syntaxDeclField.TokenName.TextValue, semaNode, fieldType, i);
            }

            var variantDecls = new SemaDeclStruct[declStruct.Variants.Count];
            if (variantDecls.Length != 0)
            {
                for (int i = 0; i < declStruct.Variants.Count; i++)
                {
                    var syntaxDeclVariant = declStruct.Variants[i];

                    var variantScope = new Scope(semaNode.Scope);
                    var variantNode = new SemaDeclStruct(syntaxDeclVariant.Location, syntaxDeclVariant.TokenName.TextValue)
                    {
                        ParentStruct = semaNode,
                        Scope = variantScope,
                    };

                    VariantImpl(syntaxDeclVariant, variantNode, ref nLeaves);
                    variantDecls[i] = variantNode;
                }
            }

            semaNode.FieldDecls = fieldDecls;
            semaNode.VariantDecls = variantDecls;
        }
    }

    private SemaDeclEnum AnalyseEnum(SyntaxDeclEnum declEnum, SemaDeclEnum semaNode)
    {
        Context.Unreachable("Enum declarations are not implemented in sema yet.");
        return semaNode;
    }

    private SemaStmtWhileLoop AnalyseWhileLoop(SyntaxStmtWhileLoop stmtWhile)
    {
        var boolType = Context.Types.LayeTypeBool.Qualified(Location.Nowhere);

        var semaNode = new SemaStmtWhileLoop(stmtWhile.TokenWhile.Location);
        using var _ = EnterBreakContinueScope(semaNode, BreakContinueFlags.BreakAndContinue);

        var condition = AnalyseExpr(stmtWhile.Condition, boolType);
        semaNode.Condition = ConvertOrError(condition, boolType);

        semaNode.Body = AnalyseStmtOrDecl(stmtWhile.Body);
        semaNode.ElseBody = stmtWhile.ElseBody is { } eb ? AnalyseStmtOrDecl(eb) : null;

        return semaNode;
    }

    private SemaStmtForLoop AnalyseForLoop(SyntaxStmtForLoop stmtFor)
    {
        using var _ = EnterScope();

        var boolType = Context.Types.LayeTypeBool.Qualified(Location.Nowhere);

        var semaNode = new SemaStmtForLoop(stmtFor.TokenFor.Location);
        using var _2 = EnterBreakContinueScope(semaNode, BreakContinueFlags.BreakAndContinue);

        semaNode.Initializer = stmtFor.Initializer is { } init ? AnalyseStmtOrDecl(init, true) : new SemaStmtXyzzy(Location.Nowhere);

        var condition = stmtFor.Condition is { } cond ? AnalyseExpr(cond, boolType) : new SemaExprLiteralBool(Location.Nowhere, true, boolType);
        semaNode.Condition = ConvertOrError(condition, boolType);
        
        semaNode.Increment = stmtFor.Increment is { } inc ? AnalyseStmtOrDecl(inc, true) : new SemaStmtXyzzy(Location.Nowhere);

        semaNode.Body = AnalyseStmtOrDecl(stmtFor.Body, true);

        return semaNode;
    }

    private void AnalyseRegister(SyntaxDeclRegister declRegister, SemaDeclRegister semaNode)
    {
        var registerType = AnalyseType(declRegister.RegisterType);
        semaNode.Type = registerType;

        if (!(registerType.IsInteger || registerType.IsFloat))
        {
            Context.Diag.Error(registerType.Location, "Register declarations must only be of integer or float types.");
            return;
        }
    }

    [Flags]
    private enum TypeOrExpr
    {
        Neither = 0,
        Type = 1 << 0,
        Expr = 1 << 1,
        TypeOrExpr = Type | Expr,
    }

    private SemaTypeQual AnalyseType(SyntaxNode syntax)
    {
        var node = AnalyseTypeOrExpr(syntax, which: TypeOrExpr.Type);
        Context.Assert(node is SemaTypeQual, $"Expected a qualified type from the unified {nameof(AnalyseTypeOrExpr)}.");
        return (SemaTypeQual)node;
    }

    private SemaExpr AnalyseExpr(SyntaxNode syntax, SemaTypeQual? typeHint = null)
    {
        var node = AnalyseTypeOrExpr(syntax, typeHint: typeHint, which: TypeOrExpr.Expr);
        Context.Assert(node is SemaExpr, $"Expected an expression from the unified {nameof(AnalyseTypeOrExpr)}.");
        return (SemaExpr)node;
    }

    private BaseSemaNode AnalyseTypeOrExpr(SyntaxNode syntax, SemaTypeQual? typeHint = null, TypeOrExpr which = TypeOrExpr.TypeOrExpr)
    {
        Context.Assert(which.HasFlag(TypeOrExpr.Type) || which.HasFlag(TypeOrExpr.Expr), syntax.Location,
            $"Something called {AnalyseTypeOrExpr} but did not specify if it wanted a type, expression or both; neither is not valid.");

        switch (syntax)
        {
            default:
            {
                Context.Assert(false, $"TODO: implement {syntax.GetType().Name} for {nameof(AnalyseTypeOrExpr)}");
                throw new UnreachableException();
            }

            case SyntaxQualMut qualMut: return MaybeTypeExpr(which, AnalyseType(qualMut.Inner).Qualified(qualMut.Location, TypeQualifiers.Mutable));
            case SyntaxTypeBuiltIn typeBuiltin: return MaybeTypeExpr(which, typeBuiltin.Type.Qualified(typeBuiltin.Location));
            case SyntaxTypePointer typePointer: return MaybeTypeExpr(which, AnalyseTypePointer(typePointer));
            case SyntaxTypeBuffer typeBuffer: return MaybeTypeExpr(which, AnalyseTypeBuffer(typeBuffer));
            case SyntaxTypeSlice typeSlice: return MaybeTypeExpr(which, AnalyseTypeSlice(typeSlice));
            case SyntaxTypeNilable typeNilable: return MaybeTypeExpr(which, AnalyseTypeNilable(typeNilable));
            case SyntaxTypeRange typeRange: return MaybeTypeExpr(which, AnalyseTypeRange(typeRange));

            case SyntaxNameref nameref: return AnalyseNameref(nameref, which);
            case SyntaxIndex index: return AnalyseIndex(index, which);
            case SyntaxTypeof @typeof: return AnalyseTypeof(@typeof, which);

            case SyntaxExprRef @ref: return AnalyseRef(@ref);
            case SyntaxExprUnaryPrefix unaryPrefix: return AnalyseUnaryPrefix(unaryPrefix, typeHint);
            case SyntaxExprUnaryPostfix unaryPostfix: return AnalyseUnaryPostfix(unaryPostfix, typeHint);
            case SyntaxExprBinary binary: return AnalyseBinary(binary, typeHint);
            case SyntaxExprCall call: return AnalyseCall(call, typeHint);
            case SyntaxExprCast cast: return AnalyseCast(cast, typeHint);
            case SyntaxExprField field: return AnalyseField(field, typeHint);
            case SyntaxExprConstructor ctor: return AnalyseConstructor(ctor, typeHint);
            case SyntaxGrouped grouped: return AnalyseGrouped(grouped, typeHint, which);

            case SyntaxExprSizeof @sizeof: return EvaluateIfPossible(AnalyseSizeof(@sizeof, which));
            case SyntaxExprCountof @countof: return EvaluateIfPossible(AnalyseCountof(@countof, which));
            case SyntaxExprRankof @rankof: return UnimplementedSyntax();
            case SyntaxExprAlignof @alignof: return UnimplementedSyntax();
            case SyntaxExprOffsetof @offsetof: return UnimplementedSyntax();

            case SyntaxToken tokenInteger when tokenInteger.Kind == TokenKind.LiteralInteger: return AnalyseLiteralInteger(tokenInteger, which, typeHint);
            case SyntaxToken tokenString when tokenString.Kind == TokenKind.LiteralString: return AnalyseLiteralString(tokenString, which, typeHint);
            case SyntaxToken tokenBool when tokenBool.Kind is TokenKind.True or TokenKind.False: return AnalyseLiteralBool(tokenBool, which, typeHint);
            case SyntaxToken tokenNil when tokenNil.Kind == TokenKind.Nil: return AnalyseLiteralNil(tokenNil, which, typeHint);

            case SyntaxToken unhandledToken: return UnimplementedSyntax();
        }

        BaseSemaNode UnimplementedSyntax()
        {
            Context.Todo(syntax.Location, $"New type/expr analysis does not yet implement syntax node type '{syntax.GetType().Name}'.");
            throw new UnreachableException();
        }
    }

    private SemaTypeQual ErrorExpectedType(Location location)
    {
        Context.Diag.Error(location, "Expected a type.");
        return SemaTypePoison.Instance.Qualified(location);
    }

    private BaseSemaNode MaybeTypeExpr(TypeOrExpr which, SemaTypeQual typeQual)
    {
        if (which.HasFlag(TypeOrExpr.Type)) return typeQual;
        return new SemaExprType(typeQual);
    }

    private SemaTypeQual AnalyseTypePointer(SyntaxTypePointer typePointer)
    {
        var elementType = AnalyseType(typePointer.Inner);
        return new SemaTypePointer(Context, elementType).Qualified(typePointer.Location);
    }

    private SemaTypeQual AnalyseTypeBuffer(SyntaxTypeBuffer typeBuffer)
    {
        Context.Assert(typeBuffer.TerminatorExpr is null, "Buffer terminators are not supported.");
        var elementType = AnalyseType(typeBuffer.Inner);
        return new SemaTypeBuffer(Context, elementType).Qualified(typeBuffer.Location);
    }

    private SemaTypeQual AnalyseTypeSlice(SyntaxTypeSlice typeSlice)
    {
        var elementType = AnalyseType(typeSlice.Inner);
        return new SemaTypeSlice(Context, elementType).Qualified(typeSlice.Location);
    }

    private SemaTypeQual AnalyseTypeNilable(SyntaxTypeNilable typeNilable)
    {
        var elementType = AnalyseType(typeNilable.Inner);
        return new SemaTypeNilable(elementType).Qualified(typeNilable.Location);
    }

    private SemaTypeQual AnalyseTypeRange(SyntaxTypeRange typeRange)
    {
        var elementType = AnalyseType(typeRange.Inner);
        return new SemaTypeRange(elementType).Qualified(typeRange.Location);
    }

    private BaseSemaNode AnalyseNameref(SyntaxNameref nameref, TypeOrExpr which)
    {
        var lookupResult = LookupName(nameref, CurrentScope);

        // not found, simple as
        if (lookupResult is LookupNotFound notFound)
        {
            Context.Diag.Error(notFound.Location, $"The name '{notFound.Name}' does not exist in this context.");

            var ambiguousType = SemaTypePoison.Instance.Qualified(nameref.Location);
            if (which.HasFlag(TypeOrExpr.Type))
                return ambiguousType;

            return new SemaExprLookup(nameref.Location, ambiguousType, null)
            {
                Dependence = ExprDependence.ErrorDependent,
            };
        }

        // get ambiguous names out of here first, they're useless to make guesses about
        if (lookupResult is LookupAmbiguous ambiguous)
        {
            Context.Diag.Error(ambiguous.Location, $"The name '{ambiguous.Name}' is ambiguous in this context.");
            Context.Diag.Note("The following conflicting declarations were found:");
            foreach (var ambiguousDecl in ambiguous.Decls)
                Context.Diag.Note(ambiguousDecl.Location, "");

            var ambiguousType = SemaTypePoison.Instance.Qualified(nameref.Location);
            if (which.HasFlag(TypeOrExpr.Type))
                return ambiguousType;

            return new SemaExprLookup(nameref.Location, ambiguousType, null)
            {
                Dependence = ExprDependence.ErrorDependent,
            };
        }

        // (potentially also ambiguous) non-scope encountered
        if (lookupResult is LookupNonScopeInPath nonScopeInPath)
        {
            Context.Diag.Error(nonScopeInPath.Location, $"The name '{nonScopeInPath.Name}' is not a namespace; cannot continue name lookup through it.");
            if (nonScopeInPath.Decls.Length == 1)
            {
                var nonScopeDecl = nonScopeInPath.Decls[0];
                Context.Diag.Note(nonScopeDecl.Location, "This non-scope declaration prevented the lookup from continuing.");
            }
            else
            {
                Context.Diag.Note("The following non-scope declarations prevented the lookup from continuing:");
                foreach (var nonScopeDecl in nonScopeInPath.Decls)
                    Context.Diag.Note(nonScopeDecl.Location, "");
            }

            var ambiguousType = SemaTypePoison.Instance.Qualified(nameref.Location);
            if (which.HasFlag(TypeOrExpr.Type))
                return ambiguousType;

            return new SemaExprLookup(nameref.Location, ambiguousType, null)
            {
                Dependence = ExprDependence.ErrorDependent,
            };
        }

        // treat overloading as ambiguous if we're not expecting an expression as the result of this lookup
        if (lookupResult is LookupOverloads typeOverloads && !which.HasFlag(TypeOrExpr.Expr))
        {
            Context.Diag.Error(typeOverloads.Location, $"The name '{typeOverloads.Name}' is ambiguous in this context.");
            Context.Diag.Note("The following conflicting declarations were found:");
            foreach (var overloadDecl in typeOverloads.Decls)
                Context.Diag.Note(overloadDecl.Location, "");

            Context.Assert(which.HasFlag(TypeOrExpr.Type), typeOverloads.Location, "Must expect this to be a type here.");
            return SemaTypePoison.Instance.Qualified(nameref.Location);
        }

        if (lookupResult is LookupOverloads exprOverloads)
        {
            return new SemaExprOverloadSet(nameref.Location, exprOverloads.Decls)
            {
                ValueCategory = ValueCategory.RValue,
            };
        }

        Context.Assert(lookupResult is LookupSuccess, nameref.Location, "The result of lookup should be a success at this point, all other cases should have been handled previously.");
        
        var success = (LookupSuccess)lookupResult;
        var decl = success.Decl;

        var valueCategory = ValueCategory.LValue;
        SemaTypeQual? exprType;

        switch (decl)
        {
            default:
            {
                Context.Diag.ICE(success.Location, "Unrecognized or unsupported declarations in lookup resolution.");
                throw new UnreachableException();
            }

            // TODO(local): enum variants, struct/variant fields

            case SemaDeclStruct declStruct: return MaybeTypeExpr(which, new SemaTypeStruct(declStruct).Qualified(success.Location));
            case SemaDeclEnum declEnum: return MaybeTypeExpr(which, new SemaTypeEnum(declEnum).Qualified(success.Location));
            case SemaDeclAlias declAlias: return MaybeTypeExpr(which, new SemaTypeAlias(declAlias).Qualified(success.Location));
            case SemaDeclDelegate declDelegate: return MaybeTypeExpr(which, new SemaTypeDelegate(Context, declDelegate).Qualified(success.Location));

            case SemaDeclBinding declBinding:
            {
                if (!which.HasFlag(TypeOrExpr.Expr))
                    return NotATypeName();

                exprType = declBinding.BindingType;
            } break;

            case SemaDeclParam declParam:
            {
                if (!which.HasFlag(TypeOrExpr.Expr))
                    return NotATypeName();

                exprType = declParam.ParamType;
                if (declParam.IsRefParam)
                    valueCategory = ValueCategory.Reference;
            } break;

            case SemaDeclFunction declFunction:
            {
                if (!which.HasFlag(TypeOrExpr.Expr))
                    return NotATypeName();

                exprType = declFunction.FunctionType(Context).Qualified(decl.Location);
                valueCategory = ValueCategory.RValue;
            } break;

            case SemaDeclRegister declRegister:
            {
                if (!which.HasFlag(TypeOrExpr.Expr))
                    return NotATypeName();

                exprType = declRegister.Type;
                valueCategory = ValueCategory.Register;
            } break;
        }

        Context.Assert(which.HasFlag(TypeOrExpr.Expr), nameref.Location, "Managed to return a non-type expression when an expression was not expected. figure that out please.");
        return new SemaExprLookup(nameref.Location, exprType, success.Decl)
        {
            ValueCategory = valueCategory,
        };

        SemaTypeQual NotATypeName()
        {
            Context.Diag.Error(success.Location, $"'{success.Name}' is not a type name.");
            Context.Diag.Note(decl.Location, "This is the non-type declaration referenced.");
            return SemaTypePoison.Instance.Qualified(success.Location);
        }
    }

    private BaseSemaNode AnalyseIndexRanged(SyntaxIndex index, SemaExpr operand, TypeOrExpr which)
    {
        Context.Assert(index.Indices is [SyntaxExprBinary { TokenOperator.Kind: TokenKind.DotDot or TokenKind.DotDotEqual }], "A ranged index must have exactly one index expression which must be a binary expression with '..' or '..=' as its operator.");

        var rangeIndexBinary = (SyntaxExprBinary)index.Indices[0];
        var operandTypeCanon = operand.Type.CanonicalType.Type;

        if (operandTypeCanon is SemaTypeBuffer typeBuffer)
        {
            operand = LValueToRValue(operand);

            SemaExpr rangeRightExpr;
            if (rangeIndexBinary.Right is SyntaxExprEmpty)
            {
                Context.Diag.Error(rangeIndexBinary.Right.Location, "Slicing a buffer requires an explicit range end value.");
                rangeRightExpr = new SemaExprLiteralInteger(rangeIndexBinary.Right.Location, 0, SemaTypePoison.Instance.Qualified(rangeIndexBinary.Right.Location));
            }
            else rangeRightExpr = AnalyseExpr(rangeIndexBinary.Right);

            SemaExpr rangeLeftExpr;
            if (rangeIndexBinary.Left is SyntaxExprEmpty)
                rangeLeftExpr = new SemaExprLiteralInteger(rangeIndexBinary.Left.Location, 0, Context.Types.LayeTypeInt.Qualified(rangeIndexBinary.Left.Location));
            else rangeLeftExpr = AnalyseExpr(rangeIndexBinary.Left);

            var range = new SemaExprRange(rangeIndexBinary.TokenOperator, CreateRangeType(ref rangeLeftExpr, ref rangeRightExpr), rangeLeftExpr, rangeRightExpr);
            return new SemaExprSlice(index.Location, operand, range, Context.Types.LayeTypeSlice(typeBuffer.ElementType));
        }
        else if (operandTypeCanon is SemaTypeArray typeArray)
        {
            // operand = LValueToRValue(operand);

            long arrayLength = 0;
            if (typeArray.Lengths.Count == 1)
                arrayLength = typeArray.Lengths[0];
            else Context.Diag.Error(index.Operand.Location, "Can only slice an array with a single dimension.");

            SemaExpr rangeLeftExpr;
            if (rangeIndexBinary.Left is SyntaxExprEmpty)
                rangeLeftExpr = new SemaExprLiteralInteger(rangeIndexBinary.Left.Location, 0, Context.Types.LayeTypeInt.Qualified(rangeIndexBinary.Left.Location));
            else rangeLeftExpr = AnalyseExpr(rangeIndexBinary.Left);

            SemaExpr rangeRightExpr;
            if (rangeIndexBinary.Right is SyntaxExprEmpty)
                rangeRightExpr = new SemaExprLiteralInteger(rangeIndexBinary.Right.Location, arrayLength, Context.Types.LayeTypeInt.Qualified(rangeIndexBinary.Right.Location));
            else rangeRightExpr = AnalyseExpr(rangeIndexBinary.Right);

            var range = new SemaExprRange(rangeIndexBinary.TokenOperator, CreateRangeType(ref rangeLeftExpr, ref rangeRightExpr), rangeLeftExpr, rangeRightExpr);
            return new SemaExprSlice(index.Location, operand, range, Context.Types.LayeTypeSlice(typeArray.ElementType));
        }
        else if (operandTypeCanon is SemaTypeSlice typeSlice)
        {
            operand = LValueToRValue(operand);

            SemaExpr rangeLeftExpr;
            if (rangeIndexBinary.Left is SyntaxExprEmpty)
                rangeLeftExpr = new SemaExprLiteralInteger(rangeIndexBinary.Left.Location, 0, Context.Types.LayeTypeInt.Qualified(rangeIndexBinary.Left.Location));
            else rangeLeftExpr = AnalyseExpr(rangeIndexBinary.Left);

            SemaExpr rangeRightExpr;
            if (rangeIndexBinary.Right is SyntaxExprEmpty)
                rangeRightExpr = new SemaExprCountof(rangeIndexBinary.Right.Location, operand, Context.Types.LayeTypeInt.Qualified(rangeIndexBinary.Right.Location));
            else rangeRightExpr = AnalyseExpr(rangeIndexBinary.Right);

            var range = new SemaExprRange(rangeIndexBinary.TokenOperator, CreateRangeType(ref rangeLeftExpr, ref rangeRightExpr), rangeLeftExpr, rangeRightExpr);
            return new SemaExprSlice(index.Location, operand, range, Context.Types.LayeTypeSlice(typeSlice.ElementType));
        }
        else
        {
            if (!operand.Type.IsPoison)
                Context.Diag.Error(index.Location, $"Cannot index value of type {operand.Type.ToDebugString(Colors)}.");
            return new SemaExprIndexInvalid(operand, [])
            {
                ValueCategory = ValueCategory.LValue
            };
        }

        SemaTypeQual CreateRangeType(ref SemaExpr rangeLeftExpr, ref SemaExpr rangeRightExpr)
        {
            if (rangeLeftExpr.Type.IsPoison || rangeRightExpr.Type.IsPoison)
                return SemaTypePoison.Instance.Qualified(rangeIndexBinary.Location);
            else if (!rangeRightExpr.Type.IsInteger || !rangeLeftExpr.Type.IsInteger)
            {
                if (!rangeLeftExpr.Type.IsInteger)
                    Context.Diag.Error(rangeLeftExpr.Location, "Operand to range expression must evaluate to an integer.");
                if (!rangeRightExpr.Type.IsInteger)
                    Context.Diag.Error(rangeRightExpr.Location, "Operand to range expression must evaluate to an integer.");
                return SemaTypePoison.Instance.Qualified(rangeIndexBinary.Location);
            }
            else
            {
                if (ConvertToCommonTypeOrError(ref rangeLeftExpr, ref rangeRightExpr, Context.Types.LayeTypeInt.Qualified(rangeIndexBinary.Location)))
                {
                    Context.Assert(rangeLeftExpr.Type.IsInteger, rangeLeftExpr.Location, "Left side of range operator must have evaluated to integer type.");
                    Context.Assert(rangeRightExpr.Type.IsInteger, rangeRightExpr.Location, "Right side of range operator must have evaluated to integer type.");
                    return new SemaTypeRange(rangeLeftExpr.Type).Qualified(rangeIndexBinary.Location);
                }
                else return SemaTypePoison.Instance.Qualified(rangeIndexBinary.Location);
            }
        }
    }

    private BaseSemaNode AnalyseIndex(SyntaxIndex index, TypeOrExpr which)
    {
        var baseOperand = AnalyseTypeOrExpr(index.Operand, null, which);
        SemaExpr[] indices;

        if (baseOperand is SemaTypeQual elementType)
        {
            Context.Assert(which.HasFlag(TypeOrExpr.Type), index.Location, "Somehow we got a type back for the operand of an index syntax, but we didn't expect a type here.");

            var indexType = Context.Types.LayeTypeInt.Qualified(Location.Nowhere);
            indices = index.Indices.Select(expr => AnalyseExpr(expr, indexType)).ToArray();

            var lengthExprs = new SemaExprEvaluatedConstant[indices.Length];
            for (int i = 0; i < lengthExprs.Length; i++)
            {
                var expr = indices[i];
                if (!TryEvaluate(expr, out var lengthConst))
                {
                    Context.Diag.Error(expr.Location, "Could not evaluate this expression to a constant value.");
                    lengthExprs[i] = new SemaExprEvaluatedConstant(expr, new EvaluatedConstant(0));
                    continue;
                }

                if (lengthConst.Kind != EvaluatedConstantKind.Integer)
                {
                    Context.Diag.Error(expr.Location, "The length of an array must evaluate to an integer value.");
                    lengthExprs[i] = new SemaExprEvaluatedConstant(expr, new EvaluatedConstant(0));
                    continue;
                }

                if (lengthConst.IntegerValue < 0 || lengthConst.IntegerValue > ulong.MaxValue)
                {
                    Context.Diag.Error(expr.Location, $"The length of an array must evaluate to an integer value in the rage [0, {ulong.MaxValue}].");
                    lengthExprs[i] = new SemaExprEvaluatedConstant(expr, new EvaluatedConstant(0));
                    continue;
                }

                lengthExprs[i] = new SemaExprEvaluatedConstant(expr, lengthConst);
            }

            // TODO(local): check if dimensions are out of bounds for the underlying index type

            return new SemaTypeArray(Context, elementType, lengthExprs).Qualified(elementType.Location);
        }
        
        var operand = (SemaExpr)baseOperand;
        if (index.Indices is [SyntaxExprBinary { TokenOperator.Kind: TokenKind.DotDot or TokenKind.DotDotEqual }])
            return AnalyseIndexRanged(index, operand, which);

        var operandTypeCanon = operand.Type.CanonicalType.Type;
        indices = index.Indices.Select(expr => AnalyseExpr(expr, Context.Types.LayeTypeInt.Qualified(index.Location))).ToArray();
        
        Context.Assert(indices is not [SemaExprRange], $"Should have deferred to the range-specific implementation of {nameof(AnalyseIndex)}: {nameof(AnalyseIndexRanged)}.");
        Context.Assert(baseOperand is SemaExpr, index.Location, "Somehow we did not get an expression when the operand was not a type.");
        Context.Assert(which.HasFlag(TypeOrExpr.Expr), "Somehow we got an expr back for the operand of an index syntax, but we didn't expect an expr here.");

        if (operandTypeCanon is SemaTypeBuffer typeBuffer)
        {
            operand = LValueToRValue(operand);

            for (int i = 0; i < indices.Length; i++)
                indices[i] = ConvertOrError(indices[i], Context.Types.LayeTypeInt.Qualified(index.Location));

            if (indices.Length != 1)
                Context.Diag.Error(index.Location, $"Expected exactly one index to a buffer, but got {indices.Length}.");

            var operandElementType = typeBuffer.ElementType;
            return new SemaExprIndexBuffer(operandElementType, operand, indices[0])
            {
                ValueCategory = ValueCategory.LValue
            };
        }
        else if (operandTypeCanon is SemaTypeArray typeArray)
        {
            for (int i = 0; i < indices.Length; i++)
                indices[i] = ConvertOrError(indices[i], Context.Types.LayeTypeInt.Qualified(index.Location));

            if (indices.Length != typeArray.Arity)
                Context.Diag.Error(index.Location, $"Expected {typeArray.Arity} indices, but got {indices.Length}.");

            var operandElementType = typeArray.ElementType;
            return new SemaExprIndexArray(operandElementType, operand, indices)
            {
                ValueCategory = ValueCategory.LValue
            };
        }
        else if (operandTypeCanon is SemaTypeSlice typeSlice)
        {
            for (int i = 0; i < indices.Length; i++)
                indices[i] = ConvertOrError(indices[i], Context.Types.LayeTypeInt.Qualified(index.Location));

            if (indices.Length != 1)
                Context.Diag.Error(index.Location, $"Expected exactly one index to a slice, but got {indices.Length}.");

            var operandElementType = typeSlice.ElementType;
            return new SemaExprIndexSlice(operandElementType, operand, indices[0])
            {
                ValueCategory = ValueCategory.LValue
            };
        }
        else
        {
            if (!operand.Type.IsPoison)
                Context.Diag.Error(index.Location, $"Cannot index value of type {operand.Type.ToDebugString(Colors)}.");
            return new SemaExprIndexInvalid(operand, indices)
            {
                ValueCategory = ValueCategory.LValue
            };
        }
    }

    private BaseSemaNode AnalyseTypeof(SyntaxTypeof @typeof, TypeOrExpr which)
    {
        var operand = AnalyseTypeOrExpr(@typeof.Operand);
        if (operand is SemaTypeQual operandAsType)
        {
            if (which.HasFlag(TypeOrExpr.Type))
                return SemaTypeTypeInfo.Instance.Qualified(@typeof.Location);

            Context.Assert(which.HasFlag(TypeOrExpr.Expr), @typeof.Location, "how did we get here?");
            return new SemaExprType(operandAsType);
        }
        else
        {
            Context.Assert(operand is SemaExpr, @typeof.Location, "how did we get here?");
            var operandAsExpr = (SemaExpr)operand;

            var exprType = operandAsExpr.Type;
            if (which.HasFlag(TypeOrExpr.Type))
                return exprType;

            Context.Assert(which.HasFlag(TypeOrExpr.Expr), @typeof.Location, "how did we get here?");
            return new SemaExprType(exprType);
        }
    }
    
    private BaseSemaNode AnalyseGrouped(SyntaxGrouped grouped, SemaTypeQual? typeHint, TypeOrExpr which)
    {
        // this code doesn't care what `which` actually is, since the inner call to AnalyseTypeOrExpr will (should) handle that for us.

        var inner = AnalyseTypeOrExpr(grouped.Inner, typeHint, which);
        if (inner is SemaExpr expr)
        {
            return new SemaExprGrouped(grouped.Location, expr)
            {
                ValueCategory = expr.ValueCategory,
            };
        }

        Context.Assert(inner is SemaTypeQual, "If it wasn't an expression, it has to be at type");
        return inner;
    }

    private BaseSemaNode AnalyseSizeof(SyntaxExprSizeof @sizeof, TypeOrExpr which)
    {
        if (!which.HasFlag(TypeOrExpr.Expr))
            return ErrorExpectedType(@sizeof.Location);

        var operand = AnalyseExpr(@sizeof.Operand);
        var sizeofType = Context.Types.LayeTypeInt.Qualified(@sizeof.Location);

        if (operand is SemaExprType { TypeExpr: { } operandAsType })
            return new SemaExprSizeof(@sizeof.Location, operandAsType, operandAsType.Size, sizeofType);
        else return new SemaExprSizeof(@sizeof.Location, operand, operand.Type.Size, sizeofType);
    }

    private BaseSemaNode AnalyseCountof(SyntaxExprCountof @countof, TypeOrExpr which)
    {
        if (!which.HasFlag(TypeOrExpr.Expr))
            return ErrorExpectedType(@countof.Location);

        var typeInt = Context.Types.LayeTypeInt.Qualified(@countof.Location);
        
        SemaTypeQual? typeHint = null;
        // NOTE(local): Hinting a binary `..` expression with the `int` type allows constant ranges to be well-typed.
        if (countof.Operand is SyntaxExprBinary { TokenOperator: { Kind: TokenKind.DotDot or TokenKind.DotDotEqual } })
            typeHint = typeInt.Qualified(countof.Location);

        var operand = AnalyseTypeOrExpr(@countof.Operand, typeHint);
        operand = EvaluateIfPossible(operand);

        if (operand is SemaTypeQual { Type: { } operandType } operandTypeQual)
        {
            switch (operandType.CanonicalType)
            {
                case SemaTypeArray:
                    break;

                default:
                {
                    Context.Diag.Error(countof.Operand.Location, $"Cannot get the count of type {operandType.ToDebugString(Colors)}{Colors.Default}.");
                } break;
            }
        }
        else
        {
            var operandExpr = (SemaExpr)operand;
            switch (operandExpr.Type.CanonicalType.Type)
            {
                case SemaTypeArray:
                case SemaTypeSlice:
                case SemaTypeRange:
                    break;

                default:
                {
                    Context.Diag.Error(operandExpr.Location, $"Cannot get the count of an expression of type {operandExpr.Type.ToDebugString(Colors)}{Colors.Default}.");
                } break;
            }
        }

        return new SemaExprCountof(@countof.Location, operand, typeInt);
    }

    private BaseSemaNode AnalyseLiteralBool(SyntaxToken literalBool, TypeOrExpr which, SemaTypeQual? typeHint)
    {
        if (!which.HasFlag(TypeOrExpr.Expr))
            return ErrorExpectedType(literalBool.Location);

        if (typeHint is not null && !typeHint.IsBool)
        {
            Context.Diag.Error(literalBool.Location, $"Literal bool is not convertible to {typeHint.ToDebugString(Colors)}.");
            typeHint = SemaTypePoison.Instance.Qualified(literalBool.Location);
        }

        var literalExpr = new SemaExprLiteralBool(literalBool.Location, literalBool.Kind == TokenKind.True, SemaTypeLiteralBool.Instance.Qualified(literalBool.Location));
        if (typeHint is null)
            typeHint = Context.Types.LayeTypeBool.Qualified(literalBool.Location);
        else if (typeHint.IsPoison)
            return literalExpr;

        return literalExpr;
    }

    private BaseSemaNode AnalyseLiteralInteger(SyntaxToken literalInteger, TypeOrExpr which, SemaTypeQual? typeHint)
    {
        if (!which.HasFlag(TypeOrExpr.Expr))
            return ErrorExpectedType(literalInteger.Location);

        if (typeHint is not null && !typeHint.IsNumeric)
        {
            Context.Diag.Error(literalInteger.Location, $"Literal integer is not convertible to {typeHint.ToDebugString(Colors)}.");
            typeHint = SemaTypePoison.Instance.Qualified(literalInteger.Location);
        }

        var literalExpr = new SemaExprLiteralInteger(literalInteger.Location, literalInteger.IntegerValue, SemaTypeLiteralInteger.Instance.Qualified(literalInteger.Location));
        if (typeHint is null || typeHint.IsPoison)
            return literalExpr;

        return literalExpr;
    }

    private BaseSemaNode AnalyseLiteralString(SyntaxToken literalString, TypeOrExpr which, SemaTypeQual? typeHint)
    {
        if (!which.HasFlag(TypeOrExpr.Expr))
            return ErrorExpectedType(literalString.Location);

        if (typeHint is not null)
        {
            bool isValidStringLiteralType = typeHint.TypeEquals(Context.Types.LayeTypeI8Buffer.Qualified(default), TypeComparison.WithQualifierConversions)
                || typeHint.TypeEquals(Context.Types.LayeTypeI8Slice.Qualified(default), TypeComparison.WithQualifierConversions);

            if (!isValidStringLiteralType)
            {
                Context.Diag.Error(literalString.Location, $"Literal string is not convertible to {typeHint.ToDebugString(Colors)}.");
                typeHint = SemaTypePoison.Instance.Qualified(literalString.Location);
            }
        }

        var literalExpr = new SemaExprLiteralString(literalString.Location, literalString.TextValue, SemaTypeLiteralString.Instance.Qualified(literalString.Location));
        if (typeHint is null || typeHint.IsPoison)
            return literalExpr;

        return literalExpr;
    }

    private BaseSemaNode AnalyseLiteralNil(SyntaxToken literalNil, TypeOrExpr which, SemaTypeQual? typeHint)
    {
        if (!which.HasFlag(TypeOrExpr.Expr))
            return ErrorExpectedType(literalNil.Location);

        return new SemaExprLiteralNil(literalNil.Location);
    }

    /// <summary>
    /// Base type for describing the result of a lookup operation, noting the final relevant name to the process.
    /// </summary>
    /// <param name="Name">The name that resulted in this lookup result.</param>
    private abstract record class LookupResult(Location Location, string Name);
    /// <summary>
    /// Result describing that the lookup successfully resolved to a single declaration.
    /// </summary>
    /// <param name="Name">The name that resulted in this lookup result.</param>
    /// <param name="Decl">The resolved declaration.</param>
    private sealed record class LookupSuccess(Location Location, string Name, SemaDeclNamed Decl) : LookupResult(Location, Name);
    /// <summary>
    /// Result describing that the lookup did not resolve to any declarations.
    /// </summary>
    /// <param name="Name">The name that resulted in this lookup result.</param>
    private sealed record class LookupNotFound(Location Location, string Name) : LookupResult(Location, Name);
    /// <summary>
    /// Result describing that the lookup detected a valid set of overloadable declarations.
    /// The caller which receives this result should determine if it's able to perform overload resolution, else treat this as ambiguous.
    /// </summary>
    /// <param name="Name">The name that resulted in this lookup result.</param>
    /// <param name="Decls">The resolved, overloadable declarations.</param>
    private sealed record class LookupOverloads(Location Location, string Name, SemaDeclNamed[] Decls) : LookupResult(Location, Name);
    /// <summary>
    /// Result describing that the lookup detected multiple possible declarations which could not be provided as a valid overload list.
    /// </summary>
    /// <param name="Name">The name that resulted in this lookup result.</param>
    /// <param name="Decls">The resolved, ambiguous declarations.</param>
    private sealed record class LookupAmbiguous(Location Location, string Name, SemaDeclNamed[] Decls) : LookupResult(Location, Name);
    /// <summary>
    /// Result describing a lookup which was stopped early, as part of the path was not a scope that could continue being searched.
    /// </summary>
    /// <param name="Name">The name that resulted in this lookup result.</param>
    /// <param name="Decls">The, potentially many. declarations resolved which did not have scopes to continue searching.</param>
    private sealed record class LookupNonScopeInPath(Location Location, string Name, SemaDeclNamed[] Decls) : LookupResult(Location, Name);

    private LookupResult LookUpUnqualifiedName(Location location, string name, Scope scope, bool thisScopeOnly)
    {
        if (name.IsNullOrEmpty()) return new LookupNotFound(location, name);

        var overloads = new List<SemaDeclNamed>();

        Scope? scopeCheck = scope;
        while (scopeCheck is not null)
        {
            var decls = scopeCheck.LookUp(name);
            if (decls.Count == 0)
            {
                if (thisScopeOnly) break;
                scopeCheck = scopeCheck.Parent;
                continue;
            }

            if (decls.Count == 1 && decls[0] is not SemaDeclFunction)
                return new LookupSuccess(location, name, decls[0]);

            Context.Assert(decls.All(d => d is SemaDeclFunction),
                "At this point in unqualified name lookup, all declarations should be functions.");
            overloads.AddRange(decls);

            scopeCheck = scopeCheck.Parent;
        }

        if (overloads.Count == 0)
            return new LookupNotFound(location, name);

        if (overloads.Count == 1)
            return new LookupSuccess(location, name, overloads[0]);

        return new LookupOverloads(location, name, [.. overloads]);
    }

    private LookupResult LookUpQualifiedName(string[] names, Location[] locations, Scope scope)
    {
        Context.Assert(names.Length > 1, "Should not be unqualified lookup.");
        Context.Assert(names.Length == locations.Length, "Number of names should match number of locations exactly.");

        string firstName = names[0];
        var firstLocation = locations[0];

        var res = LookUpUnqualifiedName(firstLocation, firstName, scope, false);
        switch (res)
        {
            // can't do scope resolution on an ambiguous name.
            case LookupAmbiguous: return res;
            // unqualified lookup should never complain about this.
            case LookupNonScopeInPath:
            {
                Context.Unreachable("Non-scope error in unqualified lookup.");
                throw new UnreachableException();
            }

            case LookupOverloads overloads:
                return new LookupNonScopeInPath(firstLocation, firstName, overloads.Decls);

            case LookupSuccess success:
            {
                var nextScope = GetScopeFromDecl(success.Decl);
                if (nextScope is null)
                    return new LookupNonScopeInPath(firstLocation, firstName, [ success.Decl ]);
                scope = nextScope;
            } break;

            // no name found, look through module imports.
            case LookupNotFound:
            {
                if (!_currentFileImports.TryGetValue(firstName, out var importedModuleScope))
                    return res;

                scope = importedModuleScope;
            } break;
        }

        for (int i = 1; i < names.Length - 1; i++)
        {
            string middleName = names[i];
            var middleLocation = locations[i];

            var decls = scope.LookUp(middleName);
            if (decls.Count == 0)
                return new LookupNotFound(middleLocation, middleName);

            if (decls.Count != 1)
                return new LookupAmbiguous(middleLocation, middleName, [.. decls]);

            var declScope = GetScopeFromDecl(decls[0]);
            if (declScope is null)
                return new LookupNonScopeInPath(middleLocation, firstName, [.. decls]);

            scope = declScope;
        }

        return LookUpUnqualifiedName(locations[^1], names[^1], scope, true);
    }

    private Scope? GetScopeFromDecl(SemaDeclNamed decl)
    {
        switch (decl)
        {
            default: return null;
            case SemaDeclStruct declStruct: return declStruct.Scope;
            case SemaDeclEnum declEnum: return declEnum.Scope;
        }
    }

    private LookupResult LookupName(SyntaxNode nameNode, Scope scope)
    {
        switch (nameNode)
        {
            default:
            {
                Context.Unreachable(nameNode.Location, $"Unsupported name syntax node: {nameNode.GetType().FullName}.");
                throw new UnreachableException();
            }

            case SyntaxToken { Kind: TokenKind.Identifier } nameIdent:
                return LookUpUnqualifiedName(nameIdent.Location, nameIdent.TextValue, scope, false);

            case SyntaxNameref { NamerefKind: NamerefKind.Default, Names: [SyntaxToken { Kind: TokenKind.Identifier } nameIdent] }:
                return LookUpUnqualifiedName(nameIdent.Location, nameIdent.TextValue, scope, false);

            case SyntaxNameref { NamerefKind: NamerefKind.Default } defaultNameref when defaultNameref.Names.All(nameNode => nameNode is SyntaxToken { Kind: TokenKind.Identifier }):
                return LookUpQualifiedName(defaultNameref.Names.Select(n => ((SyntaxToken)n).TextValue).ToArray(), defaultNameref.Names.Select(n => n.Location).ToArray(), scope);

            case SyntaxNameref:
            {
                Context.Unreachable(nameNode.Location, $"Unsupported nameref syntax in name resolution.");
                throw new UnreachableException();
            }
        }
    }

    private SemaExpr AnalyseRef(SyntaxExprRef @ref)
    {
        var operand = AnalyseExpr(@ref.Operand);
        Context.Diag.Error(@ref.Location, "'ref' expressions are only allowed when passing an l-value as an argument to a function.");
        return operand;
    }

    private SemaExpr AnalyseUnaryPrefix(SyntaxExprUnaryPrefix unary, SemaTypeQual? typeHint = null)
    {
        var operand = AnalyseExpr(unary.Operand);
        switch (unary.TokenOperator.Kind)
        {
            default:
            {
                operand = LValueToRValue(operand);
                return UndefinedOperator();
            }

            case TokenKind.Minus:
            {
                operand = LValueToRValue(operand);
                if (!operand.Type.CanonicalType.IsNumeric)
                    return UndefinedOperator();

                if (typeHint is not null)
                    operand = ConvertOrError(operand, typeHint);

                return new SemaExprNegate(operand);
            }

            case TokenKind.Plus:
            {
                operand = LValueToRValue(operand);
                if (!operand.Type.CanonicalType.IsNumeric)
                    return UndefinedOperator();

                if (typeHint is not null)
                    operand = ConvertOrError(operand, typeHint);

                return operand;
            }

            case TokenKind.Tilde:
            {
                operand = LValueToRValue(operand);
                if (!operand.Type.CanonicalType.IsInteger)
                    return UndefinedOperator();

                if (typeHint is not null)
                    operand = ConvertOrError(operand, typeHint);

                return new SemaExprComplement(operand);
            }

            case TokenKind.Ampersand:
            {
                if (!operand.IsLValue)
                {
                    Context.Diag.Error(unary.TokenOperator.Location, $"Cannot take the address of {operand.ValueCategory.ToHumanString(includeArticle: true)}.");
                    return UndefinedOperator(false);
                }

                return new SemaExprCast(operand.Location, CastKind.LValueToReference,
                    Context.Types.LayeTypePointer(operand.Type).Qualified(unary.Location),
                    operand);
            }

            case TokenKind.Star:
            {
                operand = LValueToRValue(operand);
                if (operand.Type.Type is SemaTypePointer typePtr)
                {
                    return new SemaExprCast(unary.TokenOperator.Location, CastKind.PointerToLValue, typePtr.ElementType, operand)
                    {
                        ValueCategory = ValueCategory.LValue,
                        IsCompilerGenerated = true
                    };
                }
                else
                {
                    Context.Diag.Error(unary.TokenOperator.Location, $"Cannot dereference a value of type {operand.Type.ToDebugString(Colors)}.");
                    return UndefinedOperator(false);
                }
            }

            case TokenKind.Not:
            {
                operand = LValueToRValue(operand);
                if (operand.Type.IsBool)
                {
                    if (typeHint is not null)
                        operand = ConvertOrError(operand, typeHint);

                    return new SemaExprLogicalNot(operand);
                }

                return UndefinedOperator();
            }
        }

        SemaExprUnary UndefinedOperator(bool reportError = true)
        {
            if (reportError)
                Context.Diag.Error(unary.Location, $"Unary operator '{unary.TokenOperator.Location.Span(Context)}' is not defined for operand {operand.Type.ToDebugString(Colors)}.");
            return new SemaExprUnaryUndefined(unary.TokenOperator, operand);
        }
    }

    private SemaExprUnary AnalyseUnaryPostfix(SyntaxExprUnaryPostfix unary, SemaTypeQual? typeHint = null)
    {
        var operand = AnalyseExpr(unary.Operand);
        var operandType = operand.Type.CanonicalType.Type;

        if (operandType.IsPoison)
            return new SemaExprUnaryUndefined(unary.TokenOperator, operand);

        switch (unary.TokenOperator.Kind)
        {
            case TokenKind.PlusPlus:
            case TokenKind.MinusMinus:
            {
                string operatorImage = unary.TokenOperator.Kind == TokenKind.PlusPlus ? "++" : "--";
                Context.Diag.Error(unary.TokenOperator.Location, $"The postfix unary operator '{operatorImage}' is not an expression.");
                Context.Diag.Note("In Laye, the postfix increment and decrement operators are statements.");
                return new SemaExprUnaryUndefined(unary.TokenOperator, operand);
            }
        }

        return UndefinedOperator();

        SemaExprUnary UndefinedOperator()
        {
            Context.Diag.Error(unary.Location, $"Unary operator '{unary.TokenOperator.Location.Span(Context)}' is not defined for operand {operand.Type.ToDebugString(Colors)}.");
            return new SemaExprUnaryUndefined(unary.TokenOperator, operand);
        }
    }

    private static readonly Dictionary<BinaryOperatorKind, (TokenKind TokenKind, BinaryOperatorKind OperatorKind)[]> _builtinSymmetricBinaryOperators = new()
    {
        { BinaryOperatorKind.Integer, [
            (TokenKind.Plus, BinaryOperatorKind.Add),
            (TokenKind.Minus, BinaryOperatorKind.Sub),
            (TokenKind.Star, BinaryOperatorKind.Mul),
            (TokenKind.Slash, BinaryOperatorKind.Div),
            (TokenKind.Percent, BinaryOperatorKind.Rem),

            (TokenKind.EqualEqual, BinaryOperatorKind.Eq),
            (TokenKind.BangEqual, BinaryOperatorKind.Neq),
            (TokenKind.Less, BinaryOperatorKind.Lt),
            (TokenKind.LessEqual, BinaryOperatorKind.Le),
            (TokenKind.Greater, BinaryOperatorKind.Gt),
            (TokenKind.GreaterEqual, BinaryOperatorKind.Ge),

            (TokenKind.LessLess, BinaryOperatorKind.Shl),
            (TokenKind.GreaterGreater, BinaryOperatorKind.Shr),
            (TokenKind.Ampersand, BinaryOperatorKind.And),
            (TokenKind.Pipe, BinaryOperatorKind.Or),
            (TokenKind.Tilde, BinaryOperatorKind.Xor),
        ] },

        { BinaryOperatorKind.Pointer, [
            (TokenKind.EqualEqual, BinaryOperatorKind.Eq),
            (TokenKind.BangEqual, BinaryOperatorKind.Neq),
        ] },

        { BinaryOperatorKind.Buffer, [
            (TokenKind.Minus, BinaryOperatorKind.Sub),

            (TokenKind.EqualEqual, BinaryOperatorKind.Eq),
            (TokenKind.BangEqual, BinaryOperatorKind.Neq),
            (TokenKind.Less, BinaryOperatorKind.Lt),
            (TokenKind.LessEqual, BinaryOperatorKind.Le),
            (TokenKind.Greater, BinaryOperatorKind.Gt),
            (TokenKind.GreaterEqual, BinaryOperatorKind.Ge),
        ] },

        { BinaryOperatorKind.Bool, [
            (TokenKind.EqualEqual, BinaryOperatorKind.Eq),
            (TokenKind.BangEqual, BinaryOperatorKind.Neq),
            (TokenKind.And, BinaryOperatorKind.LogAnd),
            (TokenKind.Or, BinaryOperatorKind.LogOr),
            (TokenKind.Xor, BinaryOperatorKind.Neq),

            (TokenKind.Ampersand, BinaryOperatorKind.And),
            (TokenKind.Pipe, BinaryOperatorKind.Or),
            (TokenKind.Tilde, BinaryOperatorKind.Neq),
        ] }
    };

    private SemaExpr AnalyseBinary(SyntaxExprBinary binary, SemaTypeQual? typeHint = null)
    {
        SemaExpr left, right;

        if (binary.TokenOperator.Kind is TokenKind.DotDot or TokenKind.DotDotEqual)
        {
            if (binary.Left is SyntaxExprEmpty)
            {
                Context.Diag.Error(binary.Left.Location, "There is not enough context to deduce the begin value of this range.");
                left = new SemaExprLiteralInteger(binary.Left.Location, 0, SemaTypePoison.Instance.Qualified(binary.Left.Location));
            }
            else left = LValueToRValue(AnalyseExpr(binary.Left));

            if (binary.Right is SyntaxExprEmpty)
            {
                Context.Diag.Error(binary.Right.Location, "There is not enough context to deduce the end value of this range.");
                right = new SemaExprLiteralInteger(binary.Right.Location, 0, SemaTypePoison.Instance.Qualified(binary.Right.Location));
            }
            else right = LValueToRValue(AnalyseExpr(binary.Right));
        }
        else
        {
            left = LValueToRValue(AnalyseExpr(binary.Left));
            right = LValueToRValue(AnalyseExpr(binary.Right));
        }

        if (left.Type.IsPoison || right.Type.IsPoison)
            return new SemaExprBinaryBuiltIn(BinaryOperatorKind.Undefined, binary.TokenOperator, SemaTypePoison.Instance.Qualified(binary.Location), left, right);

        var leftType = left.Type.CanonicalType;
        var rightType = right.Type.CanonicalType;

        static BinaryOperatorKind ClassifyType(SemaTypeQual type)
        {
            if (type.IsInteger)
                return BinaryOperatorKind.Integer;
            else if (type.IsBool)
                return BinaryOperatorKind.Bool;
            else if (type.IsPointer)
                return BinaryOperatorKind.Pointer;
            else if (type.IsBuffer)
                return BinaryOperatorKind.Buffer;
            else return BinaryOperatorKind.Undefined;
        }

        var lhsClass = ClassifyType(leftType);
        var rhsClass = ClassifyType(rightType);

        // integer .. integer OR integer ..= integer
        if (binary.TokenOperator.Kind is TokenKind.DotDot or TokenKind.DotDotEqual &&
            lhsClass == BinaryOperatorKind.Integer && rhsClass == BinaryOperatorKind.Integer)
        {
            left = EvaluateIfPossible(left);
            right = EvaluateIfPossible(right);

            SemaTypeQual rangeType;
            if (!ConvertToCommonTypeOrError(ref left, ref right, typeHint is { Type: SemaTypeRange typeHintRange } ? typeHintRange.ElementType : typeHint))
                rangeType = SemaTypePoison.Instance.Qualified(binary.Location);
            else rangeType = new SemaTypeRange(left.Type).Qualified(binary.Location);

            return new SemaExprRange(binary.TokenOperator, rangeType, left, right);
        }

        // buffer + integer OR integer + buffer  pointer arithmetic
        if (binary.TokenOperator.Kind == TokenKind.Plus &&
            ((lhsClass == BinaryOperatorKind.Buffer && rhsClass == BinaryOperatorKind.Integer) || (lhsClass == BinaryOperatorKind.Integer && rhsClass == BinaryOperatorKind.Buffer)))
        {
            SemaTypeQual bufferType;
            if (lhsClass == BinaryOperatorKind.Integer)
            {
                Context.Assert(rhsClass == BinaryOperatorKind.Buffer, right.Location, "Should be a buffer");
                bufferType = rightType;

                left = EvaluateIfPossible(left);
                if (leftType.IsLiteral)
                    left = ConvertOrError(left, Context.Types.LayeTypeInt.Qualified(left.Location));
            }
            else
            {
                Context.Assert(lhsClass == BinaryOperatorKind.Buffer, left.Location, "Should be a buffer");
                bufferType = leftType;

                right = EvaluateIfPossible(right);
                if (rightType.IsLiteral)
                    right = ConvertOrError(right, Context.Types.LayeTypeInt.Qualified(right.Location));
            }

            return EvaluateIfPossible(new SemaExprBinaryBuiltIn(BinaryOperatorKind.Buffer | BinaryOperatorKind.Add, binary.TokenOperator, bufferType, left, right));
        }

        // buffer - integer  pointer arithmetic
        if (binary.TokenOperator.Kind == TokenKind.Minus && lhsClass == BinaryOperatorKind.Buffer && rhsClass == BinaryOperatorKind.Integer)
        {
            SemaTypeQual bufferType = leftType;

            right = EvaluateIfPossible(right);
            if (rightType.IsLiteral)
                right = ConvertOrError(right, Context.Types.LayeTypeInt.Qualified(right.Location));

            return EvaluateIfPossible(new SemaExprBinaryBuiltIn(BinaryOperatorKind.Buffer | BinaryOperatorKind.Sub, binary.TokenOperator, bufferType, left, right));
        }

        // any other "symmetric" built-ins handled with a lookup table
        if (lhsClass == rhsClass)
        {
            var operatorKind = BinaryOperatorKind.Undefined;
            if (leftType.IsInteger && rightType.IsInteger)
                operatorKind |= BinaryOperatorKind.Integer;
            else if (leftType.IsBool && rightType.IsBool)
                operatorKind |= BinaryOperatorKind.Bool;
            else if (leftType.IsPointer && rightType.IsPointer)
                operatorKind |= BinaryOperatorKind.Pointer;
            else if (leftType.IsBuffer && rightType.IsBuffer)
                operatorKind |= BinaryOperatorKind.Buffer;

            if (operatorKind == BinaryOperatorKind.Undefined)
                return UndefinedOperator();

            if (!_builtinSymmetricBinaryOperators.TryGetValue(operatorKind, out var availableOperators))
                return UndefinedOperator();

            var definedOperator = availableOperators
                .Where(kv => kv.TokenKind == binary.TokenOperator.Kind)
                .Select(kv => kv.OperatorKind).SingleOrDefault();
            if (definedOperator == BinaryOperatorKind.Undefined) return UndefinedOperator();

            operatorKind |= definedOperator;

            SemaTypeQual binaryType;
            if (!ConvertToCommonTypeOrError(ref left, ref right, typeHint))
                binaryType = SemaTypePoison.InstanceQualified;
            else
            {
                if (operatorKind.IsComparisonOperator())
                    binaryType = Context.Types.LayeTypeBool.Qualified(binary.Location);
                else if (operatorKind == (BinaryOperatorKind.Buffer | BinaryOperatorKind.Sub))
                    binaryType = Context.Types.LayeTypeInt.Qualified(binary.Location);
                else binaryType = left.Type;
            }

            return EvaluateIfPossible(new SemaExprBinaryBuiltIn(operatorKind, binary.TokenOperator, binaryType, left, right));
        }

        return UndefinedOperator();

        SemaExprBinary UndefinedOperator()
        {
            Context.Diag.Error(binary.Location, $"Binary operator '{binary.TokenOperator.Location.Span(Context)}' is not defined for operands {left.Type.ToDebugString(Colors)} and {right.Type.ToDebugString(Colors)}.");
            return new SemaExprBinaryBuiltIn(BinaryOperatorKind.Undefined, binary.TokenOperator, SemaTypePoison.InstanceQualified, left, right);
        }
    }

    private SemaExprCall AnalyseCall(SyntaxExprCall call, SemaTypeQual? typeHint = null)
    {
        var callee = AnalyseExpr(call.Callee);
        var callableType = callee.Type.Type;

        switch (callableType)
        {
            case SemaTypePoison typePoison:
            {
                var arguments = call.Args.Select(arg => EvaluateIfPossible(AnalyseExpr(arg))).ToArray();
                return new SemaExprCall(call.Location, SemaTypePoison.InstanceQualified, callee, arguments);
            }

            case SemaTypeFunction typeFunction:
            {
                int paramCount = typeFunction.ParamTypes.Count;
                if (typeFunction.VarargsKind == VarargsKind.Laye)
                    paramCount--; // don't include the last parameter in regular arg/param checks

                int argumentCount = call.Args.Count;

                if (argumentCount < paramCount)
                {
                    Context.Diag.Error(call.Location, $"Not enough arguments to function call. Expected {paramCount}, got {argumentCount}.");
                }

                if (argumentCount > paramCount)
                {
                    if (typeFunction.VarargsKind == VarargsKind.None)
                        Context.Diag.Error(call.Location, $"Too many arguments to function call. Expected {paramCount}, got {argumentCount}.");
                }

                SemaTypeQual? layeVarargsElementType = null;
                if (typeFunction.VarargsKind == VarargsKind.Laye)
                {
                    Context.Assert(typeFunction.ParamTypes.Count >= 1, "Can't be a Laye varargs function without at least one argument.");

                    var lastParamType = typeFunction.ParamTypes[typeFunction.ParamTypes.Count - 1];
                    var lastParamTypeCanon = lastParamType.Type.CanonicalType;

                    Context.Assert(lastParamTypeCanon is SemaTypeSlice, "Laye varargs function expected to end in a slice parameter.");
                    layeVarargsElementType = ((SemaTypeSlice)lastParamTypeCanon).ElementType;
                }

                SemaExpr[] arguments = new SemaExpr[argumentCount];
                for (int i = 0; i < arguments.Length; i++)
                {
                    SemaTypeQual? argTypeHint = null;
                    if (i < typeFunction.ParamTypes.Count)
                        argTypeHint = typeFunction.ParamTypes[i];
                    else
                    {
                        if (typeFunction.VarargsKind == VarargsKind.Laye)
                        {
                            Context.Assert(layeVarargsElementType is not null, "where did it go");
                            argTypeHint = layeVarargsElementType;
                        }
                    }

                    if (call.Args[i] is SyntaxExprRef refArg)
                    {
                        var lvalueArg = AnalyseExpr(refArg.Operand);
                        if (!lvalueArg.IsLValue)
                            Context.Diag.Error(lvalueArg.Location, "Operand of a 'ref' expression must be an l-value.");

                        if (i >= typeFunction.ParamTypes.Count)
                        {
                            if (typeFunction.VarargsKind != VarargsKind.None)
                                Context.Diag.Error(lvalueArg.Location, "A 'ref' expression cannot be a variadic argument.");
                        }
                        else
                        {
                            if (!typeFunction.ParamDecls[i].IsRefParam)
                                Context.Diag.Error(lvalueArg.Location, $"Cannot pass a value by reference to a non-'ref' parameter.");
                        }

                        arguments[i] = WrapWithCast(lvalueArg, lvalueArg.Type, CastKind.LValueToReference);
                    }
                    else
                    {
                        var argument = AnalyseExpr(call.Args[i], argTypeHint);
                        if (argTypeHint is not null)
                            argument = ConvertOrError(argument, argTypeHint);
                        else
                        {
                            // do no real conversion if the function call is already invalid
                            if (typeFunction.VarargsKind == VarargsKind.None)
                                argument = LValueToRValue(argument);
                            // otherwise, convert to a valid C varargs type
                            else if (typeFunction.VarargsKind == VarargsKind.C)
                                argument = ConvertToCVarargsOrError(argument);
                            else Context.Assert(false, "Unexpected varargs kind without type hint.");
                        }

                        arguments[i] = argument;
                    }
                }

                return new SemaExprCall(call.Location, typeFunction.ReturnType, callee, arguments)
                {
                    IsDiscardable = typeFunction.IsDiscardable
                };
            }

            default:
            {
                var C = new Colors(Context.UseColor);
                Context.Diag.Error(call.Callee.Location, $"Cannot call an expression of type {callee.Type.ToDebugString(C)}");

                var arguments = call.Args.Select(e => EvaluateIfPossible(AnalyseExpr(e))).ToArray();
            } break;
        }

        Context.Todo(call.Location, "Handle other kinds of calls.");
        throw new UnreachableException();
    }

    private SemaExpr AnalyseCast(SyntaxExprCast cast, SemaTypeQual? typeHint = null)
    {
        SemaTypeQual castType;
        if (cast.IsAutoCast)
        {
            if (typeHint is null)
            {
                Context.Diag.Error(cast.Location, "Unable to determine the type of the auto-cast. Specify the desired type manually.");
                castType = Context.Types.LayeTypePoison.Qualified(cast.Location);
            }
            else castType = typeHint;
        }
        else castType = AnalyseType(cast.TargetType!);

        var expr = AnalyseExpr(cast.Expr);
        if (Convert(ref expr, castType))
            return expr;

        if (expr.Type.IsInteger && castType.IsInteger)
        {
            CastKind castKind;
            if (expr.Type.Size == castType.Size)
                castKind = CastKind.NoOp;
            else if (expr.Type.Size > castType.Size)
                castKind = CastKind.IntegralTruncate;
            // TODO(local): zero extend casting for handling unsigned types
            else castKind = CastKind.IntegralSignExtend;

            return new SemaExprCast(cast.Location, castKind, castType, expr);
        }

        Context.Diag.Error(cast.Location, $"Cannot convert from {expr.Type.ToDebugString(Colors)} to {castType.ToDebugString(Colors)}.");
        return new SemaExprCast(cast.Location, CastKind.Invalid, castType, expr);
    }

    private SemaExpr AnalyseField(SyntaxExprField field, SemaTypeQual? typeHint = null)
    {
        string fieldName = field.FieldNameText;
        var operand = AnalyseExpr(field.Operand);

        if (operand.Type.IsPoison)
            return new SemaExprFieldBadIndex(field.Location, operand, fieldName);

        if (operand.Type.CanonicalType.Type is SemaTypePointer { ElementType.CanonicalType.Type: SemaTypeStruct })
            ImplicitDereference(ref operand);

        switch (operand.Type.CanonicalType.Type)
        {
            default:
            {
                Context.Diag.Error(field.Operand.Location, $"Cannot index a value of type {operand.Type.ToDebugString(Colors)}.");
                return new SemaExprFieldBadIndex(field.Location, operand, fieldName)
                {
                    ValueCategory = ValueCategory.LValue,
                };
            }

            case SemaTypeStruct typeStruct:
            {
                if (!operand.IsLValue)
                {
                    // TODO(local): figure out a better way to say this.
                    Context.Diag.Error(field.Operand.Location, "Cannot index a non-lvalue.");
                }

                var structDecl = typeStruct.DeclStruct;
                if (!structDecl.TryLookupField(fieldName, out var declField, out _))
                {
                    Context.Diag.Error(field.Operand.Location, $"No such field '{fieldName}' on type {typeStruct.ToDebugString(Colors)}.");
                    return new SemaExprFieldBadIndex(field.Location, operand, fieldName)
                    {
                        ValueCategory = ValueCategory.LValue,
                    };
                }

                return new SemaExprFieldStructIndex(field.Location, operand, declField, declField.Offset)
                {
                    ValueCategory = ValueCategory.LValue,
                };
            }

            case SemaTypeRange typeRange:
            {
                if (!operand.IsLValue)
                {
                    // TODO(local): figure out a better way to say this.
                    Context.Diag.Error(field.Operand.Location, "Cannot index a non-lvalue.");
                }

                var rangeField = fieldName switch
                {
                    "begin" => RangeField.Begin,
                    "end" => RangeField.End,
                    _ => RangeField.Invalid,
                };

                if (rangeField == RangeField.Invalid)
                    Context.Diag.Error(field.Operand.Location, $"Value of type {typeRange} does not have a field '{fieldName}'.");

                return new SemaExprFieldRange(field.Location, operand, rangeField, fieldName, typeRange.ElementType)
                {
                    ValueCategory = ValueCategory.LValue,
                };
            }
        }
    }

    public SemaExpr AnalyseConstructor(SyntaxExprConstructor ctor, SemaTypeQual? typeHint = null)
    {
        SemaTypeQual ctorType;
        SemaConstructorInitializer[] inits = new SemaConstructorInitializer[ctor.Inits.Count];

        if (ctor.Type is SyntaxToken { Kind: TokenKind.Var })
        {
            if (typeHint is null)
            {
                Context.Diag.Error(ctor.Type.Location, "Unable to infer a type for this construct in this context.");
                ctorType = SemaTypePoison.InstanceQualified;
            }
            else ctorType = typeHint;
        }
        else ctorType = AnalyseType(ctor.Type);

        bool hasEncounteredDesignator = false;

        bool hasErroredOnExcessiveInitializer = false;
        bool hasErroredOnNonDesignatedInitializer = false;

        int initIndex = 0;

        if (ctorType.CanonicalType.Type is SemaTypeStruct ctorStruct)
        {
            if (!ctorStruct.DeclStruct.IsLeaf)
            {
                Context.Diag.Error(ctorType.Location, $"Cannot construct a value of type {ctorType.ToDebugString(Colors)}: it has child variants. Specify a variant of this type to construct.");
            }

            for (int i = 0; i < inits.Length; i++)
            {
                var init = ctor.Inits[i];
                Size initOffset = ctorStruct.Size;

                SemaExpr initExpr;
                if (init is SyntaxConstructorInitDesignated initDesignated)
                {
                    hasEncounteredDesignator = true;
                    Context.Todo(init.Location, "Designated initializers are currently not implemented.");
                }
                else
                {
                    if (hasEncounteredDesignator)
                    {
                        ReportInitializerAfterDesignated(init.Location);
                        initExpr = AnalyseExpr(init.Value);
                        initExpr = LValueToRValue(initExpr);
                        inits[i] = new SemaConstructorInitializer(init.Location, initExpr, initOffset);
                        continue;
                    }

                    if (initIndex < ctorStruct.DeclStruct.FieldDecls.Count)
                    {
                        var initField = ctorStruct.DeclStruct.FieldDecls[initIndex];
                        initOffset = initField.Offset;

                        initExpr = AnalyseExpr(init.Value, initField.FieldType);
                        initExpr = ConvertOrError(initExpr, initField.FieldType);

                        initIndex++;
                    }
                    else
                    {
                        if (!hasErroredOnExcessiveInitializer)
                        {
                            hasErroredOnExcessiveInitializer = true;
                            Context.Diag.Error(init.Location, "Initializer is beyond the bounds of the struct.");
                        }

                        initExpr = AnalyseExpr(init.Value);
                        initExpr = LValueToRValue(initExpr);
                    }

                    inits[i] = new SemaConstructorInitializer(init.Location, initExpr, initOffset);
                }
            }
        }
        else if (ctorType.CanonicalType.Type is SemaTypeArray ctorArray)
        {
            var elementType = ctorArray.ElementType;
            for (int i = 0; i < inits.Length; i++)
            {
                var init = ctor.Inits[i];
                Size initOffset = ctorArray.Size;

                SemaExpr initExpr;
                if (init is SyntaxConstructorInitDesignated initDesignated)
                {
                    hasEncounteredDesignator = true;
                    Context.Todo(init.Location, "Designated initializers are currently not implemented.");
                }
                else
                {
                    if (hasEncounteredDesignator)
                    {
                        ReportInitializerAfterDesignated(init.Location);
                        initExpr = AnalyseExpr(init.Value);
                        initExpr = LValueToRValue(initExpr);
                        inits[i] = new SemaConstructorInitializer(init.Location, initExpr, initOffset);
                        continue;
                    }

                    initExpr = AnalyseExpr(init.Value, elementType);
                    initExpr = ConvertOrError(initExpr, elementType);

                    if (initIndex < ctorArray.FlatLength)
                    {
                        initOffset = elementType.Size * initIndex;
                        initIndex++;
                    }
                    else
                    {
                        if (!hasErroredOnExcessiveInitializer)
                        {
                            hasErroredOnExcessiveInitializer = true;
                            Context.Diag.Error(init.Location, "Initializer is beyond the bounds of the array.");
                        }
                    }

                    inits[i] = new SemaConstructorInitializer(init.Location, initExpr, initOffset);
                }
            }
        }
        else
        {
            if (!ctorType.IsPoison)
                Context.Diag.Error(ctorType.Location, $"Cannot construct a value of type {ctorType.ToDebugString(Colors)} in this way.");

            // ensure we still report errors for initializers if there are any
            inits = new SemaConstructorInitializer[ctor.Inits.Count];
            for (int i = 0; i < inits.Length; i++)
            {
                var init = ctor.Inits[i];

                var initExpr = AnalyseExpr(init.Value);
                initExpr = LValueToRValue(initExpr);

                inits[i] = new SemaConstructorInitializer(init.Location, initExpr, Size.FromBytes(0));
            }
        }

        return new SemaExprConstructor(ctor.Location, ctorType, inits);

        void ReportInitializerAfterDesignated(Location location)
        {
            if (!hasErroredOnNonDesignatedInitializer)
            {
                hasErroredOnNonDesignatedInitializer = true;
                Context.Diag.Error(location, "Positional initializers must all occur before designated initializers.");

                var firstDesignatedLocation = ctor.Inits.First(init => init is SyntaxConstructorInitDesignated).Location;
                Context.Diag.Note(firstDesignatedLocation, "The first designated initializer occured here.");
            }
        }
    }

    public SemaExpr AnalyseSizeof(SyntaxExprSizeof @sizeof, SemaTypeQual? typeHint = null)
    {
        Context.Todo($"Implement {nameof(AnalyseSizeof)}");
        throw new UnreachableException();
        //var operand = AnalyseTypeOrExpr(@sizeof.Operand);
        //if (operand is SemaTypeQual { } operandType)
        //    return EvaluateIfPossible(new SemaExprSizeofType(@sizeof.Location, operandType));
        //else return EvaluateIfPossible(new SemaExprSizeofExpr(@sizeof.Location, (SemaExpr)operand));
    }

    public SemaExpr AnalyseCountof(SyntaxExprCountof @countof, SemaTypeQual? typeHint = null)
    {
        Context.Todo($"Implement {nameof(AnalyseCountof)}");
        throw new UnreachableException();
    }

    public SemaExpr AnalyseRankof(SyntaxExprRankof @rankof, SemaTypeQual? typeHint = null)
    {
        Context.Todo($"Implement {nameof(AnalyseRankof)}");
        throw new UnreachableException();
    }

    public SemaExpr AnalyseAlignof(SyntaxExprAlignof @alignof, SemaTypeQual? typeHint = null)
    {
        Context.Todo($"Implement {nameof(AnalyseAlignof)}");
        throw new UnreachableException();
    }

    public SemaExpr AnalyseOffsetof(SyntaxExprOffsetof @offsetof, SemaTypeQual? typeHint = null)
    {
        Context.Todo($"Implement {nameof(AnalyseOffsetof)}");
        throw new UnreachableException();
    }

    public SemaExpr AnalyseTypeof(SyntaxTypeof @typeof, SemaTypeQual? typeHint = null)
    {
        Context.Todo($"Implement {nameof(AnalyseTypeof)}");
        throw new UnreachableException();
    }

    // TODO(local): require type information explicitly? how do we evaluate an integer literal with no type information?
    private bool TryEvaluate(SemaExpr expr, out EvaluatedConstant value)
    {
        var evaluator = new ConstantEvaluator();
        return evaluator.TryEvaluate(expr, out value);
    }

    private SemaExpr EvaluateIfPossible(SemaExpr expr)
    {
        // already evaluated
        if (expr is SemaExprEvaluatedConstant)
            return expr;

        // evaluate and construct the node
        if (TryEvaluate(expr, out var constant))
            return new SemaExprEvaluatedConstant(expr, constant);

        // couldn't evaluate
        return expr;
    }

    private BaseSemaNode EvaluateIfPossible(BaseSemaNode node)
    {
        if (node is SemaExpr expr && TryEvaluate(expr, out var constant))
            return new SemaExprEvaluatedConstant(expr, constant);

        return node;
    }

    private const int ConvertScoreNoOp = 0;
    private const int ConvertScoreImpossible = -1;
    private const int ConvertScoreContainsErrors = -2;

    private int ConvertImpl(ref SemaExpr expr, SemaTypeQual toQual, bool performConversion)
    {
        var from = expr.Type.CanonicalType.Type;
        var to = toQual.CanonicalType.Type;

        if (from.IsPoison || to.IsPoison)
            return ConvertScoreNoOp;

        if (from.IsErrored || to.IsErrored)
            return ConvertScoreContainsErrors;

        int score = ConvertScoreNoOp;
        if (expr.IsLValue)
        {
            if (performConversion)
                expr = LValueToRValue(expr);
            score = 1;
        }

        if (from == to)
        {
            if (performConversion)
                expr = EvaluateIfPossible(expr);
            return score;
        }

        bool CheckContainerType<T>(ref SemaExpr expr, bool checkNil = false)
            where T : SemaContainerType<T>
        {
            if (from is T fromCont && to is T toCont)
            {
                if (fromCont.ElementType.TypeEquals(toCont.ElementType, TypeComparison.WithQualifierConversions))
                {
                    if (performConversion)
                        expr = ImplicitCast(expr, toQual);
                    return true;
                }
            }

            if (checkNil && from is SemaTypeNil && to is T)
            {
                Context.Assert(expr is SemaExprLiteralNil, expr.Location, "The only thing which should have the 'nil' type is the literal nil expression.");
                if (performConversion)
                    expr = new SemaExprLiteralNil(expr.Location, toQual);
                return true;
            }

            return false;
        }

        if (CheckContainerType<SemaTypePointer>(ref expr, true))
            return score;

        if (CheckContainerType<SemaTypeBuffer>(ref expr, true))
            return score;

        if (CheckContainerType<SemaTypeNilable>(ref expr, true))
            return score;

        if (CheckContainerType<SemaTypeArray>(ref expr))
            return score;

        if (CheckContainerType<SemaTypeSlice>(ref expr))
            return score;

        // TODO(local): more conversion checks

        if (TryEvaluate(expr, out var evaluatedConstant))
        {
            if (evaluatedConstant.Kind == EvaluatedConstantKind.Integer && to.IsNumeric)
            {
                if (to.IsFloat)
                {
                    Context.Assert(false, "TODO: Converting an evaluated integer constant to a float constant is not supported; floats are not currently supported at this stage.");
                    throw new UnreachableException();
                }

                long bitCount = evaluatedConstant.IntegerValue.GetBitLength();
                if (bitCount <= to.Size.Bits)
                {
                    if (performConversion)
                        expr = new SemaExprEvaluatedConstant(ImplicitCast(expr, toQual), evaluatedConstant);
                    return score;
                }
            }
            else if (evaluatedConstant.Kind == EvaluatedConstantKind.Bool)
            {
                if (to.IsBool)
                {
                    if (performConversion)
                        expr = new SemaExprEvaluatedConstant(ImplicitCast(expr, toQual), evaluatedConstant);
                    return score;
                }
            }
            else if (evaluatedConstant.Kind == EvaluatedConstantKind.String)
            {
                // check for the lack of qualifiers all the way down, must match these types exactly
                if (to == Context.Types.LayeTypeI8Slice || to == Context.Types.LayeTypeI8Buffer)
                {
                    if (performConversion)
                        expr = new SemaExprEvaluatedConstant(ImplicitCast(expr, toQual), evaluatedConstant);
                    return score;
                }
            }
        }

        if (from.IsInteger && to.IsInteger)
        {
            if (from.Size <= to.Size)
            {
                if (performConversion)
                    expr = WrapWithCast(expr, toQual, CastKind.IntegralSignExtend);
                return score;
            }
        }

        if (from.IsBool && to.IsBool)
        {
            if (performConversion)
                expr = WrapWithCast(expr, toQual, from.Size < to.Size ? CastKind.IntegralZeroExtend : CastKind.IntegralTruncate);
            return score;
        }

        return ConvertScoreImpossible;
    }

    private bool Convert(ref SemaExpr expr, SemaTypeQual to)
    {
        if (expr.IsErrored) return true;
        return ConvertImpl(ref expr, to, true) >= 0;
    }

    private SemaExpr ConvertOrError(SemaExpr expr, SemaTypeQual to)
    {
        if (expr.IsErrored) return expr;
        if (!Convert(ref expr, to))
            Context.Diag.Error(expr.Location, $"Expression of type {expr.Type.ToDebugString(Colors)} is not convertible to {to.ToDebugString(Colors)}.");

        return expr;
    }

    private SemaExpr ConvertToCVarargsOrError(SemaExpr expr)
    {
        var exprType = expr.Type.Type.CanonicalType;
        if (exprType.IsInteger && exprType.Size.Bits <= Context.Types.LayeTypeFFIInt.Size.Bits)
            return ConvertOrError(expr, Context.Types.LayeTypeFFIInt.Qualified(Location.Nowhere));
        else if (exprType.IsFloat && exprType.Size.Bits <= Context.Types.LayeTypeFFIDouble.Size.Bits)
            return ConvertOrError(expr, Context.Types.LayeTypeFFIDouble.Qualified(Location.Nowhere));
        else if (exprType.IsNumeric || exprType.IsPointer || exprType.IsBuffer)
            return LValueToRValue(expr);

        Context.Todo(expr.Location, $"ConvertToCVarargsOrError for expr of type {expr.Type.ToDebugString(Colors)}.");
        throw new UnreachableException();
    }

    private int TryConvert(ref SemaExpr expr, SemaTypeQual to)
    {
        return ConvertImpl(ref expr, to, false);
    }

    private bool ConvertToCommonType(ref SemaExpr a, ref SemaExpr b, SemaTypeQual? typeHint = null)
    {
        int a2bScore = TryConvert(ref a, b.Type);
        int b2aScore = TryConvert(ref b, a.Type);

        if (typeHint is not null)
        {
            int a2hScore = TryConvert(ref a, typeHint);
            int b2hScore = TryConvert(ref b, typeHint);

            int bestCommonScore = Math.Max(a2bScore, b2aScore);
            int worstCommonScore = Math.Min(a2bScore, b2aScore);

            if (a2hScore >= 0 && b2hScore >= 0 && ((a2hScore >= bestCommonScore && b2hScore >= bestCommonScore) || worstCommonScore < 0))
            {
                a = EvaluateIfPossible(a);
                b = EvaluateIfPossible(b);

                return Convert(ref a, typeHint) & Convert(ref b, typeHint);
            }
        }

        if (a2bScore >= 0 && (a2bScore <= b2aScore || b2aScore < 0))
        {
            b = EvaluateIfPossible(b);
            return Convert(ref a, b.Type);
        }

        a = EvaluateIfPossible(a);
        return Convert(ref b, a.Type);
    }

    private bool ConvertToCommonTypeOrError(ref SemaExpr a, ref SemaExpr b, SemaTypeQual? typeHint = null)
    {
        if (!ConvertToCommonType(ref a, ref b, typeHint))
        {
            Context.Diag.Error(a.Location, $"Can't convert expressions of type {a.Type.ToDebugString(Colors)} and {b.Type.ToDebugString(Colors)} to a common type.");
            return false;
        }

        return true;
    }

    private bool ImplicitDereference(ref SemaExpr expr)
    {
        while (expr.Type.Type is SemaTypePointer typePtr)
        {
            expr = LValueToRValue(expr);
            expr = new SemaExprCast(expr.Location, CastKind.PointerToLValue, typePtr.ElementType, expr)
            {
                ValueCategory = ValueCategory.LValue,
                IsCompilerGenerated = true,
            };
        }

        return expr.IsLValue;
    }

    private SemaExprCast WrapWithCast(SemaExpr expr, SemaTypeQual type, CastKind castKind)
    {
        return new SemaExprCast(expr.Location, castKind, type, expr)
        {
            IsCompilerGenerated = true,
        };
    }

    private SemaExprCast ImplicitCast(SemaExpr expr, SemaTypeQual type)
    {
        return WrapWithCast(expr, type, CastKind.Implicit);
    }

    private SemaExpr PointerToIntegerCast(SemaExpr expr)
    {
        if (expr.Type.Type is SemaTypePointer or SemaTypeBuffer)
            return WrapWithCast(expr, Context.Types.LayeTypeInt.Qualified(expr.Location), CastKind.BitCast);
        
        return expr;
    }

    private SemaExpr LValueToRValue(SemaExpr expr)
    {
        if (expr.IsErrored) return expr;

        if (expr.IsReference)
        {
            expr = WrapWithCast(expr, expr.Type, CastKind.ReferenceToLValue);
            expr.ValueCategory = ValueCategory.LValue;
        }

        if (expr.IsLValue)
            expr = WrapWithCast(expr, expr.Type, CastKind.LValueToRValue);

        return expr;
    }

    private IDisposable EnterScope(Scope scope)
    {
        scope.CurrentDefer = CurrentScope.CurrentDefer;
        return new ScopeDisposable(this, scope);
    }

    private IDisposable EnterScope(bool createNewScope = true)
    {
        return createNewScope ? new ScopeDisposable(this) : new ScopeDisposableNoPush(this);
    }

    private CurrentFunctionDisposable EnterFunction(SemaDeclFunction function)
    {
        return new CurrentFunctionDisposable(this, function);
    }

    private BreakContinueDisposable EnterBreakContinueScope(SemaStmt stmt, BreakContinueFlags flags)
    {
        return new BreakContinueDisposable(this, stmt, flags);
    }

    private sealed class ScopeDisposableNoPush : IDisposable
    {
        private readonly Sema _sema;
        private readonly Scope _scope;

        public ScopeDisposableNoPush(Sema sema)
        {
            _sema = sema;
            _scope = sema.CurrentScope;
        }

        public void Dispose()
        {
            if (!_sema._scopeStack.TryPeek(out var scope))
            {
                _sema.Context.Diag.ICE($"Exited a {nameof(ScopeDisposableNoPush)}, but there were no scopes");
                throw new UnreachableException();
            }

            _sema.Context.Assert(ReferenceEquals(scope, _scope), $"Exited a {nameof(ScopeDisposableNoPush)}, but the scope was not the correct scope");
        }
    }

    private sealed class ScopeDisposable : IDisposable
    {
        private readonly Sema _sema;
        private readonly Scope _scope;

        public ScopeDisposable(Sema sema, Scope? scope = null)
        {
            _sema = sema;
            _scope = scope ?? new Scope(sema.CurrentScope) { CurrentDefer = sema.CurrentScope.CurrentDefer };
            sema._scopeStack.Push(_scope);
        }

        public void Dispose()
        {
            if (!_sema._scopeStack.TryPop(out var scope))
            {
                _sema.Context.Diag.ICE($"Exited a {nameof(ScopeDisposable)}, but there were no scopes");
                throw new UnreachableException();
            }

            _sema.Context.Assert(ReferenceEquals(scope, _scope), $"Exited a {nameof(ScopeDisposable)}, but the scope was not the correct scope");
        }
    }

    private sealed class CurrentFunctionDisposable : IDisposable
    {
        private readonly Sema _sema;
        private readonly SemaDeclFunction _function;

        public CurrentFunctionDisposable(Sema sema, SemaDeclFunction function)
        {
            _sema = sema;
            _function = function;
            sema._functionStack.Push(function);
        }

        public void Dispose()
        {
            if (!_sema._functionStack.TryPop(out var function))
            {
                _sema.Context.Diag.ICE($"Exited a {nameof(CurrentFunctionDisposable)}, but there were no functions");
                throw new UnreachableException();
            }

            _sema.Context.Assert(ReferenceEquals(function, _function), $"Exited a {nameof(CurrentFunctionDisposable)}, but the function was not the correct function");
        }
    }

    private sealed class BreakContinueDisposable : IDisposable
    {
        private readonly Sema _sema;
        private readonly SemaStmt _stmt;
        private readonly BreakContinueFlags _flags;

        public BreakContinueDisposable(Sema sema, SemaStmt stmt, BreakContinueFlags flags)
        {
            _sema = sema;
            _stmt = stmt;
            sema._breakContinueStack.Add(new(stmt, flags));
        }

        public void Dispose()
        {
            if (_sema._breakContinueStack.Count == 0)
            {
                _sema.Context.Diag.ICE($"Exited a {nameof(BreakContinueDisposable)}, but there were no statement");
                throw new UnreachableException();
            }

            var target = _sema._breakContinueStack[^1];
            _sema._breakContinueStack.RemoveAt(_sema._breakContinueStack.Count - 1);

            _sema.Context.Assert(ReferenceEquals(target.Stmt, _stmt), $"Exited a {nameof(BreakContinueDisposable)}, but the function was not the correct statement");
        }
    }
}
#pragma warning restore CA1822 // Mark members as static
