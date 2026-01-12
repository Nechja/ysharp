using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using Superpower.Tokenizers;

namespace YSharp.Compiler.Lexing;

/// <summary>
/// Turns Y# source code into a stream of tokens.
///
/// How it works:
/// 1. We define "text parsers" that recognize patterns in raw text
/// 2. We build a tokenizer that applies these parsers in order
/// 3. The tokenizer produces Token&lt;YSharpToken&gt; objects
///
/// Important: ORDER MATTERS!
/// - Multi-char operators (-> =>) must come BEFORE single-char (- =)
/// - Otherwise "->" would be tokenized as Minus, GreaterThan
/// </summary>
public static class YSharpTokenizer
{
    // ===== Text Parsers =====
    // These recognize patterns in raw text. They don't produce tokens yet,
    // just confirm "yes, this text matches the pattern."

    /// <summary>
    /// Matches a string literal: "anything except quotes"
    /// The 'from/select' syntax is LINQ query syntax - reads like English.
    /// </summary>
    private static TextParser<Unit> StringToken { get; } =
        from open in Character.EqualTo('"')
        from content in Character.Except('"').Many()
        from close in Character.EqualTo('"')
        select Unit.Value;  // We don't care about the value here, just that it matched

    /// <summary>
    /// Matches an interpolated string: $"text {expr} more"
    /// Handles nested braces in expressions by counting depth.
    /// </summary>
    private static TextParser<Unit> InterpolatedStringToken { get; } =
        from dollar in Character.EqualTo('$')
        from open in Character.EqualTo('"')
        from content in InterpolatedContent
        from close in Character.EqualTo('"')
        select Unit.Value;

    /// <summary>
    /// Parses the content of an interpolated string, handling {expr} parts.
    /// </summary>
    private static TextParser<Unit> InterpolatedContent { get; } =
        Span.MatchedBy(
            Character.Matching(c => c != '"', "interpolated content").Many()
        ).Select(_ => Unit.Value);

    /// <summary>
    /// Matches an identifier: letter or underscore, then letters/digits/underscores
    /// Examples: foo, _bar, myVar123, __init__
    /// </summary>
    private static TextParser<Unit> IdentifierToken { get; } =
        from first in Character.Letter.Or(Character.EqualTo('_'))
        from rest in Character.LetterOrDigit.Or(Character.EqualTo('_')).Many()
        select Unit.Value;

    /// <summary>
    /// Matches a decimal number: digits.digits
    /// Examples: 3.14, 0.5, 100.0
    /// </summary>
    private static TextParser<Unit> DecimalToken { get; } =
        from whole in Character.Digit.AtLeastOnce()
        from dot in Character.EqualTo('.')
        from frac in Character.Digit.AtLeastOnce()
        select Unit.Value;

    /// <summary>
    /// Matches an integer: one or more digits
    /// Examples: 0, 42, 12345
    /// </summary>
    private static TextParser<Unit> IntegerToken { get; } =
        from digits in Character.Digit.AtLeastOnce()
        select Unit.Value;

    // ===== The Tokenizer =====
    // This is where we assemble everything. The builder processes rules top-to-bottom.

    public static Tokenizer<YSharpToken> Instance { get; } =
        new TokenizerBuilder<YSharpToken>()

            // ----- Skip whitespace and comments -----
            .Ignore(Span.WhiteSpace)
            .Ignore(Comment.CPlusPlusStyle)  // // single line comments

            // ----- Multi-character operators (MUST come before single-char!) -----
            .Match(Span.EqualTo(".."), YSharpToken.DotDot)
            .Match(Span.EqualTo("->"), YSharpToken.Arrow)
            .Match(Span.EqualTo("=>"), YSharpToken.FatArrow)
            .Match(Span.EqualTo("=="), YSharpToken.DoubleEquals)
            .Match(Span.EqualTo("!="), YSharpToken.NotEquals)
            .Match(Span.EqualTo("<="), YSharpToken.LessOrEqual)
            .Match(Span.EqualTo(">="), YSharpToken.GreaterOrEqual)
            .Match(Span.EqualTo("&&"), YSharpToken.AmpAmp)
            .Match(Span.EqualTo("||"), YSharpToken.PipePipe)
            .Match(Span.EqualTo("+="), YSharpToken.PlusEquals)
            .Match(Span.EqualTo("-="), YSharpToken.MinusEquals)
            .Match(Span.EqualTo("*="), YSharpToken.StarEquals)
            .Match(Span.EqualTo("/="), YSharpToken.SlashEquals)
            .Match(Span.EqualTo("%="), YSharpToken.PercentEquals)

            // ----- Single-character operators -----
            .Match(Character.EqualTo('+'), YSharpToken.Plus)
            .Match(Character.EqualTo('-'), YSharpToken.Minus)
            .Match(Character.EqualTo('*'), YSharpToken.Star)
            .Match(Character.EqualTo('/'), YSharpToken.Slash)
            .Match(Character.EqualTo('%'), YSharpToken.Percent)
            .Match(Character.EqualTo('='), YSharpToken.Equals)
            .Match(Character.EqualTo('!'), YSharpToken.Bang)
            .Match(Character.EqualTo('?'), YSharpToken.Question)
            .Match(Character.EqualTo('<'), YSharpToken.LessThan)
            .Match(Character.EqualTo('>'), YSharpToken.GreaterThan)

            // ----- Punctuation -----
            .Match(Character.EqualTo('('), YSharpToken.LParen)
            .Match(Character.EqualTo(')'), YSharpToken.RParen)
            .Match(Character.EqualTo('{'), YSharpToken.LBrace)
            .Match(Character.EqualTo('}'), YSharpToken.RBrace)
            .Match(Character.EqualTo('['), YSharpToken.LBracket)
            .Match(Character.EqualTo(']'), YSharpToken.RBracket)
            .Match(Character.EqualTo(','), YSharpToken.Comma)
            .Match(Character.EqualTo(':'), YSharpToken.Colon)
            .Match(Character.EqualTo(';'), YSharpToken.Semicolon)
            .Match(Character.EqualTo('.'), YSharpToken.Dot)
            .Match(Character.EqualTo('~'), YSharpToken.Tilde)

            // ----- Strings -----
            .Match(InterpolatedStringToken, YSharpToken.InterpolatedString)
            .Match(StringToken, YSharpToken.String)

            // ----- Numbers (decimal MUST come before integer!) -----
            .Match(DecimalToken, YSharpToken.Decimal)
            .Match(IntegerToken, YSharpToken.Integer)

            // ----- Keywords (MUST come before Identifier!) -----
            // Order matters: if Identifier came first, "fn" would match as Identifier
            .Match(Span.EqualTo("fn"), YSharpToken.Fn, requireDelimiters: true)
            .Match(Span.EqualTo("record"), YSharpToken.Record, requireDelimiters: true)
            .Match(Span.EqualTo("class"), YSharpToken.Class, requireDelimiters: true)
            .Match(Span.EqualTo("interface"), YSharpToken.Interface, requireDelimiters: true)
            .Match(Span.EqualTo("enum"), YSharpToken.Enum, requireDelimiters: true)
            .Match(Span.EqualTo("error"), YSharpToken.ErrorType, requireDelimiters: true)
            .Match(Span.EqualTo("this"), YSharpToken.This, requireDelimiters: true)
            .Match(Span.EqualTo("return"), YSharpToken.Return, requireDelimiters: true)
            .Match(Span.EqualTo("if"), YSharpToken.If, requireDelimiters: true)
            .Match(Span.EqualTo("else"), YSharpToken.Else, requireDelimiters: true)
            .Match(Span.EqualTo("true"), YSharpToken.True, requireDelimiters: true)
            .Match(Span.EqualTo("false"), YSharpToken.False, requireDelimiters: true)
            .Match(Span.EqualTo("None"), YSharpToken.None, requireDelimiters: true)
            .Match(Span.EqualTo("Some"), YSharpToken.Some, requireDelimiters: true)
            .Match(Span.EqualTo("let"), YSharpToken.Let, requireDelimiters: true)
            .Match(Span.EqualTo("mut"), YSharpToken.Mut, requireDelimiters: true)
            .Match(Span.EqualTo("match"), YSharpToken.Match, requireDelimiters: true)
            .Match(Span.EqualTo("_"), YSharpToken.Underscore, requireDelimiters: true)
            .Match(Span.EqualTo("for"), YSharpToken.For, requireDelimiters: true)
            .Match(Span.EqualTo("in"), YSharpToken.In, requireDelimiters: true)
            .Match(Span.EqualTo("break"), YSharpToken.Break, requireDelimiters: true)
            .Match(Span.EqualTo("continue"), YSharpToken.Continue, requireDelimiters: true)
            .Match(Span.EqualTo("with"), YSharpToken.With, requireDelimiters: true)
            .Match(Span.EqualTo("blocking"), YSharpToken.Blocking, requireDelimiters: true)
            .Match(Span.EqualTo("service"), YSharpToken.Service, requireDelimiters: true)
            .Match(Span.EqualTo("singleton"), YSharpToken.Singleton, requireDelimiters: true)
            .Match(Span.EqualTo("scoped"), YSharpToken.Scoped, requireDelimiters: true)
            .Match(Span.EqualTo("transient"), YSharpToken.Transient, requireDelimiters: true)
            .Match(Span.EqualTo("module"), YSharpToken.Module, requireDelimiters: true)
            .Match(Span.EqualTo("bind"), YSharpToken.Bind, requireDelimiters: true)
            .Match(Span.EqualTo("provide"), YSharpToken.Provide, requireDelimiters: true)
            .Match(Span.EqualTo("app"), YSharpToken.App, requireDelimiters: true)
            .Match(Span.EqualTo("extends"), YSharpToken.Extends, requireDelimiters: true)
            .Match(Span.EqualTo("scope"), YSharpToken.Scope, requireDelimiters: true)
            .Match(Span.EqualTo("route"), YSharpToken.Route, requireDelimiters: true)
            .Match(Span.EqualTo("get"), YSharpToken.Get, requireDelimiters: true)
            .Match(Span.EqualTo("post"), YSharpToken.Post, requireDelimiters: true)
            .Match(Span.EqualTo("put"), YSharpToken.Put, requireDelimiters: true)
            .Match(Span.EqualTo("delete"), YSharpToken.Delete, requireDelimiters: true)
            .Match(Span.EqualTo("int"), YSharpToken.Int, requireDelimiters: true)
            .Match(Span.EqualTo("long"), YSharpToken.Long, requireDelimiters: true)
            .Match(Span.EqualTo("float"), YSharpToken.Float, requireDelimiters: true)
            .Match(Span.EqualTo("double"), YSharpToken.Double, requireDelimiters: true)
            .Match(Span.EqualTo("bool"), YSharpToken.Bool, requireDelimiters: true)
            .Match(Span.EqualTo("string"), YSharpToken.StringType, requireDelimiters: true)
            .Match(Span.EqualTo("void"), YSharpToken.Void, requireDelimiters: true)

            // ----- Identifiers (anything else that looks like a name) -----
            .Match(IdentifierToken, YSharpToken.Identifier, requireDelimiters: true)

            .Build();

    // ===== Keyword Mapping =====
    // After tokenization, we convert certain identifiers to keywords.
    // This is simpler than trying to match keywords directly in the tokenizer.

    public static readonly Dictionary<string, YSharpToken> Keywords = new()
    {
        ["fn"] = YSharpToken.Fn,
        ["record"] = YSharpToken.Record,
        ["class"] = YSharpToken.Class,
        ["interface"] = YSharpToken.Interface,
        ["enum"] = YSharpToken.Enum,
        ["error"] = YSharpToken.ErrorType,
        ["this"] = YSharpToken.This,
        ["return"] = YSharpToken.Return,
        ["if"] = YSharpToken.If,
        ["else"] = YSharpToken.Else,
        ["true"] = YSharpToken.True,
        ["false"] = YSharpToken.False,
        ["None"] = YSharpToken.None,
        ["Some"] = YSharpToken.Some,
        ["let"] = YSharpToken.Let,
        ["mut"] = YSharpToken.Mut,
        ["match"] = YSharpToken.Match,
        ["_"] = YSharpToken.Underscore,
        ["for"] = YSharpToken.For,
        ["in"] = YSharpToken.In,
        ["break"] = YSharpToken.Break,
        ["continue"] = YSharpToken.Continue,
        ["with"] = YSharpToken.With,
        ["blocking"] = YSharpToken.Blocking,
        ["service"] = YSharpToken.Service,
        ["singleton"] = YSharpToken.Singleton,
        ["scoped"] = YSharpToken.Scoped,
        ["transient"] = YSharpToken.Transient,
        ["module"] = YSharpToken.Module,
        ["bind"] = YSharpToken.Bind,
        ["provide"] = YSharpToken.Provide,
        ["app"] = YSharpToken.App,
        ["extends"] = YSharpToken.Extends,
        ["scope"] = YSharpToken.Scope,
        ["route"] = YSharpToken.Route,
        ["get"] = YSharpToken.Get,
        ["post"] = YSharpToken.Post,
        ["put"] = YSharpToken.Put,
        ["delete"] = YSharpToken.Delete,
        ["int"] = YSharpToken.Int,
        ["long"] = YSharpToken.Long,
        ["float"] = YSharpToken.Float,
        ["double"] = YSharpToken.Double,
        ["bool"] = YSharpToken.Bool,
        ["string"] = YSharpToken.StringType,
        ["void"] = YSharpToken.Void,
    };
}
