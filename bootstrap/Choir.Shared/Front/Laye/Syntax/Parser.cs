using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Choir.CommandLine;

namespace Choir.Front.Laye.Syntax;

public partial class Parser(SourceFile sourceFile)
{
    private enum ExprParseContext
    {
        Default,
        CheckForDeclarations,
        WithinTemplate,
        ForLoopInitializer,
        Pattern,
    }

    public static SyntaxDeclModuleUnitHeader ParseModuleUnitHeader(SourceFile sourceFile)
    {
        var parser = new Parser(sourceFile);
        return parser.ParseModuleUnitHeader();
    }

    public static SyntaxDeclModuleUnit ParseModuleUnit(SourceFile sourceFile)
    {
        var parser = new Parser(sourceFile);
        var header = parser.ParseModuleUnitHeader();

        var decls = new List<SyntaxNode>();
        while (parser.ParseTopLevelSyntax() is { } topLevelNode)
            decls.Add(topLevelNode);

        return new(sourceFile, header, decls);
    }

    private bool IsDefinitelyTypeStart(TokenKind kind) => kind switch
    {
        TokenKind.Mut or // TokenKind.Var or
        TokenKind.Void or TokenKind.NoReturn or
        TokenKind.Bool or TokenKind.BoolSized or
        TokenKind.Int or TokenKind.IntSized or
        TokenKind.FloatSized or TokenKind.BuiltinFFIBool or
        TokenKind.BuiltinFFIChar or TokenKind.BuiltinFFIShort or
        TokenKind.BuiltinFFIInt or TokenKind.BuiltinFFILong or
        TokenKind.BuiltinFFILongLong or TokenKind.BuiltinFFIFloat or
        TokenKind.BuiltinFFIDouble or TokenKind.BuiltinFFILongDouble => true,
        TokenKind.Var when !PeekAt(1, TokenKind.OpenBrace) => true,
        _ => false,
    };
    
    public SourceFile SourceFile { get; } = sourceFile;
    public ChoirContext Context { get; } = sourceFile.Context;
    public Colors Colors { get; } = new Colors(sourceFile.Context?.UseColor ?? false);

    private readonly Lexer _lexer = new(sourceFile);
    private readonly List<SyntaxToken> _tokenQueue = [];

    private Location _previousLocation;

    private SyntaxToken CurrentToken => Peek(0);
    private Location CurrentLocation
    {
        get
        {
            if (IsAtEnd)
                return new Location(_previousLocation.Offset + _previousLocation.Length, 1, SourceFile.FileId);

            return CurrentToken.Location;
        }
    }

    private bool IsAtEnd => CurrentToken.Kind == TokenKind.EndOfFile;

    private SyntaxToken Peek(int ahead)
    {
        Context.Assert(ahead >= 0, $"peeking ahead in the parser should only ever look forward or at the current character. the caller requested {ahead} tokens ahead, which is illegal.");

        if (_tokenQueue.Count == 0 || _tokenQueue[^1].Kind != TokenKind.EndOfFile)
        {
            while (_tokenQueue.Count <= ahead)
            {
                var token = _lexer.ReadToken();
                _tokenQueue.Add(token);

                if (token.Kind == TokenKind.EndOfFile)
                    break;
            }
        }

        // cap at EOF, when no more tokens are read.
        int peekPosition = Math.Min(_tokenQueue.Count - 1, ahead);
        return _tokenQueue[peekPosition];
    }

    private bool At(params TokenKind[] kinds) => PeekAt(0, kinds);
    private bool At(params string[] contextualKeywordTexts) => PeekAt(0, contextualKeywordTexts);

    private bool PeekAt(int ahead, params TokenKind[] kinds)
    {
        foreach (var kind in kinds)
        {
            if (Peek(ahead).Kind == kind)
                return true;
        }

        return false;
    }

    private bool PeekAt(int ahead, params string[] contextualKeywordTexts)
    {
        foreach (string text in contextualKeywordTexts)
        {
            if (Peek(ahead).Kind == TokenKind.Identifier && Peek(ahead).TextValue == text)
                return true;
        }

        return false;
    }

    private void Advance() => Advance(out _);
    private void Advance(out SyntaxToken token)
    {
        token = CurrentToken;
        if (token.Kind == TokenKind.EndOfFile)
            return;

        _tokenQueue.RemoveAt(0);
        _previousLocation = token.Location;
    }

    private bool TryAdvance(TokenKind kind) => TryAdvance(kind, out _);
    private bool TryAdvance(TokenKind kind, [MaybeNullWhen(false)] out SyntaxToken token)
    {
        if (CurrentToken.Kind != kind)
        {
            token = null;
            return false;
        }

        token = CurrentToken;
        Advance();

        return true;
    }

    private bool TryAdvance(string keywordText, TokenKind contextualKind, [MaybeNullWhen(false)] out SyntaxToken token)
    {
        if (CurrentToken.Kind == contextualKind || (CurrentToken.Kind == TokenKind.Identifier && MemoryExtensions.SequenceEqual(CurrentToken.Location.Span(Context), keywordText)))
        {
            token = CurrentToken;
            token.Kind = contextualKind;
            Advance();
            return true;
        }

        token = null;
        return false;
    }

    public SyntaxToken Consume()
    {
        var result = CurrentToken;
        Advance();
        return result;
    }

    private bool Consume(TokenKind kind)
    {
        if (CurrentToken.Kind != kind)
            return false;

        Advance();
        return true;
    }

    private bool Consume(TokenKind kind, out SyntaxToken token)
    {
        if (CurrentToken.Kind != kind)
        {
            token = new SyntaxToken(TokenKind.Missing, new Location(CurrentLocation.Offset, 0, SourceFile.FileId));
            return false;
        }

        token = CurrentToken;
        Advance();
        
        return true;
    }

    private bool Consume(TokenKind[] kinds, out SyntaxToken token)
    {
        foreach (var kind in kinds)
        {
            if (CurrentToken.Kind == kind)
            {
                token = CurrentToken;
                Advance();
                
                return true;
            }
        }
        
        token = new SyntaxToken(TokenKind.Missing, new Location(CurrentLocation.Offset, 0, SourceFile.FileId));
        return false;
    }

    private bool Consume(string keywordText, TokenKind contextualKind)
    {
        if (CurrentToken.Kind == TokenKind.Identifier && MemoryExtensions.SequenceEqual(CurrentToken.Location.Span(Context), keywordText))
        {
            CurrentToken.Kind = contextualKind;
            Advance();
            return true;
        }

        return false;
    }

    private bool Consume(string keywordText, TokenKind contextualKind, out SyntaxToken token)
    {
        if (CurrentToken.Kind == TokenKind.Identifier && MemoryExtensions.SequenceEqual(CurrentToken.Location.Span(Context), keywordText))
        {
            token = CurrentToken;
            token.Kind = contextualKind;
            Advance();
            return true;
        }

        token = new SyntaxToken(TokenKind.Missing, new Location(CurrentLocation.Offset, 0, SourceFile.FileId));
        return false;
    }

    private SyntaxToken Expect(TokenKind kind, string expected)
    {
        if (!Consume(kind, out var token))
            Context.Diag.Error(CurrentLocation, $"Expected {expected}");
        return token;
    }

    // private void Expect(TokenKind kind, string expected)
    // {
    //     if (!Consume(kind))
    //         Context.Diag.Error(CurrentLocation, $"expected {expected}");
    // }

    private void Expect(TokenKind kind, string expected, out SyntaxToken token)
    {
        if (!Consume(kind, out token))
        {
            Context.Diag.Error(CurrentLocation, $"Expected {expected}");
        }
    }

    private SyntaxToken ExpectSemiColon() { Expect(TokenKind.SemiColon, "';'", out var token); return token; }
    private void ExpectSemiColon(out SyntaxToken token) => Expect(TokenKind.SemiColon, "';'", out token);
    private SyntaxToken ExpectIdentifier() { Expect(TokenKind.Identifier, "an identifier", out var token); return token; }
    private void ExpectIdentifier(out SyntaxToken token) => Expect(TokenKind.Identifier, "an identifier", out token);

    private void ExpectContextualKeyword(string keywordText, TokenKind contextualKind)
    {
        if (!Consume(keywordText, contextualKind))
            Context.Diag.Error(CurrentToken.Location, $"expected '{keywordText}'");
    }

    private void ExpectContextualKeyword(string keywordText, TokenKind contextualKind, out SyntaxToken token)
    {
        if (!Consume(keywordText, contextualKind, out token))
            Context.Diag.Error(CurrentToken.Location, $"expected '{keywordText}'");
    }

    private void ExpectTemplateArgumentClose(out SyntaxToken tokenGreater)
    {
        tokenGreater = CurrentToken;

        // easy
        if (TryAdvance(TokenKind.Greater)) return;

        if (!At(TokenKind.GreaterGreater, TokenKind.GreaterGreaterGreater, TokenKind.GreaterEqual, TokenKind.GreaterGreaterEqual, TokenKind.GreaterGreaterGreaterEqual))
        {
            tokenGreater = new SyntaxToken(TokenKind.Missing, new Location(CurrentLocation.Offset, 0, SourceFile.FileId));
            Context.Diag.Error(CurrentToken.Location, $"expected '>'");
            return;
        }

        tokenGreater = new SyntaxToken(TokenKind.Greater, new Location(CurrentLocation.Offset, 1, SourceFile.FileId));

        TokenKind newTokenKind;
        switch (CurrentToken.Kind)
        {
            default:
            {
                Context.Diag.ICE("Checked for tokens which start with `>`, but ended up at a token that doesn't.");
                throw new UnreachableException();
            }

            case TokenKind.GreaterEqual: newTokenKind = TokenKind.Equal; break;
            case TokenKind.GreaterGreater: newTokenKind = TokenKind.Greater; break;
            case TokenKind.GreaterGreaterEqual: newTokenKind = TokenKind.GreaterEqual; break;
            case TokenKind.GreaterGreaterGreater: newTokenKind = TokenKind.GreaterGreater; break;
            case TokenKind.GreaterGreaterGreaterEqual: newTokenKind = TokenKind.GreaterGreaterEqual; break;
        }

        var newTokenLocation = new Location(CurrentLocation.Offset + 1, CurrentLocation.Length - 1, SourceFile.FileId);
        var newToken = new SyntaxToken(newTokenKind, newTokenLocation);

        _tokenQueue[0] = newToken;
    }

    private void SyncThrough(params TokenKind[] kinds)
    {
        while (!At(kinds)) Advance();
        if (At(kinds)) Advance();
    }

    private IReadOnlyList<T> ParseDelimited<T>(Func<T> parser, TokenKind tokenDelimiter, string expected, bool allowTrailing, params TokenKind[] closers)
        where T : SyntaxNode
    {
        Debug.Assert(!allowTrailing || closers.Length > 0, "if `allowTrailing` is specified, it must also specify closers");
        if (At(closers)) return [];

        var results = new List<T>();
        do
        {
            if (At(closers))
            {
                if (!allowTrailing)
                    Context.Diag.Error(CurrentLocation, $"expected {expected}");
                break;
            }

            results.Add(parser());
        } while (TryAdvance(tokenDelimiter));
        return [.. results];
    }

    private SyntaxDeclModule ParseModuleDeclaration()
    {
        Context.Assert(At(TokenKind.Module), CurrentLocation, $"Should only call {nameof(ParseModuleDeclaration)} at a `module` token.");
        Consume(TokenKind.Module, out var tokenModule);
        var tokenName = ExpectIdentifier();
        if (TryAdvance(TokenKind.SemiColon))
            return new SyntaxDeclModule(tokenModule, [tokenName]);

        var tokenNames = new List<SyntaxToken>(4) { tokenName };
        while (TryAdvance(TokenKind.ColonColon))
        {
            if (!Consume(TokenKind.Identifier, out var identToken))
            {
                Context.Diag.Error(CurrentToken.Location, $"expected an identifier.");
                break;
            }

            tokenNames.Add(identToken);
        }

        ExpectSemiColon();
        return new SyntaxDeclModule(tokenModule, tokenNames);
    }

    public SyntaxDeclModuleUnitHeader ParseModuleUnitHeader()
    {
        var location = CurrentLocation;

        var targetCondition = ParseTargetConditionIfPresent();

        SyntaxDeclModule? declModule = null;
        if (At(TokenKind.Module))
            declModule = ParseModuleDeclaration();

        var declImports = new List<SyntaxDeclImport>();

        while (At(TokenKind.Import) || (At(TokenKind.Export) && PeekAt(1, TokenKind.Import)))
        {
            var declImport = ParseImportDeclaration();
            declImports.Add(declImport);
        }

        return new(location, targetCondition, declModule, declImports);
    }

    private SyntaxTargetCondition? ParseTargetConditionIfPresent()
    {
        SyntaxTargetCondition? targetCondition = null;
        if (TryAdvance(TokenKind.HashSquare, out var hashSquareToken))
        {
            var targetToken = ExpectIdentifier();
            if (targetToken.TextValue != "target")
                Context.Diag.Error(CurrentLocation, "Expected 'target'."); ;

            SyntaxToken targetStringToken;
            if (!Consume(TokenKind.OpenParen))
            {
                Context.Diag.Error(CurrentLocation, "Expected '('.");
                targetStringToken = new SyntaxToken(TokenKind.Missing, CurrentLocation);
            }
            else targetStringToken = Expect(TokenKind.LiteralString, "a string literal.");

            Expect(TokenKind.CloseParen, "')'");
            Expect(TokenKind.CloseBracket, "']'");

            targetCondition = new SyntaxTargetCondition(targetToken, targetStringToken);
        }

        return targetCondition;
    }

    public SyntaxNode? ParseTopLevelSyntax()
    {
        if (IsAtEnd) return null;

        if (At(TokenKind.Import) || (At(TokenKind.Export) && PeekAt(1, TokenKind.Import)))
        {
            Context.Diag.Error("Import declarations must appear at the start of the source file in the module unit header.");
            return ParseImportDeclaration();
        }

        var targetCondition = ParseTargetConditionIfPresent();

        SyntaxTemplateParams? templateParams = null;
        if (TryAdvance(TokenKind.Template, out var tokenTemplate))
            templateParams = ParseTemplateParams(tokenTemplate);
        
        var attribs = ParseDeclAttributes();

        switch (CurrentToken.Kind)
        {
            case TokenKind.Foreign when PeekAt(1, TokenKind.Import):
                return ParseForeignImportDeclaration();

            case TokenKind.Alias:
            case TokenKind.Identifier when CurrentToken.TextValue == "strict" && PeekAt(1, TokenKind.Alias):
                return ParseAliasDeclaration(templateParams, targetCondition, attribs);
                
            case TokenKind.Struct:
                return ParseStructDeclaration(templateParams, targetCondition, attribs, null);

            case TokenKind.Enum:
                return ParseEnumDeclaration(templateParams, targetCondition, attribs, null);

            case TokenKind.Register:
            {
                if (templateParams is not null)
                    Context.Diag.Error(templateParams.Location, "Register declarations cannot be tempalted.");
                return ParseRegisterDeclaration(targetCondition, attribs);
            }
                
            case TokenKind.Identifier when CurrentToken.TextValue == "static" && PeekAt(1, TokenKind.If):
                return ParseStaticIf(true);

            case TokenKind.Identifier when CurrentToken.TextValue == "static" && PeekAt(1, TokenKind.Assert):
            {
                TryAdvance("static", TokenKind.Static, out var tokenStatic);
                var tokenAssert = Consume();
                var condition = ParseExpr(ExprParseContext.Default);
                SyntaxToken? message = null;
                if (TryAdvance(TokenKind.Comma, out var tokenComma)) message = Expect(TokenKind.LiteralString, "a literal string");
                var tokenSemiColon = ExpectSemiColon();
                return new SyntaxStmtAssert(tokenStatic, tokenAssert, condition, tokenComma, message, tokenSemiColon);
            }

            default:
            {
#if false
                int position = _position;
                var declType = ParseType();
                if (position == _position)
                {
                    Context.Diag.Error(CurrentLocation, "expected a declaration");
                    var token = CurrentToken;
                    SyncThrough(TokenKind.SemiColon, TokenKind.CloseBrace);
                    return new SyntaxExprEmpty(token);
                }
#endif

                var declType = ParseType();
                return ParseBindingOrFunctionDeclStartingAtName(templateParams, targetCondition, attribs, declType);
            }
        }
    }

    private SyntaxTemplateParams ParseTemplateParams(SyntaxToken tokenTemplate)
    {
        Expect(TokenKind.Less, "'<'");
        var templateParams = ParseDelimited(ParseTemplateParam, TokenKind.Comma, "an identifier or a type", false, TokenKind.Greater, TokenKind.SemiColon);
        Expect(TokenKind.Greater, "'>'");
        return new SyntaxTemplateParams(tokenTemplate, templateParams);

        SyntaxTemplateParam ParseTemplateParam()
        {
            if (At(TokenKind.Identifier) && PeekAt(1, TokenKind.EndOfFile, TokenKind.Comma, TokenKind.Greater, TokenKind.Equal))
            {
                var tokenName = Consume();
                return new SyntaxTemplateParamType(tokenName) { DefaultValue = TryParseDefaultValue(true) };
            }
            else if (TryAdvance(TokenKind.Var, out var tokenVar))
            {
                var tokenName = ExpectIdentifier();
                return new SyntaxTemplateParamDuckType(tokenVar, tokenName) { DefaultValue = TryParseDefaultValue(true) };
            }
            else
            {
                var type = ParseType();
                var tokenName = ExpectIdentifier();
                return new SyntaxTemplateParamValue(type, tokenName) { DefaultValue = TryParseDefaultValue(false) };
            }
            
            SyntaxNode? TryParseDefaultValue(bool isType)
            {
                if (!TryAdvance(TokenKind.Equal, out var tokenEqual))
                    return null;
                
                return isType ? ParseType() : ParseExpr(ExprParseContext.WithinTemplate);
            }
        }
    }

    public IReadOnlyList<SyntaxAttrib> ParseDeclAttributes()
    {
        var attribs = new List<SyntaxAttrib>();
        while (CurrentToken.Kind is TokenKind.Foreign or TokenKind.Callconv or TokenKind.Export or TokenKind.Inline or TokenKind.Discardable)
            attribs.Add(ParseDeclAttribute());

        return [.. attribs];
    }

    private SyntaxAttrib ParseDeclAttribute()
    {
        switch (CurrentToken.Kind)
        {
            default:
            {
                Context.Diag.ICE(CurrentLocation, $"{nameof(ParseDeclAttribute)} should only be called when at an attribute token");
                throw new UnreachableException();
            }

            case TokenKind.Foreign:
            {
                var tokenForeign = Consume();
                SyntaxToken? tokenForeignLibraryName = null;
                if (TryAdvance(TokenKind.OpenParen, out var tokenOpenParen))
                {
                    ExpectIdentifier(out tokenForeignLibraryName);
                    Expect(TokenKind.CloseParen, "')'");
                }

                TryAdvance(TokenKind.LiteralString, out var tokenName);
                return new SyntaxAttribForeign(tokenForeign, tokenName)
                {
                    TokenForeignLibraryName = tokenForeignLibraryName,
                };
            }

            case TokenKind.Callconv:
            {
                var tokenCallconv = Consume();
                //Expect(TokenKind.OpenParen, "'('");
                var tokenKind = Expect(TokenKind.LiteralString, "a string literal");
                // TODO(local): maybe check if this is a valid calling convention here, and store the enum value instead?
                //Expect(TokenKind.CloseParen, "')'");
                return new SyntaxAttribCallconv(tokenCallconv, tokenKind);
            }

            case TokenKind.Export: return new SyntaxAttribExport(Consume());
            case TokenKind.Inline: return new SyntaxAttribInline(Consume());
            case TokenKind.Discardable: return new SyntaxAttribDiscardable(Consume());
        }
    }

    private IReadOnlyList<SyntaxImportQuery> ParseImportQueries() => ParseDelimited(ParseImportQuery, TokenKind.Comma, "an identifier or '*'", false, TokenKind.SemiColon, TokenKind.OpenBrace, TokenKind.CloseBrace);
    private SyntaxImportQuery ParseImportQuery()
    {
        if (TryAdvance(TokenKind.Star, out var tokenStar))
            return new SyntaxImportQueryWildcard(tokenStar);
        else
        {
            var queryTokenName = ExpectIdentifier();

            SyntaxToken? tokenAlias = null;
            if (TryAdvance("as", TokenKind.As, out SyntaxToken? tokenAs))
                ExpectIdentifier(out tokenAlias);

            return new SyntaxImportQueryNamed(queryTokenName, tokenAs, tokenAlias);
        }
    }

    public SyntaxDeclImport ParseImportDeclaration()
    {
        TryAdvance(TokenKind.Export, out var tokenExport);

        Context.Assert(CurrentToken.Kind == TokenKind.Import, CurrentLocation, $"{nameof(ParseImportDeclaration)} called when not at 'import'.");
        Advance(out var tokenImport);

        SyntaxToken? tokenAs;
        SyntaxToken? tokenAlias = null;
        SyntaxToken? tokenSemiColon = null;

        bool isQueryless =
            (At(TokenKind.Identifier) && PeekAt(1, TokenKind.SemiColon)) ||
            (At(TokenKind.Identifier) && PeekAt(1, "as") && PeekAt(2, TokenKind.Identifier) && PeekAt(3, TokenKind.SemiColon));

        SyntaxToken tokenModuleName;
        if (isQueryless)
        {
            tokenModuleName = ExpectIdentifier();
            if (TryAdvance("as", TokenKind.As, out tokenAs))
                ExpectIdentifier(out tokenAlias);

            ExpectSemiColon(out tokenSemiColon);

            return new SyntaxDeclImport(tokenImport)
            {
                TokenExport = tokenExport,
                Queries = [],
                TokenFrom = null,
                TokenModuleName = tokenModuleName,
                TokenAs = tokenAs,
                TokenAlias = tokenAlias,
                TokenSemiColon = tokenSemiColon,
            };
        }

        IReadOnlyList<SyntaxImportQuery> queries = [];
        if (!At("from"))
            queries = ParseImportQueries();
        else Context.Diag.Error(CurrentLocation, "expected an identifier");
        ExpectContextualKeyword("from", TokenKind.From, out var tokenFrom);

        tokenModuleName = ExpectIdentifier();
        if (TryAdvance("as", TokenKind.As, out tokenAs))
            ExpectIdentifier(out tokenAlias);

        ExpectSemiColon(out tokenSemiColon);

        return new SyntaxDeclImport(tokenImport)
        {
            TokenExport = tokenExport,
            Queries = queries,
            TokenFrom = tokenFrom,
            TokenModuleName = tokenModuleName,
            TokenAs = tokenAs,
            TokenAlias = tokenAlias,
            TokenSemiColon = tokenSemiColon,
        };
    }

    private SyntaxDeclForeignImport ParseForeignImportDeclaration()
    {
        if (TryAdvance(TokenKind.Export, out var tokenExport))
        {
            Context.Diag.Error(tokenExport.Location, "Foreign import declarations cannot be marked as 'export'.");
        }
        
        Context.Assert(At(TokenKind.Foreign) && PeekAt(1, TokenKind.Import), CurrentLocation, $"{nameof(ParseForeignImportDeclaration)} called when not at 'foreign import'.");
        Advance(out var tokenForeign);
        Advance(out var tokenImport);

        ExpectIdentifier(out var tokenLibraryName);
        Expect(TokenKind.LiteralString, "a string literal", out var tokenLibraryPath);

        ExpectSemiColon(out var tokenSemiColon);

        return new SyntaxDeclForeignImport(tokenForeign, tokenImport)
        {
            TokenExport = tokenExport,
            TokenLibraryName = tokenLibraryName,
            TokenLibraryPath = tokenLibraryPath,
            TokenSemiColon = tokenSemiColon,
        };
    }

    private SyntaxDeclAlias ParseAliasDeclaration(SyntaxTemplateParams? templateParams, SyntaxTargetCondition? targetCondition, IReadOnlyList<SyntaxAttrib> attribs)
    {
        Context.Assert(At(TokenKind.Alias) || At("strict") && PeekAt(1, TokenKind.Alias), CurrentLocation, $"{nameof(ParseAliasDeclaration)} called when not at 'alias' or 'strict alias'.");

        TryAdvance("strict", TokenKind.Strict, out var tokenStrict);
        var tokenAlias = Consume();
        var tokenName = ExpectIdentifier();
        Expect(TokenKind.Equal, "'='");
        var type = ParseType();
        ExpectSemiColon();

        return new SyntaxDeclAlias(tokenStrict, tokenAlias, tokenName, type)
        {
            TemplateParams = templateParams,
            TargetCondition = targetCondition,
            Attribs = attribs,
        };
    }

    private SyntaxDeclStruct ParseStructDeclaration(SyntaxTemplateParams? templateParams, SyntaxTargetCondition? targetCondition, IReadOnlyList<SyntaxAttrib> attribs, SyntaxToken? tokenError)
    {
        Context.Assert(At(TokenKind.Struct, TokenKind.Variant), CurrentLocation, $"{nameof(ParseStructDeclaration)} called when not at 'struct' or 'variant'.");

        var tokenStructOrVariant = Consume();
        var tokenName = ExpectIdentifier();
        Expect(TokenKind.OpenBrace, "'{'");

        List<SyntaxDeclField> fields = [];
        List<SyntaxDeclStruct> variants = [];

        while (!IsAtEnd && !At(TokenKind.CloseBrace))
        {
            if (At(TokenKind.Variant))
            {
                if (PeekAt(1, TokenKind.Void))
                {
                    var tokenVariant = Consume();
                    var tokenVoid = Consume();
                    ExpectSemiColon();
                    var variant = new SyntaxDeclStruct(tokenVariant, tokenVoid, [], []) { TargetCondition = null, TemplateParams = null, Attribs = [] };
                    variants.Add(variant);
                }
                else
                {
                    var variant = ParseStructDeclaration(null, null, [], null);
                    variants.Add(variant);
                }

                continue;
            }

            var fieldType = ParseType();
            var tokenFieldName = ExpectIdentifier();
            ExpectSemiColon();
            var @field = new SyntaxDeclField(fieldType, tokenFieldName);
            fields.Add(@field);
        }

        Expect(TokenKind.CloseBrace, "'}'");

        return new SyntaxDeclStruct(tokenStructOrVariant, tokenName, [.. fields], [.. variants])
        {
            TemplateParams = templateParams,
            TargetCondition = targetCondition,
            Attribs = attribs,
        };
    }

    private SyntaxDeclEnumVariant ParseEnumVariant()
    {
        var tokenName = ExpectIdentifier();
        if (TryAdvance(TokenKind.Equal, out var tokenEqual))
        {
            var value = ParseExpr(ExprParseContext.Default);
            return new SyntaxDeclEnumVariant(tokenName, value);
        }

        return new SyntaxDeclEnumVariant(tokenName, null);
    }

    private SyntaxDeclEnum ParseEnumDeclaration(SyntaxTemplateParams? templateParams, SyntaxTargetCondition? targetCondition, IReadOnlyList<SyntaxAttrib> attribs, SyntaxToken? tokenError)
    {
        Context.Assert(At(TokenKind.Enum), CurrentLocation, $"{nameof(ParseEnumDeclaration)} called when not at 'enum'.");

        var tokenEnum = Consume();
        var tokenName = ExpectIdentifier();
        Expect(TokenKind.OpenBrace, "'{'");

        var variants = ParseDelimited(
            ParseEnumVariant,
            TokenKind.Comma,
            "an identifier",
            true,
            TokenKind.CloseBrace,
            TokenKind.SemiColon
        );

        Expect(TokenKind.CloseBrace, "'}'");

        return new SyntaxDeclEnum(tokenEnum, tokenName, variants)
        {
            TemplateParams = templateParams,
            TargetCondition = targetCondition,
            Attribs = attribs,
        };
    }

    private SyntaxDeclRegister ParseRegisterDeclaration(SyntaxTargetCondition? targetCondition, IReadOnlyList<SyntaxAttrib> attribs)
    {
        Context.Assert(At(TokenKind.Register), CurrentLocation, $"{nameof(ParseRegisterDeclaration)} called when not at 'register'.");

        var tokenRegister = Consume();
        var tokenRegisterName = Expect(TokenKind.LiteralString, "a literal string");
        var registerType = ParseType();
        var tokenDeclName = ExpectIdentifier();
        ExpectSemiColon();

        return new SyntaxDeclRegister(tokenRegister, tokenRegisterName, registerType, tokenDeclName)
        {
            TargetCondition = targetCondition,
            Attribs = attribs,
        };
    }

    private SyntaxNode ParseBindingOrFunctionDeclStartingAtName(SyntaxTemplateParams? templateParams, SyntaxTargetCondition? targetCondition, IReadOnlyList<SyntaxAttrib> attribs, SyntaxNode declType)
    {
        if (At(TokenKind.Operator) || PeekAt(1, TokenKind.OpenParen))
            return ParseFunctionDeclStartingAtName(templateParams, targetCondition, attribs, declType);
            
        return ParseBindingDeclStartingAtName(templateParams, targetCondition, attribs, declType, true);
    }

    private SyntaxDeclBinding ParseBindingDeclStartingAtName(SyntaxTemplateParams? templateParams, SyntaxTargetCondition? targetCondition, IReadOnlyList<SyntaxAttrib> attribs, SyntaxNode bindingType, bool consumeSemi)
    {
        ExpectIdentifier(out var tokenName);

        SyntaxNode? initializer = null;
        if (Consume(TokenKind.Equal, out var tokenAssign))
        {
            initializer = ParseExpr(ExprParseContext.Default);
        }
        
        SyntaxToken? tokenSemiColon = null;
        if (consumeSemi) ExpectSemiColon(out tokenSemiColon);
        return new SyntaxDeclBinding(bindingType, tokenName, tokenAssign, initializer, tokenSemiColon)
        {
            TemplateParams = templateParams,
            TargetCondition = targetCondition,
            Attribs = attribs,
        };
    }

    private SyntaxDeclParam ParseFunctionParameter()
    {
        TryAdvance(TokenKind.Ref, out var refToken);
        var paramType = ParseType();
        ExpectIdentifier(out var tokenName);
        return new SyntaxDeclParam(refToken, paramType, tokenName);
    }

    private SyntaxDeclFunction ParseFunctionDeclStartingAtName(SyntaxTemplateParams? templateParams, SyntaxTargetCondition? targetCondition, IReadOnlyList<SyntaxAttrib> attribs, SyntaxNode returnType)
    {
        var name = ParseIdentifierOrOperatorName();
        Expect(TokenKind.OpenParen, "'('");

        var paramDecls = new List<SyntaxDeclParam>();

        var varargsKind = VarargsKind.None;
        Location? varargsLocation = default;

        bool hasErroredOnExcessParametersAfterVarargs = false;

        if (!At(TokenKind.CloseParen))
        {
            do
            {
                if (At(TokenKind.CloseParen, TokenKind.SemiColon, TokenKind.OpenBrace, TokenKind.CloseBrace))
                {
                    Context.Diag.Error(CurrentLocation, $"Expected a function parameter.");
                    break;
                }

                if (Consume(TokenKind.Varargs, out var tokenVarargs))
                {
                    if (varargsKind != VarargsKind.None && !hasErroredOnExcessParametersAfterVarargs)
                    {
                        hasErroredOnExcessParametersAfterVarargs = true;
                        Context.Diag.Error(tokenVarargs.Location, "No further function parameters may be provided after a varargs parameter.");
                        Context.Diag.Note(varargsLocation!.Value, "The first varargs parameter is here.");
                    }

                    varargsKind = VarargsKind.C;
                    varargsLocation ??= tokenVarargs.Location;

                    if (!At(TokenKind.CloseParen, TokenKind.SemiColon, TokenKind.OpenBrace, TokenKind.CloseBrace))
                    {
                        var varargsParamDecl = ParseFunctionParameter();
                        paramDecls.Add(varargsParamDecl);
                    }
                }
                else
                {
                    var paramDecl = ParseFunctionParameter();
                    if (varargsKind != VarargsKind.None && !hasErroredOnExcessParametersAfterVarargs)
                    {
                        hasErroredOnExcessParametersAfterVarargs = true;
                        Context.Diag.Error(paramDecl.Location, "No further function parameters may be provided after a varargs parameter.");
                        Context.Diag.Note(varargsLocation!.Value, "The varargs parameter is here.");
                    }
                    
                    paramDecls.Add(paramDecl);
                }
            } while (TryAdvance(TokenKind.Comma));
        }

        Expect(TokenKind.CloseParen, "')'");

        if (At(TokenKind.OpenBrace))
        {
            var body = ParseCompound();
            return new SyntaxDeclFunction(returnType, name, paramDecls)
            {
                TemplateParams = templateParams,
                TargetCondition = targetCondition,
                Attribs = attribs,
                Body = body,
            };
        }

        ExpectSemiColon(out var tokenSemiColon);
        return new SyntaxDeclFunction(returnType, name, paramDecls)
        {
            TemplateParams = templateParams,
            TargetCondition = targetCondition,
            Attribs = attribs,
            TokenSemiColon = tokenSemiColon,
            VarargsKind = varargsKind,
        };
    }

    private SyntaxDeclFunction ParseFunctionDeclStartingWithinParameters(SyntaxTemplateParams? templateParams, SyntaxTargetCondition? targetCondition, IReadOnlyList<SyntaxAttrib> attribs, SyntaxNode returnType, SyntaxToken tokenName, SyntaxToken? refToken, SyntaxNode firstParamType)
    {
        Consume(TokenKind.Identifier, out var tokenFirstParamName);

        var firstParam = new SyntaxDeclParam(refToken, firstParamType, tokenFirstParamName);

        SyntaxDeclParam[] paramDecls;
        if (Consume(TokenKind.Comma))
        {
            var remainingParams = ParseDelimited(ParseFunctionParameter, TokenKind.Comma, "type", false, TokenKind.CloseParen, TokenKind.SemiColon);
            paramDecls = [firstParam, .. remainingParams];
        }
        else paramDecls = [firstParam];

        Expect(TokenKind.CloseParen, "')'");

        if (At(TokenKind.OpenBrace))
        {
            var body = ParseCompound();
            return new SyntaxDeclFunction(returnType, tokenName, paramDecls)
            {
                TemplateParams = templateParams,
                TargetCondition = targetCondition,
                Attribs = attribs,
                Body = body,
            };
        }

        ExpectSemiColon(out var tokenSemiColon);
        return new SyntaxDeclFunction(returnType, tokenName, paramDecls)
        {
            TemplateParams = templateParams,
            TargetCondition = targetCondition,
            Attribs = attribs,
            TokenSemiColon = tokenSemiColon,
        };
    }

    private SyntaxNode ParseStmtContinue(SyntaxNode expr, bool consumeSemi)
    {
        SyntaxToken? tokenSemiColon = null;

        if (CurrentToken.Kind.IsAssignmentOperator())
        {
            var tokenAssign = CurrentToken;
            Advance();

            var rhs = ParseExpr(ExprParseContext.Default);
            if (consumeSemi) ExpectSemiColon(out tokenSemiColon);
            return new SyntaxStmtAssign(expr, tokenAssign, rhs, tokenSemiColon);
        }
        
        if (consumeSemi) ExpectSemiColon(out tokenSemiColon);
        return new SyntaxStmtExpr(expr, tokenSemiColon);
    }

    private SyntaxNode ParseSyntaxInStmtContext()
    {
        // if it's *definitely* a type, we *definitely* want to return a binding/function declaration
        if (IsDefinitelyTypeStart(CurrentToken.Kind))
        {
            var declType = ParseType();
            return ParseBindingOrFunctionDeclStartingAtName(null, null, [], declType);
        }

        switch (CurrentToken.Kind)
        {
            case TokenKind.SemiColon:
            case TokenKind.OpenBrace:
            case TokenKind.Assert:
            case TokenKind.Break:
            case TokenKind.Continue:
            case TokenKind.Defer:
            case TokenKind.Delete:
            case TokenKind.Discard:
            case TokenKind.Do:
            case TokenKind.For:
            case TokenKind.Goto:
            case TokenKind.Identifier when CurrentToken.TextValue == "static" && PeekAt(1, TokenKind.If):
            case TokenKind.If:
            case TokenKind.Return:
            case TokenKind.While:
            case TokenKind.Xyzzy:
            case TokenKind.Yield:
            case TokenKind.Unreachable:
                return ParseStmt();

            default:
            {
                var location = CurrentLocation;
                var syntax = ParseExpr(ExprParseContext.CheckForDeclarations);
                //if (syntax is SyntaxExprEmpty syntaxEmpty) return new SyntaxStmtExpr(syntax, syntaxEmpty.Location);
                if (location.Offset == CurrentLocation.Offset)
                {
                    Context.Diag.Error(CurrentLocation, "expected an expression");
                    var token = CurrentToken;
                    SyncThrough(TokenKind.SemiColon, TokenKind.CloseBrace);
                    return new SyntaxExprEmpty(token.Location);
                }

                if (syntax.IsDecl) return syntax;
                if (syntax.CanBeType && At(TokenKind.Identifier))
                    return ParseBindingOrFunctionDeclStartingAtName(null, null, [], syntax);

                return ParseStmtContinue(syntax, true);
            }
        }
    }

    private SyntaxNode ParseStmt()
    {
        switch (CurrentToken.Kind)
        {
            case TokenKind.SemiColon:
            {
                var tokenSemiColon = Consume();
                Context.Diag.Error(tokenSemiColon.Location, "Empty statements are not allowed.");
                Context.Diag.Note($"Did you mean to use the {Colors.LayeKeyword()}xyzzy{Colors.Default} statement?");
                return new SyntaxStmtExpr(new SyntaxExprEmpty(tokenSemiColon.Location), tokenSemiColon);
            }

            case TokenKind.OpenBrace: return ParseCompound();

            case TokenKind.Assert:
            {
                var tokenAssert = Consume();

                bool startedWithParen = TryAdvance(TokenKind.OpenParen, out var tokenOpenParen);

                var condition = ParseExpr(ExprParseContext.Default);
                if (startedWithParen)
                {
                    if (Consume(TokenKind.CloseParen))
                    {
                        startedWithParen = false; // don't check for the trailing paren
                        condition = new SyntaxGrouped(condition);
                    }
                    else if (At(TokenKind.Comma))
                    {
                        Context.Diag.Error(tokenOpenParen!.Location, $"Unexpected '(', improper {Colors.LayeKeyword()}assert{Colors.Reset} syntax.");
                        Context.Diag.Note($"Allowed {Colors.LayeKeyword()}assert{Colors.Reset} syntax:\n- {Colors.LayeKeyword()}assert{Colors.Reset} condition;\n- {Colors.LayeKeyword()}assert{Colors.Reset} condition, message;");
                        Context.Diag.Note($"The following is also accepted since '(condition)' is an expression:\n- {Colors.LayeKeyword()}assert{Colors.Reset}(condition);");
                    }
                }

                SyntaxToken? message = null;
                if (TryAdvance(TokenKind.Comma, out var tokenComma)) message = Expect(TokenKind.LiteralString, "a literal string");

                // if we started with an erroneous paren (unchecked after parsing the condition), then also check for an erroneous trailing paren.
                if (startedWithParen) Consume(TokenKind.CloseParen);

                var tokenSemiColon = ExpectSemiColon();
                return new SyntaxStmtAssert(null, tokenAssert, condition, tokenComma, message, tokenSemiColon);
            }

            case TokenKind.Break:
            {
                var tokenBreak = Consume();
                TryAdvance(TokenKind.Identifier, out var tokenLabel);
                ExpectSemiColon(out var tokenSemiColon);
                return new SyntaxStmtBreak(tokenBreak, tokenLabel, tokenSemiColon);
            }

            case TokenKind.Continue:
            {
                var tokenContinue = Consume();
                TryAdvance(TokenKind.Identifier, out var tokenLabel);
                ExpectSemiColon(out var tokenSemiColon);
                return new SyntaxStmtContinue(tokenContinue, tokenLabel, tokenSemiColon);
            }

            case TokenKind.Defer:
            {
                var tokenDefer = Consume();
                var stmt = ParseStmt();
                return new SyntaxStmtDefer(tokenDefer, stmt);
            }

            case TokenKind.Delete:
            {
                var tokenDelete = Consume();
                var expr = ParseExpr(ExprParseContext.Default);
                ExpectSemiColon();
                return new SyntaxStmtDelete(tokenDelete, expr);
            }

            case TokenKind.Discard:
            {
                var tokenDelete = Consume();
                var expr = ParseExpr(ExprParseContext.Default);
                ExpectSemiColon();
                return new SyntaxStmtDiscard(tokenDelete, expr);
            }

            case TokenKind.Do:
            {
                var tokenDo = Consume();
                var body = ParseStmt();
                Expect(TokenKind.While, "'while'", out var tokenWhile);
                Expect(TokenKind.OpenParen, "'('", out var tokenOpenParen);
                var condition = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'", out var tokenCloseParen);
                var tokenSemiColon = ExpectSemiColon();
                return new SyntaxStmtDoLoop(tokenDo, body, tokenWhile, condition);
            }

            case TokenKind.For:
            {
                // TODO(local): I think I need to take more control over how I parse the init/inc forms (and maybe extend that to statement parsing in general?)

                var tokenFor = Consume();
                Expect(TokenKind.OpenParen, "'('", out var tokenOpenParen);

                SyntaxNode? initializer = null, condition = null, increment = null;
                if (!At(TokenKind.SemiColon))
                {
                    initializer = ParseExpr(ExprParseContext.ForLoopInitializer);

                    if (initializer.IsDecl) {}
                    else if (initializer.CanBeType && At(TokenKind.Identifier))
                        initializer = ParseBindingDeclStartingAtName(null, null, [], initializer, false);
                    else if (CurrentToken.Kind.IsAssignmentOperator())
                        initializer = ParseStmtContinue(initializer, false);
                }

                ExpectSemiColon();

                if (!At(TokenKind.SemiColon))
                    condition = ParseExpr(ExprParseContext.Default);
                ExpectSemiColon();
                
                if (!At(TokenKind.CloseParen))
                {
                    if (TryAdvance(TokenKind.Discard, out var tokenIncDiscard))
                    {
                        increment = new SyntaxStmtDiscard(tokenIncDiscard, ParseExpr(ExprParseContext.Default));
                    }
                    else
                    {
                        increment = ParsePrimaryExpr(ExprParseContext.Default);
                        if (CurrentToken.Kind.IsAssignmentOperator())
                            increment = ParseStmtContinue(increment, false);
                        else increment = new SyntaxStmtExpr(ParseBinaryExpr(ExprParseContext.Default, increment), null);
                    }
                }

                Expect(TokenKind.CloseParen, "')'", out var tokenCloseParen);
                var body = ParseStmt();
                return new SyntaxStmtForLoop(tokenFor, initializer, condition, increment, body);
            }

            case TokenKind.Goto: return new SyntaxStmtGoto(Consume(), ExpectIdentifier(), ExpectSemiColon());

            case TokenKind.Identifier when CurrentToken.TextValue == "static" && PeekAt(1, TokenKind.If):
                return ParseStaticIf(false);

            case TokenKind.If: return ParseIf();

            case TokenKind.Return:
            {
                var tokenReturn = Consume();
                SyntaxNode? value = At(TokenKind.SemiColon) ? null : ParseExpr(ExprParseContext.Default);
                ExpectSemiColon(out var tokenSemiColon);
                return new SyntaxStmtReturn(tokenReturn, value, tokenSemiColon);
            }

            case TokenKind.While:
            {
                var tokenWhile = Consume();
                Expect(TokenKind.OpenParen, "'('", out var tokenOpenParen);
                var condition = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'", out var tokenCloseParen);
                var body = ParseStmt();
                if (TryAdvance(TokenKind.Else))
                {
                    var elseBody = ParseStmt();
                    return new SyntaxStmtWhileLoop(tokenWhile, condition, body, elseBody);
                }

                return new SyntaxStmtWhileLoop(tokenWhile, condition, body, null);
            }

            case TokenKind.Xyzzy: return new SyntaxStmtXyzzy(Consume(), ExpectSemiColon());
            case TokenKind.Unreachable: return new SyntaxStmtUnreachable(Consume(), ExpectSemiColon());

            case TokenKind.Yield when !PeekAt(1, TokenKind.Break, TokenKind.Return):
                return new SyntaxStmtYield(Consume(), ParseExpr(ExprParseContext.Default), ExpectSemiColon());

            default:
            {
                var expr = ParseExpr(ExprParseContext.Default);
                //var primary = ParsePrimaryExpr(parseContext);
                //return ParseBinaryExpr(parseContext, primary);
                return ParseStmtContinue(expr, true);
            }
        }
    }

    private SyntaxCompound ParseCompound()
    {
        Expect(TokenKind.OpenBrace, "'{'", out var tokenOpenBrace);

        var body = new List<SyntaxNode>();
        while (!IsAtEnd && !At(TokenKind.CloseBrace))
        {
            var stmt = ParseSyntaxInStmtContext();
            body.Add(stmt);
        }
        
        Expect(TokenKind.CloseBrace, "'}'");

        return new SyntaxCompound(tokenOpenBrace.Location, [.. body]);
    }

    private SyntaxStmtStaticIf ParseStaticIf(bool isTopLevelOnly)
    {
        Debug.Assert(At("static") && PeekAt(1, TokenKind.If));
        if (!TryAdvance("static", TokenKind.Static, out var tokenStatic))
            throw new UnreachableException();

        var primaries = new List<SyntaxIfPrimary>
        {
            ParseStaticIfPrimary()
        };

        while (TryAdvance(TokenKind.Else, out var tokenElse))
        {
            if (At(TokenKind.If))
                primaries.Add(ParseStaticIfPrimary());
            else
            {
                var elseBody = ParseStaticBody();
                return new SyntaxStmtStaticIf(tokenStatic, [.. primaries], tokenElse, elseBody);
            }
        }
        
        return new SyntaxStmtStaticIf(tokenStatic, [.. primaries], null, null);

        SyntaxIfPrimary ParseStaticIfPrimary()
        {
            Debug.Assert(At(TokenKind.If));

            var tokenIf = CurrentToken;
            Advance();

            Expect(TokenKind.OpenParen, "'('");
            var condition = ParseExpr(ExprParseContext.Default);
            Expect(TokenKind.CloseParen, "')'");

            SyntaxNode body = ParseStaticBody();
            return new SyntaxIfPrimary(tokenIf, condition, body);
        }

        SyntaxNode ParseStaticBody()
        {
            if (At(TokenKind.OpenBrace))
            {
                var tokenOpenBrace = Consume();

                var bodyNodes = new List<SyntaxNode>();
                while (!IsAtEnd && !At(TokenKind.CloseBrace))
                {
                    if (isTopLevelOnly)
                    {
                        var stmt = ParseTopLevelSyntax();
                        if (stmt is null) break;
                        bodyNodes.Add(stmt);
                    }
                    else bodyNodes.Add(ParseSyntaxInStmtContext());
                }
                
                Expect(TokenKind.CloseBrace, "'}'");
                return new SyntaxCompound(tokenOpenBrace.Location, [.. bodyNodes]);
            }
            else return isTopLevelOnly ? (ParseTopLevelSyntax() ?? Expect(TokenKind.OpenBrace, "'{' or declaration")) : ParseStmt();
        }
    }

    private SyntaxStmtIf ParseIf()
    {
        var primaries = new List<SyntaxIfPrimary>
        {
            ParseIfPrimary()
        };

        while (TryAdvance(TokenKind.Else, out var tokenElse))
        {
            if (At(TokenKind.If))
                primaries.Add(ParseIfPrimary());
            else
            {
                var elseBody = ParseStmt();
                return new SyntaxStmtIf([.. primaries], tokenElse, elseBody);
            }
        }
        
        return new SyntaxStmtIf([.. primaries], null, null);

        SyntaxIfPrimary ParseIfPrimary()
        {
            Debug.Assert(At(TokenKind.If));

            var tokenIf = CurrentToken;
            Advance();

            Expect(TokenKind.OpenParen, "'('");
            var condition = ParseExpr(ExprParseContext.Default);
            Expect(TokenKind.CloseParen, "')'");

            var body = ParseStmt();
            return new SyntaxIfPrimary(tokenIf, condition, body);
        }
    }

    private SyntaxNode ParseIdentifierOrOperatorName()
    {
        if (TryAdvance(TokenKind.Operator, out var tokenOperatorKeyword))
        {
            if (TryAdvance(TokenKind.Cast, out var tokenCast))
            {
                Expect(TokenKind.OpenParen, "'('");
                var castType = ParseType();
                Expect(TokenKind.CloseParen, "')'");
                return new SyntaxOperatorCast(tokenOperatorKeyword, tokenCast, castType);
            }
            else if (TryAdvance(TokenKind.New, out var tokenNew))
            {
                if (TryAdvance(TokenKind.OpenBracket))
                {
                    Expect(TokenKind.CloseBracket, "']'");
                    return new SyntaxOperatorNewArray(tokenOperatorKeyword, tokenNew);
                }
                
                return new SyntaxOperatorNew(tokenOperatorKeyword, tokenNew);
            }
            else if (TryAdvance(TokenKind.Delete, out var tokenDelete))
            {
                if (TryAdvance(TokenKind.OpenBracket))
                {
                    Expect(TokenKind.CloseBracket, "']'");
                    return new SyntaxOperatorDeleteArray(tokenOperatorKeyword, tokenDelete);
                }
                
                return new SyntaxOperatorDelete(tokenOperatorKeyword, tokenDelete);
            }
            else if (CurrentToken.Kind.IsOverloadableOperatorKind())                
            {
                var tokenOperator = Consume();
                return new SyntaxOperatorSimple(tokenOperatorKeyword, tokenOperator);
            }
            else
            {
                Context.Diag.Error(CurrentLocation, $"expected an overloadable operator");
                var tokenMissing = new SyntaxToken(TokenKind.Missing, CurrentLocation);
                return new SyntaxOperatorSimple(tokenOperatorKeyword, tokenMissing);
            }

            // Context.Unreachable();
        }

        return ExpectIdentifier();
    }

    private SyntaxNode[] ParseNamerefImpl(out NamerefKind kind)
    {
        kind = NamerefKind.Default;
        if (TryAdvance(TokenKind.ColonColon))
            kind = NamerefKind.Implicit;
        else if (TryAdvance(TokenKind.Global))
        {
            Expect(TokenKind.ColonColon, "'::'");
            kind = NamerefKind.Global;
        }

        var names = ParseDelimited(
            ParseIdentifierOrOperatorName,
            TokenKind.ColonColon,
            "an identifier",
            false
        );
        Debug.Assert(names.Count > 0);

        return [.. names];
    }

    private SyntaxNameref ParseNamerefNoTemplateArguments()
    {
        var names = ParseNamerefImpl(out var kind);
        return SyntaxNameref.Create(Context, names[names.Length - 1].Location, kind, names, null);
    }

    private SyntaxNode ParseNamerefWithTemplateArgumentCheck()
    {
        var names = ParseNamerefImpl(out var kind);
        var lastNameLocation = names[names.Length - 1].Location;

        SyntaxTemplateArguments? templateArguments = null;
        if (TryAdvance(TokenKind.Less, out var tokenLess))
        {
            var arg = ParseTemplateArgument();
            if (!At(TokenKind.Comma, TokenKind.Greater, TokenKind.GreaterGreater, TokenKind.GreaterGreaterGreater, TokenKind.GreaterEqual, TokenKind.GreaterGreaterEqual, TokenKind.GreaterGreaterGreaterEqual))
            {
                var nameref = SyntaxNameref.Create(Context, lastNameLocation, kind, names, null);
                var binary = new SyntaxExprBinary(nameref, arg, tokenLess);
                return ParseBinaryExpr(ExprParseContext.Default, binary, TokenKind.Less.GetBinaryOperatorPrecedence());
            }

            var args = new List<SyntaxNode>() { arg };
            if (TryAdvance(TokenKind.Comma))
            {
                do
                {
                    arg = ParseTemplateArgument();
                    args.Add(arg);
                } while (TryAdvance(TokenKind.Comma));
            }

            ExpectTemplateArgumentClose(out var tokenGreater);
            templateArguments = new([.. args]);
        }

        return SyntaxNameref.Create(Context, lastNameLocation, kind, names, templateArguments);
    }

    private SyntaxNode ParseTemplateArgument()
    {
        if (IsDefinitelyTypeStart(CurrentToken.Kind))
            return ParseType();
    
        return ParseExpr(ExprParseContext.WithinTemplate);
    }

    private SyntaxNode ParseTypeContinuation(SyntaxNode typeNode)
    {
        switch (CurrentToken.Kind)
        {
            default: return typeNode;
            
            case TokenKind.Mut: break;

            case TokenKind.OpenBracket when Peek(1).Kind == TokenKind.CloseBracket:
            {
                for (int i = 0; i < 2; i++) Advance();
                typeNode = new SyntaxTypeSlice(typeNode);
            } break;

            case TokenKind.OpenBracket when Peek(1).Kind == TokenKind.Star && Peek(2).Kind == TokenKind.CloseBracket:
            {
                for (int i = 0; i < 3; i++) Advance();
                typeNode = new SyntaxTypeBuffer(typeNode, null);
            } break;

#if false
            case TokenKind.OpenBracket when Peek(1).Kind == TokenKind.Star && Peek(2).Kind == TokenKind.Colon:
            {
                for (int i = 0; i < 3; i++) Advance();
                var terminatorExpr = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseBracket, "']'");
                typeNode = new SyntaxTypeBuffer(typeNode, terminatorExpr);
            } break;
#endif
            
            case TokenKind.OpenBracket:
            {
                var tokenOpenBracket = CurrentToken;
                Advance();

                var indices = ParseDelimited(() => ParseExpr(ExprParseContext.Default), TokenKind.Comma, "an expression", false, TokenKind.CloseBracket, TokenKind.SemiColon);

                Expect(TokenKind.CloseBracket, "']'", out var tokenCloseBracket);
                // the index node represents both expression forms of indexing as well as the array type, (hopefully) for simplicity
                typeNode = new SyntaxIndex(typeNode, tokenOpenBracket, indices, tokenCloseBracket);
            } break;

            case TokenKind.Star:
            {
                Advance();
                typeNode = new SyntaxTypePointer(typeNode);
            } break;

            case TokenKind.Question:
            {
                Advance();
                typeNode = new SyntaxTypeNilable(typeNode);
            } break;

            case TokenKind.DotDot:
            {
                Advance();
                typeNode = new SyntaxTypeRange(typeNode);
            } break;
        }

        if (TryAdvance(TokenKind.Mut, out var tokenMut))
        {
            typeNode = new SyntaxQualMut(typeNode, tokenMut);
            while (TryAdvance(TokenKind.Mut, out var leadingMut)) {
                Context.Diag.Error(leadingMut.Location, "duplicate 'mut' qualifier");
            }
        }

        return ParseTypeContinuation(typeNode);
    }

    private SyntaxNode ParseType()
    {
        var currentToken = CurrentToken;

        SyntaxToken? tokenMut = null;
        if (TryAdvance(TokenKind.Mut, out tokenMut))
        {
            while (TryAdvance(TokenKind.Mut, out var leadingMut)) {
                Context.Diag.Error(leadingMut.Location, "duplicate 'mut' qualifier");
            }
        }

        SyntaxNode typeNode;
        switch (CurrentToken.Kind)
        {
            case TokenKind.OpenParen:
            {
                Advance();
                var innerType = ParseType();
                Expect(TokenKind.CloseParen, "')'");
                return new SyntaxGrouped(innerType);
            }

            case TokenKind.Var: Advance(); typeNode = currentToken; break;
            case TokenKind.Void: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeVoid); break;
            case TokenKind.NoReturn: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeNoReturn); break;
            case TokenKind.Bool: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeBool); break;
            case TokenKind.BoolSized: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeBoolSized((int)currentToken.IntegerValue)); break;
            case TokenKind.Int: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeInt); break;
            case TokenKind.IntSized: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeIntSized((int)currentToken.IntegerValue)); break;
            case TokenKind.FloatSized: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFloatSized((int)currentToken.IntegerValue)); break;
            case TokenKind.BuiltinFFIBool: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFIBool); break;
            case TokenKind.BuiltinFFIChar: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFIChar); break;
            case TokenKind.BuiltinFFIShort: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFIShort); break;
            case TokenKind.BuiltinFFIInt: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFIInt); break;
            case TokenKind.BuiltinFFILong: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFILong); break;
            case TokenKind.BuiltinFFILongLong: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFILongLong); break;
            case TokenKind.BuiltinFFIFloat: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFIFloat); break;
            case TokenKind.BuiltinFFIDouble: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFIDouble); break;
            case TokenKind.BuiltinFFILongDouble: Advance(); typeNode = new SyntaxTypeBuiltIn(currentToken.Location, Context.Types.LayeTypeFFILongDouble); break;

            case TokenKind.Global:
            case TokenKind.ColonColon:
            case TokenKind.Identifier:
            {
                typeNode = ParseNamerefWithTemplateArgumentCheck();
            } break;

            case TokenKind.Typeof:
            {
                var tokenTypeof = Consume();
                Expect(TokenKind.OpenParen, "'('");
                var expr = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'");
                return ParsePrimaryExprContinuation(ExprParseContext.Default, new SyntaxTypeof(tokenTypeof, expr));
            }

            default:
            {
                Context.Diag.ICE(CurrentLocation, $"need to return a default syntax node when no type was parseable (at token kind {CurrentToken.Kind})");
                throw new UnreachableException();
            }
        }

        if (tokenMut is not null || TryAdvance(TokenKind.Mut, out tokenMut))
        {
            typeNode = new SyntaxQualMut(typeNode, tokenMut);
            while (TryAdvance(TokenKind.Mut, out var leadingMut)) {
                Context.Diag.Error(leadingMut.Location, "duplicate 'mut' qualifier");
            }
        }

        return ParseTypeContinuation(typeNode);
    }
    
    private SyntaxNode ParseExpr(ExprParseContext parseContext)
    {
        var primary = ParsePrimaryExpr(parseContext);
        return ParseBinaryExpr(parseContext, primary);
    }

    private SyntaxExprCall ParseExprCallFromFirstArg(SyntaxNode callee, SyntaxNode firstArg)
    {
        SyntaxNode[] args;
        if (TryAdvance(TokenKind.Comma))
        {
            var remainingArgs = ParseDelimited(() => ParseExpr(ExprParseContext.Default), TokenKind.Comma, "an expression", false, TokenKind.CloseParen, TokenKind.SemiColon);
            args = [firstArg, .. remainingArgs];
        }
        else args = [firstArg];

        Expect(TokenKind.CloseParen, "')'");
        return new SyntaxExprCall(callee, args);
    }

    private SyntaxConstructorInit ParseConstructorInit()
    {
        if (ParseDesignator() is { } designator)
        {
            var location = designator.Location;

            var designators = new List<SyntaxDesignator>() { designator };
            while ((designator = ParseDesignator()) is not null)
                designators.Add(designator);

            Expect(TokenKind.Equal, "'='");
            
            SyntaxNode initializer;
            if (At(TokenKind.Comma, TokenKind.CloseBrace, TokenKind.SemiColon))
                initializer = new SyntaxExprEmpty(new Location(CurrentLocation.Offset, 0, SourceFile.FileId));
            else initializer = ParseExpr(ExprParseContext.Default);

            return new SyntaxConstructorInitDesignated(location, designators, initializer);
        }

        SyntaxDesignator? ParseDesignator()
        {
            if (Consume(TokenKind.Dot))
            {
                ExpectIdentifier(out var fieldToken);
                return new SyntaxDesignatorField(fieldToken);
            }
            else if (Consume(TokenKind.OpenBracket))
            {
                var indexExpr = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseBracket, "']'");
                return new SyntaxDesignatorIndex(indexExpr);
            }

            return null;
        }

        var exprValue = ParseExpr(ExprParseContext.Default);
        if (Consume(TokenKind.Equal, out var tokenEq))
        {
            Context.Diag.Error(tokenEq.Location, "Unexpected '='. Expected ',' or '}' to continue or end a constructor.");
            Context.Diag.Note(exprValue.Location, "Did you mean for this to be a designator?");

            designator = new SyntaxDesignatorInvalid(exprValue);
            
            SyntaxNode initializer;
            if (At(TokenKind.Comma, TokenKind.CloseBrace, TokenKind.SemiColon))
                initializer = new SyntaxExprEmpty(new Location(CurrentLocation.Offset, 0, SourceFile.FileId));
            else initializer = ParseExpr(ExprParseContext.Default);

            return new SyntaxConstructorInitDesignated(designator.Location, [designator], initializer);
        }

        return new SyntaxConstructorInit(exprValue.Location, exprValue);
    }

    private static bool CanStartExpr(TokenKind kind) => kind switch
    {
        TokenKind.Alignof or
        TokenKind.Ampersand or
        TokenKind.Bool or
        TokenKind.BoolSized or
        TokenKind.BuiltinFFIBool or
        TokenKind.BuiltinFFIChar or
        TokenKind.BuiltinFFIDouble or
        TokenKind.BuiltinFFIFloat or
        TokenKind.BuiltinFFIInt or
        TokenKind.BuiltinFFILong or
        TokenKind.BuiltinFFILongDouble or
        TokenKind.BuiltinFFILongLong or
        TokenKind.BuiltinFFIShort or
        TokenKind.Cast or
        TokenKind.ColonColon or
        TokenKind.Countof or
        TokenKind.Do or
        TokenKind.Eval or
        TokenKind.False or
        TokenKind.FloatSized or
        TokenKind.Global or
        TokenKind.Identifier or
        TokenKind.If or
        TokenKind.Int or
        TokenKind.IntSized or
        TokenKind.LiteralFloat or
        TokenKind.LiteralInteger or
        TokenKind.LiteralRune or
        TokenKind.LiteralString or
        TokenKind.Minus or
        TokenKind.MinusMinus or
        TokenKind.Mut or
        TokenKind.New or
        TokenKind.Nil or
        TokenKind.Not or
        TokenKind.Offsetof or
        TokenKind.OpenParen or
        TokenKind.Plus or
        TokenKind.Rankof or
        TokenKind.Sizeof or
        TokenKind.Star or
        TokenKind.Tilde or
        TokenKind.True or
        TokenKind.Try or
        TokenKind.Typeof or
        TokenKind.Var or
        TokenKind.Void => true,
        _ => false,
    };

    private SyntaxNode ParsePrimaryExprContinuation(ExprParseContext parseContext, SyntaxNode primary)
    {
        switch (CurrentToken.Kind)
        {
            default: return primary;

            // mut applies to any type any time
            case TokenKind.Mut when primary.CanBeType:
            // `type**`is always a pointer type
            // NOTE(local): `type**` is not always a pointer type, because `foo * *bar` is a valid binary expression, multiplying `foo` by `*bar`.
            // I don't want to be greedy about this case, but it should probably be parenthesized anyway.
            // case TokenKind.Star when PeekAt(1, TokenKind.Star) && primary.CanBeType:
            // `type* mut` is always a pointer type, because we're greedy
            case TokenKind.Star when primary.CanBeType && PeekAt(1, TokenKind.Mut):
            // `type* <non-expression-token>` should just become a type, since it can't be a valid binary operator expression anyway.
            case TokenKind.Star when primary.CanBeType && !CanStartExpr(Peek(1).Kind):
            // `type[*]` ~~(and `type[*:`)~~ are always buffer types
            case TokenKind.OpenBracket when Peek(1).Kind == TokenKind.Star && Peek(2).Kind is TokenKind.CloseBracket /* or TokenKind.Colon */ && primary.CanBeType:
                return ParseTypeContinuation(primary);
                
            // while `type[]` is always a slice type, it could also be an array/dynamic index missing its indices
            // so just in case, we return the slice type without falling back to the type-only parser.
            // if sema encounters a slice type in an expression-only context, it should know what to report.
            case TokenKind.OpenBracket when Peek(1).Kind == TokenKind.CloseBracket && primary.CanBeType:
            {
                for (int i = 0; i < 2; i++) Advance();
                return ParsePrimaryExprContinuation(parseContext, new SyntaxTypeSlice(primary));
            }
            
            case TokenKind.OpenParen:
            {
                var tokenOpenParen = CurrentToken;
                Advance();

                var arguments = ParseDelimited(() => ParseExpr(ExprParseContext.Default), TokenKind.Comma, "an expression", false, TokenKind.CloseParen, TokenKind.SemiColon);

                Expect(TokenKind.CloseParen, "')'", out var tokenCloseParen);
                return ParsePrimaryExprContinuation(parseContext, new SyntaxExprCall(primary, arguments));
            }
            
            case TokenKind.OpenBracket:
            {
                var tokenOpenBracket = CurrentToken;
                Advance();

                var indices = ParseDelimited(() => ParseExpr(ExprParseContext.Default), TokenKind.Comma, "an expression", false, TokenKind.CloseBracket, TokenKind.SemiColon);

                Expect(TokenKind.CloseBracket, "']'", out var tokenCloseBracket);
                return ParsePrimaryExprContinuation(parseContext, new SyntaxIndex(primary, tokenOpenBracket, indices, tokenCloseBracket));
            }

            case TokenKind.Dot:
            {
                Advance();
                ExpectIdentifier(out var fieldName);
                return ParsePrimaryExprContinuation(parseContext, new SyntaxExprField(primary, fieldName));
            }

            case TokenKind.PlusPlus:
            case TokenKind.MinusMinus:
            {
                var tokenOperator = Consume();
                return ParsePrimaryExprContinuation(parseContext, new SyntaxExprUnaryPostfix(primary, tokenOperator));
            }

            case TokenKind.Is:
            {
                var tokenIs = Consume();
                var pattern = ParseExpr(ExprParseContext.Pattern);
                return new SyntaxExprPatternMatch(primary, tokenIs, pattern);
            }
        }
    }

    private SyntaxNode ParsePrimaryExpr(ExprParseContext parseContext)
    {
        if (IsDefinitelyTypeStart(CurrentToken.Kind))
        {
            var type = ParseType();
            if (Consume(TokenKind.OpenBrace))
            {
                var inits = ParseDelimited(ParseConstructorInit, TokenKind.Comma, "an initializer", true, TokenKind.CloseBrace, TokenKind.SemiColon);
                Expect(TokenKind.CloseBrace, "'}'", out var tokenCloseBrace);
                var ctor = new SyntaxExprConstructor(type, inits);
                return ParsePrimaryExprContinuation(parseContext, ctor);
            }

            return type;
        }

        switch (CurrentToken.Kind)
        {
            case TokenKind.Mut:
            case TokenKind.Var when !PeekAt(1, TokenKind.OpenBrace):
            case TokenKind.Void:
            case TokenKind.NoReturn:
            case TokenKind.Bool:
            case TokenKind.BoolSized:
            case TokenKind.Int:
            case TokenKind.IntSized:
            case TokenKind.FloatSized:
            case TokenKind.BuiltinFFIBool:
            case TokenKind.BuiltinFFIChar:
            case TokenKind.BuiltinFFIShort:
            case TokenKind.BuiltinFFIInt:
            case TokenKind.BuiltinFFILong:
            case TokenKind.BuiltinFFILongLong:
            case TokenKind.BuiltinFFIFloat:
            case TokenKind.BuiltinFFIDouble:
            case TokenKind.BuiltinFFILongDouble:
            {
                Context.Diag.ICE($"a token which definitely starts a type (kind {CurrentToken.Kind}) made it to the primary expression parser");
                throw new UnreachableException();
            }

#if false
            case TokenKind.EqualGreater:
            {
                var tokenArrow = Consume();
                var body = At(TokenKind.OpenBrace) ? ParseCompound() : ParseExpr(ExprParseContext.Default);
                return new SyntaxExprLambda([], tokenArrow, body);
            }
#endif

            case TokenKind.OpenParen when PeekAt(1, TokenKind.CloseParen) && PeekAt(2, TokenKind.EqualGreater):
            {
                for (int i = 0; i < 2; i++) Advance();
                var tokenArrow = Consume();
                var body = At(TokenKind.OpenBrace) ? ParseCompound() : ParseExpr(ExprParseContext.Default);
                return new SyntaxExprLambda([], tokenArrow, body);
            }

            case TokenKind.OpenParen when PeekAt(1, TokenKind.Identifier) && PeekAt(2, TokenKind.CloseParen, TokenKind.Comma):
            {
                Advance();
                var @params = ParseDelimited(() =>
                {
                    var paramName = ExpectIdentifier();
                    return new SyntaxDeclParam(null, new SyntaxToken(TokenKind.Var, Location.Nowhere) { IsCompilerGenerated = true }, paramName);
                }, TokenKind.Comma, "identifier", false, TokenKind.CloseParen, TokenKind.EqualGreater, TokenKind.SemiColon);
                Expect(TokenKind.CloseParen, "')'");
                var tokenArrow = Consume();
                var body = At(TokenKind.OpenBrace) ? ParseCompound() : ParseExpr(ExprParseContext.Default);
                return new SyntaxExprLambda(@params, tokenArrow, body);
            }

            case TokenKind.OpenParen:
            {
                Advance();

                //var innerExpr = ParseExpr(ExprParseContext.ForLoopInitializer);
                var innerExpr = ParseExpr(parseContext);

                if (innerExpr.IsDecl) {}
                else if (innerExpr.CanBeType && At(TokenKind.Identifier))
                {
                    var paramName = Consume();
                    innerExpr = new SyntaxDeclParam(null, innerExpr, paramName);
                }

                if (innerExpr.IsDecl)
                {
                    //Context.Assert(innerExpr is SyntaxDeclBinding, innerExpr.Location, $"when parsing an expression within parentheses that could be a for loop initializer (because it was easier than adding a new case) to determine if this is a lambda expression with typed parameters, a non-binding declaration (of type {innerExpr.GetType().Name}) was returned instead.");

                    if (innerExpr is SyntaxDeclBinding binding)
                        innerExpr = new SyntaxDeclParam(null, binding.BindingType, binding.TokenName);
                    else Context.Assert(innerExpr is SyntaxDeclParam, innerExpr.Location, $"when parsing what appears to be a lambda with typed parameters, the first decl was neither a binding nor a parameter, but was instead {innerExpr.GetType().Name}.");

                    var firstParam = (SyntaxDeclParam)innerExpr;

                    IReadOnlyList<SyntaxDeclParam> @params;
                    if (TryAdvance(TokenKind.Comma))
                        @params = [firstParam, .. ParseDelimited(ParseFunctionParameter, TokenKind.Comma, "a type", false, TokenKind.CloseParen, TokenKind.EqualGreater, TokenKind.SemiColon, TokenKind.CloseBrace)];
                    else @params = [firstParam];

                    Expect(TokenKind.CloseParen, "')'");
                    Expect(TokenKind.EqualGreater, "'=>'", out var tokenArrow);

                    var body = At(TokenKind.OpenBrace) ? ParseCompound() : ParseExpr(ExprParseContext.Default);
                    return new SyntaxExprLambda(@params, tokenArrow, body);
                }

                Expect(TokenKind.CloseParen, "')'");
                return ParsePrimaryExprContinuation(parseContext, new SyntaxGrouped(innerExpr));
            }

            case TokenKind.Identifier when PeekAt(1, TokenKind.EqualGreater):
            {
                var paramName = Consume();
                var param = new SyntaxDeclParam(null, new SyntaxToken(TokenKind.Var, Location.Nowhere) { IsCompilerGenerated = true }, paramName);
                var tokenArrow = Consume();
                var body = At(TokenKind.OpenBrace) ? ParseCompound() : ParseExpr(ExprParseContext.Default);
                return new SyntaxExprLambda([param], tokenArrow, body);
            }

            case TokenKind.Ref:
            {
                var tokenRef = Consume();
                var operand = ParsePrimaryExpr(ExprParseContext.Default);
                return new SyntaxExprRef(tokenRef, operand);
            }

            case TokenKind.Global:
            case TokenKind.ColonColon:
            case TokenKind.Identifier:
            {
#if LAYE_DECONSTRUCTOR_PATTERN_ENABLED
                var nameref = ParseNamerefWithTemplateArgumentCheck();
                if (parseContext == ExprParseContext.Pattern && At(TokenKind.OpenBrace))
                    return ParseDeconstruction(nameref);

                return ParsePrimaryExprContinuation(parseContext, nameref);

                SyntaxNode ParseDeconstruction(SyntaxNode? type)
                {
                    if (TryAdvance(TokenKind.OpenBrace, out var tokenOpenBrace))
                    {
                        var children = ParseDelimited(() => ParseDeconstruction(null), TokenKind.Comma, "an identifier or another deconstructor pattern", true, TokenKind.CloseBrace, TokenKind.SemiColon, TokenKind.CloseBracket, TokenKind.CloseParen);
                        Expect(TokenKind.CloseBrace, "'}'", out var tokenCloseBrace);
                        return new SyntaxPatternStructDeconstruction(type, tokenOpenBrace, children, tokenCloseBrace);
                    }
                    else return ExpectIdentifier();
                }
#else
                var nameref = ParseNamerefWithTemplateArgumentCheck();
                if (parseContext == ExprParseContext.Pattern && At(TokenKind.OpenBrace))
                    return ParseStructuredPatternElement(nameref);

                if (Consume(TokenKind.OpenBrace))
                {
                    var inits = ParseDelimited(ParseConstructorInit, TokenKind.Comma, "an initializer", true, TokenKind.CloseBrace, TokenKind.SemiColon);
                    Expect(TokenKind.CloseBrace, "'}'", out var tokenCloseBrace);
                    var ctor = new SyntaxExprConstructor(nameref, inits);
                    return ParsePrimaryExprContinuation(parseContext, ctor);
                }

                return ParsePrimaryExprContinuation(parseContext, nameref);

                SyntaxNode ParseStructuredPatternElement(SyntaxNode? type)
                {
                    if (TryAdvance(TokenKind.OpenBrace, out var tokenOpenBrace))
                    {
                        var children = ParseDelimited(() => ParseExpr(ExprParseContext.Default), TokenKind.Comma, "an identifier or another deconstructor pattern", true, TokenKind.CloseBrace, TokenKind.SemiColon, TokenKind.CloseBracket, TokenKind.CloseParen);
                        Expect(TokenKind.CloseBrace, "'}'", out var tokenCloseBrace);
                        return new SyntaxPatternStructured(type, tokenOpenBrace, children, tokenCloseBrace);
                    }
                    else return ExpectIdentifier();
                }
#endif
            }

            case TokenKind.Var when PeekAt(1, TokenKind.OpenBrace):
            {
                var typeVar = Consume();
                Context.Assert(At(TokenKind.OpenBrace), "you know");
                Advance();

                var inits = ParseDelimited(ParseConstructorInit, TokenKind.Comma, "an initializer", true, TokenKind.CloseBrace, TokenKind.SemiColon);
                Expect(TokenKind.CloseBrace, "'}'", out var tokenCloseBrace);
                var ctor = new SyntaxExprConstructor(typeVar, inits);
                return ParsePrimaryExprContinuation(parseContext, ctor);
            }

            case TokenKind.Nil:
            case TokenKind.True:
            case TokenKind.False:
            case TokenKind.LiteralString:
            case TokenKind.LiteralRune:
            case TokenKind.LiteralInteger:
            case TokenKind.LiteralFloat:
            {
                return Consume();
            }

            case TokenKind.Cast:
            {
                var tokenCast = Consume();

                SyntaxNode? targetType = null;
                if (Consume(TokenKind.OpenParen))
                {
                    if (!At(TokenKind.CloseParen))
                        targetType = ParseType();

                    Expect(TokenKind.CloseParen, "')'");
                }

                var expr = ParsePrimaryExpr(parseContext);
                return new SyntaxExprCast(tokenCast, targetType, expr);
            }

            case TokenKind.New:
            {
                var tokenNew = Consume();

                IReadOnlyList<SyntaxNode> @params = [];
                if (TryAdvance(TokenKind.OpenParen, out var tokenOpenParen))
                {
                    @params = ParseDelimited(
                        () => ParseExpr(ExprParseContext.Default),
                        TokenKind.Comma,
                        "an expression",
                        false,
                        TokenKind.CloseParen, TokenKind.SemiColon
                    );
                    Expect(TokenKind.CloseParen, "')'");
                }

                var type = ParseType();

                IReadOnlyList<SyntaxConstructorInit> inits = [];
                if (TryAdvance(TokenKind.OpenBrace))
                {
                    inits = ParseDelimited(
                        ParseConstructorInit,
                        TokenKind.Comma,
                        "an initializer",
                        true,
                        TokenKind.CloseBrace, TokenKind.SemiColon
                    );
                    Expect(TokenKind.CloseBrace, "'}'");
                }

                return new SyntaxExprNew(tokenNew, @params, type, inits);
            }

            case TokenKind.Sizeof:
            {
                var tokenSizeof = Consume();
                Expect(TokenKind.OpenParen, "'('");
                var operand = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'");
                return ParsePrimaryExprContinuation(parseContext, new SyntaxExprSizeof(tokenSizeof, operand));
            }

            case TokenKind.Countof:
            {
                var tokenCountof = Consume();
                Expect(TokenKind.OpenParen, "'('");
                var operand = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'");
                return ParsePrimaryExprContinuation(parseContext, new SyntaxExprCountof(tokenCountof, operand));
            }

            case TokenKind.Rankof:
            {
                var tokenRankof = Consume();
                Expect(TokenKind.OpenParen, "'('");
                var operand = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'");
                return ParsePrimaryExprContinuation(parseContext, new SyntaxExprRankof(tokenRankof, operand));
            }

            case TokenKind.Alignof:
            {
                var tokenAlignof = Consume();
                Expect(TokenKind.OpenParen, "'('");
                var operand = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'");
                return ParsePrimaryExprContinuation(parseContext, new SyntaxExprAlignof(tokenAlignof, operand));
            }

            case TokenKind.Offsetof:
            {
                var tokenOffsetof = Consume();
                Expect(TokenKind.OpenParen, "'('");
                var operand = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'");
                return ParsePrimaryExprContinuation(parseContext, new SyntaxExprOffsetof(tokenOffsetof, operand));
            }

            case TokenKind.Typeof:
            {
                var tokenTypeof = Consume();
                Expect(TokenKind.OpenParen, "'('");
                var expr = ParseExpr(ExprParseContext.Default);
                Expect(TokenKind.CloseParen, "')'");
                return ParsePrimaryExprContinuation(parseContext, new SyntaxTypeof(tokenTypeof, expr));
            }

            case TokenKind.Plus:
            case TokenKind.Minus:
            case TokenKind.Star:
            case TokenKind.Ampersand:
            case TokenKind.Tilde:
            case TokenKind.Not:
            {
                var tokenOperator = Consume();
                var expr = ParsePrimaryExpr(parseContext);
                return new SyntaxExprUnaryPrefix(tokenOperator, expr);
            }

            // TODO(local): I don't actually think this is the appropriate place to put this, but. it is what it is for now.
            case TokenKind.DotDot:
            case TokenKind.DotDotEqual:
            {
                var lhs = new SyntaxExprEmpty(CurrentLocation);
                return ParseBinaryExpr(ExprParseContext.Default, lhs);
            }

            case TokenKind.If:
            {
                var exprIf = ParseIf();
                if (exprIf.ElseBody is null)
                    Context.Diag.Error(exprIf.Location, "`if` in expression context requires `else` with no condition");
                return exprIf;
            }

            case TokenKind.Do when PeekAt(1, TokenKind.OpenBrace):
            {
                Advance();
                return ParseCompound();
            }

            default:
            {
                Context.Diag.Error(CurrentLocation, "expected an expression");
                return new SyntaxToken(TokenKind.Missing, new Location(CurrentLocation.Offset, 1, SourceFile.FileId));
            }
        }
    }

    private SyntaxNode ParseBinaryExpr(ExprParseContext parseContext, SyntaxNode lhs, int precedence = 0)
    {
        // if we're parsing template arguments, ensure we never allow arguments with
        // precedence less than or equal to `<` and `>`.
        if (parseContext == ExprParseContext.WithinTemplate)
            precedence = Math.Max(precedence, TokenKind.Plus.GetBinaryOperatorPrecedence());

        while (CurrentToken.Kind.CanBeBinaryOperator() && CurrentToken.Kind.GetBinaryOperatorPrecedence() >= precedence)
        {
            var tokenOperator = CurrentToken;
            Advance();

            SyntaxNode? rhs = null;
            if (tokenOperator.Kind is TokenKind.DotDot or TokenKind.DotDotEqual && !CanStartExpr(CurrentToken.Kind))
            {
                lhs = new SyntaxExprBinary(lhs, new SyntaxExprEmpty(CurrentLocation), tokenOperator);
                continue;
            }

            if ((parseContext == ExprParseContext.CheckForDeclarations || parseContext == ExprParseContext.ForLoopInitializer) &&
                tokenOperator.Kind is TokenKind.Star or TokenKind.DotDot &&
                At(TokenKind.Identifier, TokenKind.Operator) &&
                lhs.CanBeType)
            {
                if (At(TokenKind.Identifier) && PeekAt(1, TokenKind.Equal, TokenKind.SemiColon))
                {
                    // this is now a binding declaration
                    SyntaxNode bindingType = CreateTypeNodeFromOperator(lhs, tokenOperator);
                    return ParseBindingDeclStartingAtName(null, null, [], bindingType, parseContext != ExprParseContext.CheckForDeclarations);
                }

                if (parseContext == ExprParseContext.CheckForDeclarations && At(TokenKind.Identifier) && PeekAt(1, TokenKind.OpenParen))
                {
                    // we have some work to do to determine if this is a function
                    if (PeekAt(2, TokenKind.CloseParen) && PeekAt(3, TokenKind.SemiColon, TokenKind.OpenBrace, TokenKind.EqualGreater))
                    {
                        // this is a function declaration/definition with no parameters
                        SyntaxNode returnType = CreateTypeNodeFromOperator(lhs, tokenOperator);
                        return ParseFunctionDeclStartingAtName(null, null, [], returnType);
                    }

                    if (IsDefinitelyTypeStart(Peek(2).Kind) || (PeekAt(2, TokenKind.Ref) && IsDefinitelyTypeStart(Peek(3).Kind)))
                    {
                        // this is a function declaration/definition, since the next token
                        // within the open paren *must* start a type (or is ref and the token
                        // after *must* start a type.)
                        SyntaxNode returnType = CreateTypeNodeFromOperator(lhs, tokenOperator);
                        return ParseFunctionDeclStartingAtName(null, null, [], returnType);
                    }

                    Consume(TokenKind.Identifier, out var tokenIdent);
                    Consume(TokenKind.OpenParen, out var tokenOpenParen);

                    // TODO(local): do a better check for ref params here.

                    var firstParamOrArg = ParseExpr(ExprParseContext.Default);
                    if (firstParamOrArg.CanBeType && At(TokenKind.Identifier))
                    {
                        // this should be a parameter declaration
                        SyntaxNode returnType = CreateTypeNodeFromOperator(lhs, tokenOperator);
                        return ParseFunctionDeclStartingWithinParameters(null, null, [], returnType, tokenIdent, null, firstParamOrArg);
                    }

                    // otherwise we give up, it's an invocation on the RHS
                    var syntaxCallee = SyntaxNameref.Create(Context, tokenIdent);
                    rhs = ParseExprCallFromFirstArg(syntaxCallee, firstParamOrArg);
                }

                if (parseContext == ExprParseContext.CheckForDeclarations && At(TokenKind.Operator))
                {
                    // this is an operator overload declaration/definition
                    SyntaxNode returnType = CreateTypeNodeFromOperator(lhs, tokenOperator);
                    return ParseFunctionDeclStartingAtName(null, null, [], returnType);
                }
            }
            
            rhs ??= ParsePrimaryExpr(parseContext);

            bool isTokenOperatorRightAssoc = tokenOperator.Kind.IsRightAssociativeBinaryOperator();
            while (CurrentToken.Kind.CanBeBinaryOperator() && (
                    CurrentToken.Kind.GetBinaryOperatorPrecedence() > tokenOperator.Kind.GetBinaryOperatorPrecedence() ||
                    (isTokenOperatorRightAssoc && CurrentToken.Kind.GetBinaryOperatorPrecedence() == tokenOperator.Kind.GetBinaryOperatorPrecedence())
                ))
            {
                var rhsParseContext = parseContext == ExprParseContext.Pattern ? ExprParseContext.Pattern : ExprParseContext.Default;
                rhs = ParseBinaryExpr(rhsParseContext, rhs, CurrentToken.Kind.GetBinaryOperatorPrecedence());
            }
                
            lhs = new SyntaxExprBinary(lhs, rhs, tokenOperator);
        }

        return lhs;

        static SyntaxNode CreateTypeNodeFromOperator(SyntaxNode inner, SyntaxToken tokenOperator)
        {
            if (tokenOperator.Kind == TokenKind.Star)
                return new SyntaxTypePointer(inner);
            else if (tokenOperator.Kind == TokenKind.DotDot)
                return new SyntaxTypeRange(inner);
            else throw new UnreachableException();
        }
    }
}
