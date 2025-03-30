using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Choir.FrontEnd.Score.Diagnostics;
using Choir.Source;

namespace Choir.FrontEnd.Score.Syntax;

public sealed class ScoreParser
{
    public static ScoreSyntaxUnit ParseSyntaxUnit(ScoreContext context, SourceText source)
    {
        var tokens = ScoreLexer.ReadTokens(context, source);

        if (tokens.Count == 0)
            return new ScoreSyntaxUnit(source, tokens, []);

        context.Assert(tokens[^1].Kind == ScoreTokenKind.EndOfFile, $"The end of the token list was not an EOF token.");
        var parser = new ScoreParser(context, source, tokens);

        var topLevelNodes = new List<ScoreSyntaxNode>();
        while (true)
        {
            var beginLocation = parser.CurrentLocation;

            var node = parser.ParseTopLevel();
            topLevelNodes.Add(node);

            if (node is ScoreSyntaxEndOfUnit)
                break;

            context.Assert(parser.CurrentLocation > beginLocation, "Parser did not consume any tokens and the end of file was not reached.");
        }
        
        context.Diag.Flush();
        return new ScoreSyntaxUnit(source, tokens, topLevelNodes);
    }

    private static ScoreSyntaxToken CreateMissingToken(SourceLocation location)
    {
        return new ScoreSyntaxToken(ScoreTokenKind.Missing, new(location, location), new([], true), new([], false));
    }

    private readonly ScoreContext _context;
    private readonly SourceText _source;
    private readonly List<ScoreSyntaxToken> _tokens;

    private int _readPosition;

    private bool IsAtEnd => _readPosition >= _tokens.Count - 1;
    private ScoreSyntaxToken CurrentToken => PeekToken();
    private SourceRange CurrentRange => CurrentToken.Range;
    private SourceLocation CurrentLocation => CurrentRange.Begin;

    private ScoreParser(ScoreContext context, SourceText source, List<ScoreSyntaxToken> tokens)
    {
        _context = context;
        _source = source;
        _tokens = tokens;
    }

    #region Token Stream Access/Manipulation

    private bool At(ScoreTokenKind kind)
    {
        return CurrentToken.Kind == kind;
    }

    private bool PeekAt(int ahead, ScoreTokenKind kind)
    {
        return PeekToken(ahead).Kind == kind;
    }

    private ScoreSyntaxToken Consume()
    {
        if (IsAtEnd) return _tokens[^1];
        var result = _tokens[_readPosition];
        _readPosition++;
        return result;
    }

    private bool TryConsume(ScoreTokenKind kind, [NotNullWhen(true)] out ScoreSyntaxToken? token)
    {
        token = null;
        if (CurrentToken.Kind != kind)
            return false;

        token = Consume();
        return true;
    }

    private ScoreSyntaxToken PeekToken(int ahead = 0)
    {
        _context.Assert(ahead >= 0, $"Parameter {nameof(ahead)} to function {nameof(ScoreSyntaxToken)}::{nameof(PeekToken)} must be non-negative; the parser should never rely on token look-back.");

        int peekIndex = _readPosition + ahead;
        if (peekIndex >= _tokens.Count)
        {
            var result = _tokens[^1];
            _context.Assert(result.Kind == ScoreTokenKind.EndOfFile, _source, result.Range.Begin, $"The end of the parser's token list was not an EOF token.");
            return result;
        }

        return _tokens[peekIndex];
    }

    private ScoreSyntaxToken ExpectToken(ScoreTokenKind kind, string tokenSpelling)
    {
        if (TryConsume(kind, out var expectedToken))
            return expectedToken;

        _context.ErrorTokenExpected(_source, CurrentLocation, tokenSpelling);
        return CreateMissingToken(CurrentLocation);
    }

    private ScoreSyntaxToken ExpectIdentifier()
    {
        if (TryConsume(ScoreTokenKind.Identifier, out var expectedToken))
            return expectedToken;

        _context.ErrorIdentifierExpected(_source, CurrentLocation);

        var missingToken = new ScoreSyntaxToken(ScoreTokenKind.Missing, new(CurrentLocation, CurrentLocation), new([], true), new([], false));
        return missingToken;
    }

    private ScoreSyntaxToken ExpectSemiColon()
    {
        if (TryConsume(ScoreTokenKind.SemiColon, out var expectedToken))
            return expectedToken;

        _context.ErrorSemiColonExpected(_source, CurrentLocation);

        var missingToken = new ScoreSyntaxToken(ScoreTokenKind.Missing, new(CurrentLocation, CurrentLocation), new([], true), new([], false));
        return missingToken;
    }

    #endregion

    private ScoreSyntaxNode ParseTopLevel()
    {
        switch (CurrentToken.Kind)
        {
            case ScoreTokenKind.EndOfFile:
            {
                // this is the end of the unit, there's no point in advancing the token stream.
                return new ScoreSyntaxEndOfUnit(CurrentToken);
            }

            case ScoreTokenKind.Func: return ParseDeclFunc();

            default:
            {
                _context.Todo(_source, CurrentLocation, "What do we do when we encounter a random token at the top level?");
                throw new UnreachableException();
            }
        }
    }

    private (List<TSyntaxNode> Nodes, List<ScoreSyntaxToken> Delimiters) ParseDelimited<TSyntaxNode>(ScoreTokenKind delimiterKind, Func<TSyntaxNode> parserCallback,
        bool allowTrailingDelimiter = false, ScoreTokenKind closingTokenKind = ScoreTokenKind.Invalid)
        where TSyntaxNode : ScoreSyntaxNode
    {
        List<TSyntaxNode> nodes = [];
        List<ScoreSyntaxToken> delimiterTokens = [];

        if (IsAtEnd || At(closingTokenKind))
            return (nodes, delimiterTokens);

        nodes.Add(parserCallback());
        while (TryConsume(delimiterKind, out var delimiterToken))
        {
            delimiterTokens.Add(delimiterToken);
            if (allowTrailingDelimiter && At(closingTokenKind))
                break;

            nodes.Add(parserCallback());
        }

        return (nodes, delimiterTokens);
    }

    #region Types

    private ScoreSyntaxTypeQual ParseType()
    {
        ScoreSyntaxToken? readAccessKeywordToken = null;

        switch (CurrentToken.Kind)
        {
            case ScoreTokenKind.IntSized:
            {
                string keywordText = _source.Substring(CurrentRange);
                _context.Assert(keywordText.StartsWith("int"), _source, CurrentLocation, "Expected sized integer type keyword to start with the string 'int'.");

                bool isBitWidthValidInteger = int.TryParse(keywordText[3..], out int bitWidth);
                _context.Assert(isBitWidthValidInteger, _source, CurrentLocation + 3, "Expected sized integer type keyword to contain only digits for its bit width component.");

                if (bitWidth is < ScoreSyntaxFacts.PrimitiveTypeKeywordLowerBoundInclusive or >= ScoreSyntaxFacts.PrimitiveTypeKeywordUpperBoundExclusive)
                    _context.ErrorPrimitiveTypeSizeOutOfRange(_source, CurrentLocation + 3);

                var keywordToken = Consume();
                var syntaxType = new ScoreSyntaxTypeBuiltin(keywordToken);
                return CreateResult(syntaxType);
            }

            case ScoreTokenKind.OpenSquare when PeekAt(1, ScoreTokenKind.Star) && PeekAt(2, ScoreTokenKind.CloseSquare):
            {
                var openSquareToken = Consume();
                var starToken = Consume();
                var closeSquareToken = Consume();
                var elementType = ParseType();
                var syntaxType = new ScoreSyntaxTypeBuffer(openSquareToken, starToken, closeSquareToken, elementType);
                return CreateResult(syntaxType);
            }
        }

        //_context.ErrorTypeExpected(_source, CurrentLocation);
        _context.ErrorIdentifierExpected(_source, CurrentLocation);
        return CreateResult(null);

        ScoreSyntaxTypeQual CreateResult(ScoreSyntaxType? syntaxType)
        {
            // pass the current location as the source range just in case syntaxType is null.
            // if it is, the constructor uses this instead.
            return new ScoreSyntaxTypeQual(syntaxType, new(CurrentLocation, CurrentLocation))
            {
                ReadAccessKeywordToken = readAccessKeywordToken,
            };
        }
    }

    #endregion

    #region Declarations

    private ScoreSyntaxDeclFunc ParseDeclFunc()
    {
        _context.Assert(At(ScoreTokenKind.Func), "Parser should be at the 'func' token to parse a func declaration.");

        var funcKeywordToken = Consume();
        var funcNameToken = ExpectIdentifier();

        var openParenToken = ExpectToken(ScoreTokenKind.OpenParen, "(");
        var declParams = ParseFuncParams();
        var closeParenToken = ExpectToken(ScoreTokenKind.CloseParen, ")");

        ScoreSyntaxTypeQual? returnType = null;
        if (TryConsume(ScoreTokenKind.MinusGreater, out var minusGreaterToken))
            returnType = ParseType();

        var funcBody = ParseFuncBody();

        var funcName = new ScoreSyntaxNameIdentifier(funcNameToken);
        return new ScoreSyntaxDeclFunc(funcKeywordToken, funcName, openParenToken, declParams, closeParenToken, minusGreaterToken, returnType, funcBody);
    }

    private ScoreSyntaxDeclFuncParams ParseFuncParams()
    {
        var (declParams, commaTokens) = ParseDelimited(ScoreTokenKind.Comma, ParseDeclFuncParam, false, ScoreTokenKind.CloseParen);
        return new ScoreSyntaxDeclFuncParams(declParams, commaTokens);
    }

    private ScoreSyntaxDeclFuncParam ParseDeclFuncParam()
    {
        var paramNameToken = ExpectIdentifier();
        var colonToken = ExpectToken(ScoreTokenKind.Colon, ":");
        var paramType = ParseType();
        var paramName = new ScoreSyntaxNameIdentifier(paramNameToken);
        return new ScoreSyntaxDeclFuncParam(paramName, colonToken, paramType);
    }

    private ScoreSyntaxFuncBody ParseFuncBody()
    {
        if (TryConsume(ScoreTokenKind.SemiColon, out var semiColonToken))
            return new ScoreSyntaxFuncBodyEmpty(semiColonToken);
        else if (TryConsume(ScoreTokenKind.Equal, out var equalToken))
        {
            var returnValue = ParseExpr();
            return new ScoreSyntaxFuncBodyImplicitReturn(equalToken, returnValue);
        }
        else if (At(ScoreTokenKind.OpenCurly))
        {
            var compound = ParseExprCompound();
            return new ScoreSyntaxFuncBodyCompound(compound);
        }
        else
        {
            semiColonToken = ExpectSemiColon();
            return new ScoreSyntaxFuncBodyEmpty(semiColonToken);
        }
    }

    #endregion

    #region Statements (Expressions in a Statement context)

    public ScoreSyntaxExpr ParseStmt()
    {
        switch (CurrentToken.Kind)
        {
            case ScoreTokenKind.Return:
            {
                var returnKeywordToken = Consume();
                if (TryConsume(ScoreTokenKind.SemiColon, out var semiColonToken))
                    return new ScoreSyntaxExprReturn(returnKeywordToken, null, semiColonToken);

                var returnValue = ParseExpr();
                semiColonToken = ExpectSemiColon();
                return new ScoreSyntaxExprReturn(returnKeywordToken, returnValue, semiColonToken);
            }

            default:
            {
                var expr = ParseExpr();
                if (expr is not (ScoreSyntaxExprCompound))
                {
                    var semiColonToken = ExpectSemiColon();
                    expr = new ScoreSyntaxExprStmt(expr, semiColonToken);
                }

                return expr;
            }
        }
    }

    #endregion

    #region Expressions

    private ScoreSyntaxExpr ParseExpr()
    {
        var exprPrimary = ParseExprPrimary();
        return exprPrimary;
    }

    private ScoreSyntaxExprCompound ParseExprCompound()
    {
        var childNodes = new List<ScoreSyntaxNode>();

        var openCurlyToken = Consume();
        while (!IsAtEnd && !At(ScoreTokenKind.CloseCurly))
        {
            var statement = ParseStmt();
            childNodes.Add(statement);
        }

        var closeCurlyToken = ExpectToken(ScoreTokenKind.CloseCurly, "}");
        return new ScoreSyntaxExprCompound(openCurlyToken, childNodes, closeCurlyToken);
    }

    private ScoreSyntaxExpr ParseExprPrimary()
    {
        switch (CurrentToken.Kind)
        {
            default:
            {
                _context.Todo(_source, CurrentLocation, $"Implement a default case for {nameof(ScoreParser)}::{nameof(ParseExprPrimary)}.");
                throw new UnreachableException();
            }

            case ScoreTokenKind.LiteralInteger:
            {
                var literalToken = Consume();
                return new ScoreSyntaxExprLiteral(literalToken);
            }

            case ScoreTokenKind.OpenCurly:
            {
                var compound = ParseExprCompound();
                return compound;
            }
        }
    }

    #endregion
}
