namespace Choir.Front.Laye.Syntax;

public enum TokenKind : ushort
{
    Invalid = 0,

    __PrintableTokenStart__ = 32,

    Tilde = '~',
    Bang = '!',
    Percent = '%',
    Ampersand = '&',
    Star = '*',
    OpenParen = '(',
    CloseParen = ')',
    Minus = '-',
    Equal = '=',
    Plus = '+',
    OpenBracket = '[',
    CloseBracket = ']',
    OpenBrace = '{',
    CloseBrace = '}',
    Pipe = '|',
    SemiColon = ';',
    Colon = ':',
    Comma = ',',
    Less = '<',
    Greater = '>',
    Dot = '.',
    Slash = '/',
    Question = '?',
    
    __PrintableTokenEnd__ = 128,
    
    __MultiByteStart__ = 256,
    
    EndOfFile,
    Missing,

    Identifier,
    Global,

    LiteralInteger,
    LiteralFloat,
    LiteralString,
    LiteralRune,

    TildeEqual,
    BangEqual,
    PercentEqual,
    AmpersandEqual,
    StarEqual,
    MinusEqual,
    MinusMinus,
    MinusPercent,
    MinusPipe,
    MinusPercentEqual,
    MinusPipeEqual,
    EqualEqual,
    EqualGreater,
    PlusEqual,
    PlusPlus,
    PlusPercent,
    PlusPipe,
    PlusPercentEqual,
    PlusPipeEqual,
    PipeEqual,
    ColonColon,
    LessEqual,
    LessEqualGreater,
    LessLess,
    LessLessEqual,
    LessMinus,
    GreaterEqual,
    GreaterGreater,
    GreaterGreaterEqual,
    GreaterGreaterGreater,
    GreaterGreaterGreaterEqual,
    DotDot,
    DotDotEqual,
    SlashEqual,
    QuestionQuestion,
    QuestionQuestionEqual,
    HashSquare,

    Var,
    Void,
    NoReturn,
    Bool,
    BoolSized,
    Int,
    IntSized,
    FloatSized,

    BuiltinFFIBool,
    BuiltinFFIChar,
    BuiltinFFIShort,
    BuiltinFFIInt,
    BuiltinFFILong,
    BuiltinFFILongLong,
    BuiltinFFIFloat,
    BuiltinFFIDouble,
    BuiltinFFILongDouble,

    True,
    False,
    Nil,

    If,
    Else,
    For,
    While,
    Do,
    Switch,
    Case,
    Default,
    Return,
    Break,
    Continue,
    Fallthrough,
    Yield,
    Unreachable,

    Defer,
    Discard,
    Goto,
    Xyzzy,
    Assert,
    Try,
    Catch,

    Struct,
    Variant,
    Enum,
    Template,
    Module,
    Alias,
    Delegate,
    Register,
    Test,
    Import,
    Export,
    From,
    As,
    Static,
    Strict,
    Operator,
    CFlags,

    Mut,
    Ref,
    New,
    Delete,
    Cast,
    Eval,
    Is,
    
    Sizeof,
    Alignof,
    Offsetof,
    Countof,
    Rankof,
    Typeof,

    Not,
    And,
    Or,
    Xor,

    Varargs,
    Const,
    Foreign,
    Inline,
    Callconv,
    Pure,
    Discardable,
}

public static class TokenKindExtensions
{
    public static bool CanBeBinaryOperator(this TokenKind kind) => kind.GetBinaryOperatorPrecedence() >= 0;
    public static int GetBinaryOperatorPrecedence(this TokenKind kind) => kind switch
    {
        TokenKind.QuestionQuestion => 4,

        TokenKind.Or or TokenKind.Xor => 5,
        TokenKind.And => 6,

        TokenKind.EqualEqual or TokenKind.BangEqual => 10,

        TokenKind.Less or TokenKind.LessEqual or
        TokenKind.Greater or TokenKind.GreaterEqual => 20,

        TokenKind.DotDot or TokenKind.DotDotEqual => 25,

        TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Tilde or
        TokenKind.LessLess or TokenKind.GreaterGreater or
        TokenKind.GreaterGreaterGreater => 30,

        TokenKind.Plus or TokenKind.Minus or
        TokenKind.PlusPipe or TokenKind.MinusPipe or
        TokenKind.PlusPercent or TokenKind.MinusPercent => 40,

        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 50,

        _ => -1,
    };

    public static bool IsOverloadableOperatorKind(this TokenKind kind) => kind switch
    {
        TokenKind.LessEqualGreater or
        TokenKind.EqualEqual or TokenKind.BangEqual or
        TokenKind.Less or TokenKind.LessEqual or
        TokenKind.Greater or TokenKind.GreaterEqual or
        TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Tilde or
        TokenKind.LessLess or TokenKind.GreaterGreater or
        TokenKind.GreaterGreaterGreater or
        TokenKind.Plus or TokenKind.Minus or
        TokenKind.PlusPipe or TokenKind.MinusPipe or
        TokenKind.PlusPercent or TokenKind.MinusPercent or
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent or
        _ => false,
    };

    public static bool IsRightAssociativeBinaryOperator(this TokenKind kind) => kind switch
    {
        TokenKind.QuestionQuestion => true,
        _ => false,
    };

    public static bool IsAssignmentOperator(this TokenKind kind) => kind switch
    {
        TokenKind.Equal or
        TokenKind.TildeEqual or
        TokenKind.PercentEqual or
        TokenKind.AmpersandEqual or
        TokenKind.StarEqual or
        TokenKind.MinusEqual or
        TokenKind.MinusPercentEqual or
        TokenKind.MinusPipeEqual or
        TokenKind.PlusEqual or
        TokenKind.PlusPercentEqual or
        TokenKind.PlusPipeEqual or
        TokenKind.PipeEqual or
        TokenKind.LessLessEqual or
        TokenKind.GreaterGreaterEqual or
        TokenKind.GreaterGreaterGreaterEqual or
        TokenKind.SlashEqual or
        TokenKind.QuestionQuestionEqual
            => true,
        _ => false,
    };

    public static TokenKind GetOperatorFromAssignmentOperator(this TokenKind kind) => kind switch
    {
        TokenKind.TildeEqual => TokenKind.Tilde,
        TokenKind.BangEqual => TokenKind.Bang,
        TokenKind.PercentEqual => TokenKind.Percent,
        TokenKind.AmpersandEqual => TokenKind.Ampersand,
        TokenKind.StarEqual => TokenKind.Star,
        TokenKind.MinusEqual => TokenKind.Minus,
        TokenKind.MinusPercentEqual => TokenKind.MinusPercent,
        TokenKind.MinusPipeEqual => TokenKind.MinusPipe,
        TokenKind.PlusEqual => TokenKind.Plus,
        TokenKind.PlusPercentEqual => TokenKind.PlusPercent,
        TokenKind.PlusPipeEqual => TokenKind.PlusPipe,
        TokenKind.PipeEqual => TokenKind.Pipe,
        TokenKind.LessLessEqual => TokenKind.LessLess,
        TokenKind.GreaterGreaterEqual => TokenKind.GreaterGreater,
        TokenKind.GreaterGreaterGreaterEqual => TokenKind.GreaterGreaterGreater,
        TokenKind.SlashEqual => TokenKind.Slash,
        TokenKind.QuestionQuestionEqual => TokenKind.QuestionQuestion,
        _ => kind,
    };

    public static string CanonicalOperatorName(this TokenKind kind) => kind switch
    {
        TokenKind.Tilde => "~",
        TokenKind.Bang => "!",
        TokenKind.Percent => "%",
        TokenKind.Ampersand => "&",
        TokenKind.Star => "*",
        TokenKind.OpenParen => "(",
        TokenKind.CloseParen => ")",
        TokenKind.Minus => "-",
        TokenKind.Equal => "=",
        TokenKind.Plus => "+",
        TokenKind.OpenBracket => "[",
        TokenKind.CloseBracket => "]",
        TokenKind.OpenBrace => "{",
        TokenKind.CloseBrace => "}",
        TokenKind.Pipe => "|",
        TokenKind.SemiColon => ";",
        TokenKind.Colon => ":",
        TokenKind.Comma => ",",
        TokenKind.Less => "<",
        TokenKind.Greater => ">",
        TokenKind.Dot => ".",
        TokenKind.Slash => "/",
        TokenKind.Question => "?",

        TokenKind.TildeEqual => "~=",
        TokenKind.BangEqual => "!=",
        TokenKind.PercentEqual => "%=",
        TokenKind.AmpersandEqual => "%=",
        TokenKind.StarEqual => "*=",
        TokenKind.MinusEqual => "-=",
        TokenKind.MinusMinus => "--",
        TokenKind.MinusPercent => "-%",
        TokenKind.MinusPipe => "-|",
        TokenKind.MinusPercentEqual => "-%=",
        TokenKind.MinusPipeEqual => "-|=",
        TokenKind.EqualEqual => "==",
        TokenKind.EqualGreater => "=>",
        TokenKind.PlusEqual => "+=",
        TokenKind.PlusPlus => "++",
        TokenKind.PlusPercent => "+%",
        TokenKind.PlusPipe => "+|",
        TokenKind.PlusPercentEqual => "+%=",
        TokenKind.PlusPipeEqual => "+|=",
        TokenKind.PipeEqual => "|=",
        TokenKind.ColonColon => "::",
        TokenKind.LessEqual => "<=",
        TokenKind.LessEqualGreater => "<=>",
        TokenKind.LessLess => "<<",
        TokenKind.LessLessEqual => "<<=",
        TokenKind.LessMinus => "<-",
        TokenKind.GreaterEqual => ">=",
        TokenKind.GreaterGreater => ">>",
        TokenKind.GreaterGreaterEqual => ">>=",
        TokenKind.GreaterGreaterGreater => ">>>",
        TokenKind.GreaterGreaterGreaterEqual => ">>>=",
        TokenKind.SlashEqual => "/=",
        TokenKind.QuestionQuestion => "??",
        TokenKind.QuestionQuestionEqual => "??=",

        _ => "",
    };
}
