namespace Choir.FrontEnd.C.Preprocess;

public enum CPPTokenKind
    : byte
{
    Invalid,

    HeaderName,
    Identifier,
    PPNumber,
    LiteralCharacter,
    LiteralString,

    DirectiveEnd,

    Bang = (byte)'!',
    Pound = (byte)'#',
    //Dollar = (byte)'$',
    Percent = (byte)'%',
    Ampersand = (byte)'&',
    OpenParen = (byte)'(',
    CloseParen = (byte)')',
    Star = (byte)'*',
    Plus = (byte)'+',
    Comma = (byte)',',
    Minus = (byte)'-',
    Dot = (byte)'.',
    Slash = (byte)'/',
    Colon = (byte)':',
    SemiColon = (byte)';',
    Less = (byte)'<',
    Equal = (byte)'=',
    Greater = (byte)'>',
    Question = (byte)'?',
    //At = (byte)'@',
    OpenSquare = (byte)'[',
    //BackSlash = (byte)'\\',
    CloseSquare = (byte)']',
    Caret = (byte)'^',
    //Underscore = (byte)'_',
    //Backtick = (byte)'`',
    OpenCurly = (byte)'{',
    Pipe = (byte)'|',
    CloseCurly = (byte)'}',
    Tilde = (byte)'~',

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

    LessColon,
    ColonGreater,
    LessPercent,
    PercentGreater,
    PercentColon,
    PercentColonPercentColon,

    UnexpectedCharacter = 254,
    EndOfFile = 255,
}

public static class CPPTokenKindExtensions
{
    public static bool IsPunctuator(this CPPTokenKind kind) =>
        kind is >= CPPTokenKind.Bang and < CPPTokenKind.UnexpectedCharacter;
}
