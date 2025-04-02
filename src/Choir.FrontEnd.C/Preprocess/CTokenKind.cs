namespace Choir.FrontEnd.C.Preprocess;

public enum CTokenKind
{
    Invalid,

    HeaderName,
    Identifier,
    PPNumber,
    Number,
    LiteralCharacter,
    LiteralString,

    DirectiveEnd,

    Bang = '!',
    Pound = '#',
    //Dollar = '$',
    Percent = '%',
    Ampersand = '&',
    OpenParen = '(',
    CloseParen = ')',
    Star = '*',
    Plus = '+',
    Comma = ',',
    Minus = '-',
    Dot = '.',
    Slash = '/',
    Colon = ':',
    SemiColon = ';',
    Less = '<',
    Equal = '=',
    Greater = '>',
    Question = '?',
    //At = '@',
    OpenSquare = '[',
    //BackSlash = '\\',
    CloseSquare = ']',
    Caret = '^',
    //Underscore = '_',
    Backtick = '`',
    OpenCurly = '{',
    Pipe = '|',
    CloseCurly = '}',
    Tilde = '~',

    BangEqual,
    PoundPound,
    PercentEqual,
    AmpersandEqual,
    AmpersandAmpersand,
    StarEqual,
    PlusPlus,
    PlusEqual,
    MinusMinus,
    MinusEqual,
    MinusGreater,
    DotDotDot,
    SlashEqual,
    ColonColon,
    LessLess,
    LessLessEqual,
    LessEqual,
    EqualEqual,
    GreaterGreater,
    GreaterGreaterEqual,
    GreaterEqual,
    CaretEqual,
    PipePipe,
    PipeEqual,

    UnexpectedCharacter = 254,
    EndOfFile = 255,
}

public static class CTokenKindExtensions
{
    public static bool IsPunctuator(this CTokenKind kind) =>
        kind is >= CTokenKind.Bang and < CTokenKind.UnexpectedCharacter;
}
