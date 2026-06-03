namespace YSharp.Core.Lexing;

public enum YSharpToken
{
    // These are tokens that carry a value

    Identifier,

    Integer,

    Decimal,

    String,

    InterpolatedString,

    // Reserved words with special meaning

    Fn,         // fn - function declaration
    Record,     // record - domain data type
    Class,      // class - behavior/service type
    Interface,  // interface - contract definition
    Enum,       // enum - tagged union (algebraic data type)
    ErrorType,  // error - domain error type (tagged union for errors)
    This,       // this - self reference
    Return,     // return - exit function with value
    If,         // if - conditional
    Else,       // else - alternative branch
    True,       // true - boolean literal
    False,      // false - boolean literal
    None,       // None - absence of value (Option<T>)
    Some,       // Some - present value (Option<T>)
    Let,        // let - immutable variable declaration
    Mut,        // mut - mutable variable declaration
    Match,      // match - pattern matching
    Underscore, // _ - wildcard pattern
    For,        // for - unified loop
    In,         // in - iterator binding
    Break,      // break - exit loop
    Continue,   // continue - skip to next iteration
    With,       // with - record copy with modifications
    Blocking,   // blocking - synchronous block expression
    Concurrent, // concurrent - parallel I/O block

    // ===== Dependency Injection Keywords =====
    Service,    // service - DI service declaration
    Singleton,  // singleton - single instance lifetime
    Scoped,     // scoped - per-scope lifetime
    Transient,  // transient - new instance each time
    Module,     // module - DI module declaration
    Bind,       // bind - interface to implementation binding
    Provide,    // provide - configuration value
    App,        // app - application entry point
    Extends,    // extends - module inheritance
    Scope,      // scope - explicit scope block

    // ===== HTTP Keywords =====
    Route,      // route - HTTP route block
    Get,        // get - HTTP GET method
    Post,       // post - HTTP POST method
    Put,        // put - HTTP PUT method
    Delete,     // delete - HTTP DELETE method
    ModifierKw, // modifier - custom middleware declaration

    // ===== Type Keywords =====

    Int,        // int - 32-bit integer
    Long,       // long - 64-bit integer
    Float,      // float - single precision
    Double,     // double - double precision
    Bool,       // bool - true/false
    StringType, // string - text (can't use "String" as it conflicts)
    Void,       // void - no return value

    Plus,           // +
    Minus,          // -
    Star,           // *
    Slash,          // /
    Equals,         // =
    DoubleEquals,   // ==
    NotEquals,      // !=
    Bang,           // ! (logical not)
    LessThan,       // <
    GreaterThan,    // >
    LessOrEqual,    // <=
    GreaterOrEqual, // >=
    Arrow,          // ->
    FatArrow,       // =>
    DotDot,         // .. (range)
    Question,       // ? (error propagation)
    AmpAmp,         // && (logical and)
    PipePipe,       // || (logical or)
    Percent,        // % (modulo)
    PlusEquals,     // +=
    MinusEquals,    // -=
    StarEquals,     // *=
    SlashEquals,    // /=
    PercentEquals,  // %=

    LParen,     // (
    RParen,     // )
    LBrace,     // {
    RBrace,     // }
    LBracket,   // [
    RBracket,   // ]
    Comma,      // ,
    Colon,      // :
    Semicolon,  // ;
    Dot,        // .
    Tilde,      // ~ (modifier prefix)
}
