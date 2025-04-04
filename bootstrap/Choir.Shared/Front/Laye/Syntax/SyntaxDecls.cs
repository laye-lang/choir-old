namespace Choir.Front.Laye.Syntax;

public sealed class SyntaxDeclModuleUnit(SourceFile sourceFile, SyntaxDeclModuleUnitHeader header, IEnumerable<SyntaxNode> topLevelDecls)
    : SyntaxNode(new Location(0, 0, sourceFile.FileId))
{
    public SourceFile SourceFile { get; } = sourceFile;

    public SyntaxDeclModuleUnitHeader Header { get; } = header;
    public IReadOnlyList<SyntaxNode> TopLevelDeclarations { get; } = [.. topLevelDecls];

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            yield return Header;
            foreach (var decl in TopLevelDeclarations)
                yield return decl;
        }
    }
}

public sealed class SyntaxDeclModuleUnitHeader(Location location, SyntaxDeclModule? declModule, IEnumerable<SyntaxDeclImport> declImports)
    : SyntaxNode(location)
{
    public SyntaxDeclModule? ModuleDeclaration { get; } = declModule;
    public string? ModuleName => ModuleDeclaration?.ModuleName;
    public IReadOnlyList<SyntaxDeclImport> ImportDeclarations { get; } = [.. declImports];
    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (ModuleDeclaration is not null)
                yield return ModuleDeclaration;
            foreach (var decl in ImportDeclarations)
                yield return decl;
        }
    }
}

public sealed class SyntaxDeclModule(SyntaxToken tokenModule, IEnumerable<SyntaxToken> tokenNames)
    : SyntaxNode(tokenNames.FirstOrDefault()?.Location ?? tokenModule.Location)
{
    public SyntaxToken TokenModule { get; } = tokenModule;
    public IReadOnlyList<SyntaxToken> TokenNames { get; } = [.. tokenNames];
    public string ModuleName { get; } = string.Join(".", tokenNames.Select(tk => tk.TextValue));
    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenModule, .. tokenNames];
}

public abstract class SyntaxImportQuery(Location location) : SyntaxNode(location)
{
}

public sealed class SyntaxImportQueryWildcard(SyntaxToken tokenStar) : SyntaxImportQuery(tokenStar.Location)
{
    public SyntaxToken TokenStar { get; } = tokenStar;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenStar];
}

public sealed class SyntaxImportQueryNamed(SyntaxToken tokenQueryName, SyntaxToken? tokenAs, SyntaxToken? tokenAlias)
    : SyntaxImportQuery(tokenQueryName.Location)
{
    public SyntaxToken TokenQueryName { get; } = tokenQueryName;
    public SyntaxToken? TokenAs { get; } = tokenAs;
    public SyntaxToken? TokenAlias { get; } = tokenAlias;

    public bool IsAliased => TokenAlias is not null;

    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            yield return TokenQueryName;
            if (TokenAs is not null)
                yield return TokenAs;
            if (TokenAlias is not null)
                yield return TokenAlias;
        }
    }
}

public sealed class SyntaxImportCFlags(SyntaxToken tokenCFlags, SyntaxToken tokenOpenBrace, IReadOnlyList<SyntaxToken> flags, SyntaxToken tokenCloseBrace)
    : SyntaxNode(tokenCFlags.Location)
{
    public SyntaxToken TokenCFlags { get; } = tokenCFlags;
    public SyntaxToken TokenOpenBrace { get; } = tokenOpenBrace;
    public IReadOnlyList<SyntaxToken> Flags { get; } = flags;
    public SyntaxToken TokenCloseBrace { get; } = tokenCloseBrace;

    public override IEnumerable<SyntaxNode> Children { get; } = [tokenCFlags, tokenOpenBrace, .. flags, tokenCloseBrace];
}

public sealed class SyntaxDeclImport(SyntaxToken tokenImport)
    : SyntaxNode(tokenImport.Location)
{
    public SyntaxToken? TokenExport { get; init; }
    public SyntaxToken TokenImport { get; } = tokenImport;
    public required IReadOnlyList<SyntaxImportQuery> Queries { get; init; }
    public SyntaxToken? TokenFrom { get; init; }
    public required SyntaxToken TokenModuleName { get; init; }
    public SyntaxToken? TokenAs { get; init; }
    public SyntaxToken? TokenAlias { get; init; }
    public required SyntaxToken? TokenSemiColon { get; init; }

    public bool IsExported => TokenExport is not null;
    public string ModuleNameText => TokenModuleName.TextValue;
    public bool IsAliased => TokenAlias is not null;
    public string AliasNameText => TokenAlias?.TextValue ?? "";

    //public LayeModule? ReferencedModule { get; set; }

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (TokenExport is not null)
                yield return TokenExport;
            yield return TokenImport;
            foreach (var query in Queries)
                yield return query;
            if (TokenFrom is not null)
                yield return TokenFrom;
            yield return TokenModuleName;
            if (TokenAs is not null)
                yield return TokenAs;
            if (TokenAlias is not null)
                yield return TokenAlias;
            if (TokenSemiColon is not null)
                yield return TokenSemiColon;
        }
    }
}

public sealed class SyntaxDeclForeignImport(SyntaxToken tokenForeign, SyntaxToken tokenImport)
    : SyntaxNode(tokenImport.Location)
{
    public SyntaxToken? TokenExport { get; init; }
    public SyntaxToken TokenForeign { get; } = tokenForeign;
    public SyntaxToken TokenImport { get; } = tokenImport;
    public required SyntaxToken TokenLibraryName { get; init; }
    public required SyntaxToken TokenLibraryPath { get; init; }
    public required SyntaxToken? TokenSemiColon { get; init; }

    public string LibraryNameText => TokenLibraryName.TextValue;
    public string LibraryPathText => TokenLibraryPath.TextValue;

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (TokenExport is not null)
                yield return TokenExport;
            yield return TokenForeign;
            yield return TokenImport;
            yield return TokenLibraryName;
            yield return TokenLibraryPath;
            if (TokenSemiColon is not null)
                yield return TokenSemiColon;
        }
    }
}

public abstract class SyntaxTemplateParam(Location location) : SyntaxNode(location)
{
    public required SyntaxNode? DefaultValue { get; init; }
}

public sealed class SyntaxTemplateParamType(SyntaxToken tokenName)
    : SyntaxTemplateParam(tokenName.Location)
{
    public SyntaxToken TokenName { get; } = tokenName;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            yield return TokenName;
            if (DefaultValue is not null)
                yield return DefaultValue;
        }
    }
}

public sealed class SyntaxTemplateParamDuckType(SyntaxToken tokenVar, SyntaxToken tokenName)
    : SyntaxTemplateParam(tokenName.Location)
{
    public SyntaxToken TokenVar { get; } = tokenVar;
    public SyntaxToken TokenName { get; } = tokenName;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            yield return TokenVar;
            yield return TokenName;
            if (DefaultValue is not null)
                yield return DefaultValue;
        }
    }
}

public sealed class SyntaxTemplateParamValue(SyntaxNode type, SyntaxToken tokenName)
    : SyntaxTemplateParam(tokenName.Location)
{
    public SyntaxNode Type { get; } = type;
    public SyntaxToken TokenName { get; } = tokenName;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            yield return Type;
            yield return TokenName;
            if (DefaultValue is not null)
                yield return DefaultValue;
        }
    }
}

public sealed class SyntaxTemplateParams(SyntaxToken tokenTemplate, IReadOnlyList<SyntaxTemplateParam> templateParams)
    : SyntaxNode(tokenTemplate.Location)
{
    public SyntaxToken TokenTemplate { get; } = tokenTemplate;
    public IReadOnlyList<SyntaxTemplateParam> TemplateParams { get; } = templateParams;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenTemplate, .. templateParams];
}

public abstract class SyntaxOperatorName(SyntaxToken tokenOperatorKeyword)
    : SyntaxNode(tokenOperatorKeyword.Location)
{
    public SyntaxToken TokenOperatorKeyword { get; } = tokenOperatorKeyword;
}

public sealed class SyntaxOperatorSimple(SyntaxToken tokenOperatorKeyword, SyntaxToken tokenOperator)
    : SyntaxOperatorName(tokenOperatorKeyword)
{
    public SyntaxToken TokenOperator { get; } = tokenOperator;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenOperatorKeyword, tokenOperator];
}

public sealed class SyntaxOperatorCast(SyntaxToken tokenOperatorKeyword, SyntaxToken tokenCast, SyntaxNode type)
    : SyntaxOperatorName(tokenOperatorKeyword)
{
    public SyntaxToken TokenCast { get; } = tokenCast;
    public SyntaxNode Type { get; } = type;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenOperatorKeyword, tokenCast, type];
}

public sealed class SyntaxOperatorNew(SyntaxToken tokenOperatorKeyword, SyntaxToken tokenNew)
    : SyntaxOperatorName(tokenOperatorKeyword)
{
    public SyntaxNode TokenNew { get; } = tokenNew;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenOperatorKeyword, tokenNew];
}

public sealed class SyntaxOperatorDelete(SyntaxToken tokenOperatorKeyword, SyntaxToken tokenDelete)
    : SyntaxOperatorName(tokenOperatorKeyword)
{
    public SyntaxNode TokenDelete { get; } = tokenDelete;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenOperatorKeyword, tokenDelete];
}

public sealed class SyntaxOperatorNewArray(SyntaxToken tokenOperatorKeyword, SyntaxToken tokenNew)
    : SyntaxOperatorName(tokenOperatorKeyword)
{
    public SyntaxNode TokenNew { get; } = tokenNew;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenOperatorKeyword, tokenNew];
}

public sealed class SyntaxOperatorDeleteArray(SyntaxToken tokenOperatorKeyword, SyntaxToken tokenDelete)
    : SyntaxOperatorName(tokenOperatorKeyword)
{
    public SyntaxNode TokenDelete { get; } = tokenDelete;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenOperatorKeyword, tokenDelete];
}

public sealed class SyntaxDeclFunction(SyntaxNode returnType, SyntaxNode name, IReadOnlyList<SyntaxDeclParam> parameters)
    : SyntaxNode(name.Location)
{
    public required SyntaxTemplateParams? TemplateParams { get; init; }
    public required SyntaxTargetCondition? TargetCondition { get; init; }
    public required IReadOnlyList<SyntaxAttrib> Attribs { get; init; }

    public SyntaxNode ReturnType { get; } = returnType;
    public SyntaxNode Name { get; } = name;
    public IReadOnlyList<SyntaxDeclParam> Params { get; } = parameters;
    public VarargsKind VarargsKind { get; set; } = VarargsKind.None;

    public SyntaxNode? Body { get; init; } = null;
    public SyntaxToken? TokenSemiColon { get; init; } = null;

    public bool IsOperatorOverload { get; } = name is SyntaxOperatorName;

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (TemplateParams is not null)
                yield return TemplateParams;
            foreach (var attrib in Attribs)
                yield return attrib;
            yield return ReturnType;
            yield return Name;
            foreach (var param in Params)
                yield return param;
            if (Body is not null)
                yield return Body;
            if (TokenSemiColon is not null)
                yield return TokenSemiColon;
        }
    }
}

public sealed class SyntaxDeclBinding(SyntaxNode bindingType, SyntaxToken tokenName, SyntaxToken? tokenAssign, SyntaxNode? initializer, SyntaxToken? tokenSemiColon)
    : SyntaxNode(tokenName.Location)
{
    public required SyntaxTemplateParams? TemplateParams { get; init; }
    public required SyntaxTargetCondition? TargetCondition { get; init; }
    public required IReadOnlyList<SyntaxAttrib> Attribs { get; init; }

    public SyntaxNode BindingType { get; } = bindingType;
    public SyntaxToken TokenName { get; } = tokenName;
    public SyntaxToken? TokenAssign { get; } = tokenAssign;
    public SyntaxNode? Initializer { get; } = initializer;
    public SyntaxToken? TokenSemiColon { get; } = tokenSemiColon;

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (TemplateParams is not null)
                yield return TemplateParams;
            foreach (var attrib in Attribs)
                yield return attrib;
            yield return BindingType;
            yield return TokenName;
            if (TokenAssign is not null)
                yield return TokenAssign;
            if (Initializer is not null)
                yield return Initializer;
            if (TokenSemiColon is not null)
                yield return TokenSemiColon;
        }
    }
}

public sealed class SyntaxDeclParam(SyntaxToken? refToken, SyntaxNode paramType, SyntaxToken tokenName)
    : SyntaxNode(tokenName.Location)
{
    public SyntaxToken? RefToken { get; } = refToken;
    public SyntaxNode ParamType { get; } = paramType;
    public SyntaxToken TokenName { get; } = tokenName;

    public bool IsRefParam { get; } = refToken is not null;

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (RefToken is not null)
                yield return RefToken;
            yield return ParamType;
            yield return TokenName;
        }
    }
}

public sealed class SyntaxDeclField(SyntaxNode fieldType, SyntaxToken tokenName)
    : SyntaxNode(tokenName.Location)
{
    public SyntaxNode FieldType { get; } = fieldType;
    public SyntaxToken TokenName { get; } = tokenName;

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            yield return FieldType;
            yield return TokenName;
        }
    }
}

public sealed class SyntaxDeclStruct(SyntaxToken tokenStructOrVariant, SyntaxToken tokenName, IReadOnlyList<SyntaxDeclField> fields, IReadOnlyList<SyntaxDeclStruct> variants)
    : SyntaxNode(tokenName.Location)
{
    public required SyntaxTemplateParams? TemplateParams { get; init; }
    public required SyntaxTargetCondition? TargetCondition { get; init; }
    public required IReadOnlyList<SyntaxAttrib> Attribs { get; init; }

    public SyntaxToken TokenStructOrVariant { get; } = tokenStructOrVariant;
    public SyntaxToken TokenName { get; } = tokenName;
    public IReadOnlyList<SyntaxDeclField> Fields { get; } = fields;
    public IReadOnlyList<SyntaxDeclStruct> Variants { get; } = variants;

    public bool IsVariant => TokenStructOrVariant.Kind == TokenKind.Variant;
    public bool IsVoidVariant => IsVariant && TokenName.Kind == TokenKind.Void;
    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (TemplateParams is not null)
                yield return TemplateParams;
            foreach (var attrib in Attribs)
                yield return attrib;
            yield return TokenStructOrVariant;
            yield return TokenName;
            foreach (var @field in Fields)
                yield return @field;
            foreach (var variant in Variants)
                yield return variant;
        }
    }
}

public sealed class SyntaxDeclEnumVariant(SyntaxToken tokenName, SyntaxNode? value)
    : SyntaxNode(tokenName.Location)
{
    public SyntaxToken TokenName { get; } = tokenName;
    public SyntaxNode? Value { get; } = value;

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children { get; } = value is not null ? [tokenName, value] : [tokenName];
}

public sealed class SyntaxDeclEnum(SyntaxToken tokenEnum, SyntaxToken tokenName, IReadOnlyList<SyntaxDeclEnumVariant> variants)
    : SyntaxNode(tokenName.Location)
{
    public required SyntaxTemplateParams? TemplateParams { get; init; }
    public required SyntaxTargetCondition? TargetCondition { get; init; }
    public required IReadOnlyList<SyntaxAttrib> Attribs { get; init; }

    public SyntaxToken TokenEnum { get; } = tokenEnum;
    public SyntaxToken TokenName { get; } = tokenName;
    public IReadOnlyList<SyntaxDeclEnumVariant> Variants { get; } = variants;

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (TemplateParams is not null)
                yield return TemplateParams;
            foreach (var attrib in Attribs)
                yield return attrib;
            yield return TokenEnum;
            yield return TokenName;
            foreach (var variant in Variants)
                yield return variant;
        }
    }
}

public sealed class SyntaxDeclAlias(SyntaxToken? tokenStrict, SyntaxToken tokenAlias, SyntaxToken tokenName, SyntaxNode type)
    : SyntaxNode(tokenName.Location)
{
    public required SyntaxTemplateParams? TemplateParams { get; init; }
    public required SyntaxTargetCondition? TargetCondition { get; init; }
    public required IReadOnlyList<SyntaxAttrib> Attribs { get; init; }

    public SyntaxToken? TokenStrict { get; } = tokenStrict;
    public SyntaxToken TokenAlias { get; } = tokenAlias;
    public SyntaxToken TokenName { get; } = tokenName;
    public SyntaxNode Type { get; } = type;

    public bool IsStrict { get; } = tokenStrict is not null;
    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children
    {
        get
        {
            if (TemplateParams is not null)
                yield return TemplateParams;
            foreach (var attrib in Attribs)
                yield return attrib;
            if (TokenStrict is not null)
                yield return TokenStrict;
            yield return TokenAlias;
            yield return TokenName;
            yield return Type;
        }
    }
}

public sealed class SyntaxDeclRegister(SyntaxToken tokenRegister, SyntaxToken tokenRegisterName, SyntaxNode registerType, SyntaxToken tokenDeclName)
    : SyntaxNode(tokenRegister.Location)
{
    public required SyntaxTargetCondition? TargetCondition { get; init; }
    public required IReadOnlyList<SyntaxAttrib> Attribs { get; init; }

    public SyntaxToken TokenRegister { get; } = tokenRegister;
    public SyntaxToken TokenRegisterName { get; } = tokenRegisterName;
    public SyntaxNode RegisterType { get; } = registerType;
    public SyntaxToken TokenDeclName { get; } = tokenDeclName;

    public override bool IsDecl { get; } = true;
    public override IEnumerable<SyntaxNode> Children { get; } = [tokenRegister, tokenRegisterName, registerType, tokenDeclName];
}
