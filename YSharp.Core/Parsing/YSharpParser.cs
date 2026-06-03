// Superpower's combinator types have nullability mismatches we can't fix from
// the outside -- disable nullable checking for this file to keep build output clean.
#nullable disable

using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using YSharp.Core.Ast;
using YSharp.Core.Lexing;

namespace YSharp.Core.Parsing;

public static class YSharpParser
{

    private static TextSpan EmptySpan => new();

    private static TokenListParser<YSharpToken, TypeRef> SimpleType { get; } =
        Token.EqualTo(YSharpToken.Int).Select(t => (TypeRef)new NamedTypeRef("int", t.Span))
            .Or(Token.EqualTo(YSharpToken.Long).Select(t => (TypeRef)new NamedTypeRef("long", t.Span)))
            .Or(Token.EqualTo(YSharpToken.Float).Select(t => (TypeRef)new NamedTypeRef("float", t.Span)))
            .Or(Token.EqualTo(YSharpToken.Double).Select(t => (TypeRef)new NamedTypeRef("double", t.Span)))
            .Or(Token.EqualTo(YSharpToken.Bool).Select(t => (TypeRef)new NamedTypeRef("bool", t.Span)))
            .Or(Token.EqualTo(YSharpToken.StringType).Select(t => (TypeRef)new NamedTypeRef("string", t.Span)))
            .Or(Token.EqualTo(YSharpToken.Void).Select(t => (TypeRef)new NamedTypeRef("void", t.Span)));

    private static TokenListParser<YSharpToken, TypeRef> GenericType { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lt in Token.EqualTo(YSharpToken.LessThan)
        from args in Parse.Ref(() => TypeReference).ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        from gt in Token.EqualTo(YSharpToken.GreaterThan)
        select (TypeRef)new GenericTypeRef(name.ToStringValue(), args.ToList(), name.Span);

    private static TokenListParser<YSharpToken, TypeRef> BaseTypeReference { get; } =
        GenericType.Try()
            .Or(SimpleType)
            .Or(Token.EqualTo(YSharpToken.Identifier).Select(t => (TypeRef)new NamedTypeRef(t.ToStringValue(), t.Span)));

    private static TokenListParser<YSharpToken, TypeRef> TypeReference { get; } =
        from baseType in BaseTypeReference
        from optional in Token.EqualTo(YSharpToken.Question).Optional()
        select optional.HasValue
            ? (TypeRef)new OptionalTypeRef(baseType, baseType.Span)
            : baseType;

    private static TokenListParser<YSharpToken, Expr> IntLiteral { get; } =
        Token.EqualTo(YSharpToken.Integer)
            .Select(t => (Expr)new IntLiteralExpr(int.Parse(t.ToStringValue()), t.Span));

    private static TokenListParser<YSharpToken, Expr> DoubleLiteral { get; } =
        Token.EqualTo(YSharpToken.Decimal)
            .Select(t => (Expr)new DoubleLiteralExpr(double.Parse(t.ToStringValue()), t.Span));

    private static TokenListParser<YSharpToken, Expr> BoolLiteral { get; } =
        Token.EqualTo(YSharpToken.True).Select(t => (Expr)new BoolLiteralExpr(true, t.Span))
            .Or(Token.EqualTo(YSharpToken.False).Select(t => (Expr)new BoolLiteralExpr(false, t.Span)));

    private static TokenListParser<YSharpToken, Expr> NoneLiteral { get; } =
        Token.EqualTo(YSharpToken.None).Select(t => (Expr)new NoneExpr(t.Span));

    private static TokenListParser<YSharpToken, Expr> SomeExpr { get; } =
        from some in Token.EqualTo(YSharpToken.Some)
        from lparen in Token.EqualTo(YSharpToken.LParen)
        from value in Parse.Ref(() => Expression)
        from rparen in Token.EqualTo(YSharpToken.RParen)
        select (Expr)new SomeExpr(value, some.Span);

    private static TokenListParser<YSharpToken, Expr> StringLiteral { get; } =
        Token.EqualTo(YSharpToken.String)
            .Select(t => (Expr)new StringLiteralExpr(t.ToStringValue().Trim('"'), t.Span));

    private static TokenListParser<YSharpToken, Expr> InterpolatedStringLiteral { get; } =
        Token.EqualTo(YSharpToken.InterpolatedString)
            .Select(t => ParseInterpolatedString(t.ToStringValue(), t.Span));

    private static Expr ParseInterpolatedString(string raw, TextSpan span)
    {
        // Remove $" prefix and " suffix
        var content = raw[2..^1];
        var parts = new List<InterpolatedPart>();
        var i = 0;
        var textStart = 0;

        while (i < content.Length)
        {
            if (content[i] == '{')
            {
                // Add text before this expression
                if (i > textStart)
                {
                    parts.Add(new InterpolatedText(content[textStart..i]));
                }

                // Find matching closing brace (handles nested braces)
                var braceDepth = 1;
                var exprStart = i + 1;
                i++;
                while (i < content.Length && braceDepth > 0)
                {
                    if (content[i] == '{') braceDepth++;
                    else if (content[i] == '}') braceDepth--;
                    i++;
                }

                // Extract and parse the expression
                var exprText = content[exprStart..(i - 1)];
                var expr = ParseSimpleExpression(exprText, span);
                parts.Add(new InterpolatedExpr(expr));
                textStart = i;
            }
            else
            {
                i++;
            }
        }

        if (textStart < content.Length)
        {
            parts.Add(new InterpolatedText(content[textStart..]));
        }

        return new InterpolatedStringExpr(parts, span);
    }

    private static Expr ParseSimpleExpression(string text, TextSpan span)
    {
        text = text.Trim();

        // Handle binary operators (low precedence first: +, -)
        // Find operator not inside parens or brackets, scanning right to left for left associativity.
        var depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == ')' || c == ']') depth++;
            else if (c == '(' || c == '[') depth--;
            else if (depth == 0 && (c == '+' || c == '-') && i > 0)
            {
                // Make sure it's not a unary minus at the start
                var left = text[..i].Trim();
                var right = text[(i + 1)..].Trim();
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
                {
                    return new BinaryExpr(
                        ParseSimpleExpression(left, span),
                        c.ToString(),
                        ParseSimpleExpression(right, span),
                        span);
                }
            }
        }

        // Handle *, / (higher precedence)
        depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == ')' || c == ']') depth++;
            else if (c == '(' || c == '[') depth--;
            else if (depth == 0 && (c == '*' || c == '/') && i > 0)
            {
                var left = text[..i].Trim();
                var right = text[(i + 1)..].Trim();
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
                {
                    return new BinaryExpr(
                        ParseSimpleExpression(left, span),
                        c.ToString(),
                        ParseSimpleExpression(right, span),
                        span);
                }
            }
        }

        if (int.TryParse(text, out var intVal))
        {
            return new IntLiteralExpr(intVal, span);
        }
        if (double.TryParse(text, out var doubleVal))
        {
            return new DoubleLiteralExpr(doubleVal, span);
        }

        // Handle index access: ...[expr]. The trailing ']' must match a top-level
        // '[' -- find by scanning right-to-left so chained `matrix[0][1]` works.
        if (text.EndsWith(']'))
        {
            var depthIdx = 0;
            var openIdx = -1;
            for (var i = text.Length - 1; i >= 0; i--)
            {
                var c = text[i];
                if (c == ']' || c == ')') depthIdx++;
                else if (c == '[' || c == '(')
                {
                    depthIdx--;
                    if (depthIdx == 0 && c == '[') { openIdx = i; break; }
                }
            }
            if (openIdx > 0)
            {
                var collectionText = text[..openIdx];
                var indexText = text[(openIdx + 1)..^1].Trim();
                return new IndexAccessExpr(
                    ParseSimpleExpression(collectionText, span),
                    ParseSimpleExpression(indexText, span),
                    span);
            }
        }

        // Handle function calls: name() or name(args)
        var parenIndex = text.IndexOf('(');
        if (parenIndex > 0 && text.EndsWith(')'))
        {
            var funcName = text[..parenIndex];
            var argsText = text[(parenIndex + 1)..^1].Trim().TrimEnd(',').TrimEnd();

            // Parse target (could be member access like obj.method)
            Expr target = ParseMemberAccess(funcName, span);

            var args = new List<Expr>();
            if (!string.IsNullOrEmpty(argsText))
            {
                // Simple split by comma (doesn't handle nested commas).
                foreach (var arg in argsText.Split(','))
                {
                    var trimmed = arg.Trim();
                    if (trimmed.Length == 0) continue; // tolerate trailing comma
                    args.Add(ParseSimpleExpression(trimmed, span));
                }
            }

            return new CallExpr(target, new List<TypeRef>(), args, span);
        }

        return ParseMemberAccess(text, span);
    }

    private static Expr ParseMemberAccess(string text, TextSpan span)
    {
        var parts = text.Split('.');
        Expr result = new IdentifierExpr(parts[0], span);
        for (var i = 1; i < parts.Length; i++)
        {
            result = new MemberAccessExpr(result, parts[i], span);
        }
        return result;
    }

    private static TokenListParser<YSharpToken, Expr> Identifier { get; } =
        Token.EqualTo(YSharpToken.Identifier)
            .Select(t => (Expr)new IdentifierExpr(t.ToStringValue(), t.Span));

    private static TokenListParser<YSharpToken, Expr> This { get; } =
        Token.EqualTo(YSharpToken.This)
            .Select(t => (Expr)new ThisExpr(t.Span));

    private static TokenListParser<YSharpToken, Expr> Wildcard { get; } =
        Token.EqualTo(YSharpToken.Underscore)
            .Select(t => (Expr)new WildcardExpr(t.Span));

    // Variant pattern: TypeName.VariantName, or TypeName.VariantName(bind1, bind2)
    // destructured into named locals.
    private static TokenListParser<YSharpToken, Expr> VariantPattern { get; } =
        from typeName in Token.EqualTo(YSharpToken.Identifier)
        from dot in Token.EqualTo(YSharpToken.Dot)
        from variantName in Token.EqualTo(YSharpToken.Identifier)
        from bindings in (
            from lparen in Token.EqualTo(YSharpToken.LParen)
            from binds in Token.EqualTo(YSharpToken.Identifier).ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
            from rparen in Token.EqualTo(YSharpToken.RParen)
            select binds
        ).OptionalOrDefault(Array.Empty<Token<YSharpToken>>())
        select (Expr)new CallExpr(
            new MemberAccessExpr(new IdentifierExpr(typeName.ToStringValue(), typeName.Span), variantName.ToStringValue(), typeName.Span),
            new List<TypeRef>(),
            bindings.Select(b => (Expr)new IdentifierExpr(b.ToStringValue(), b.Span)).ToList(),
            typeName.Span);

    private static TokenListParser<YSharpToken, Expr> Pattern { get; } =
        Wildcard
            .Or(IntLiteral)
            .Or(BoolLiteral)
            .Or(StringLiteral)
            .Or(VariantPattern.Try())
            .Or(Identifier);

    private static TokenListParser<YSharpToken, MatchArm> MatchArm { get; } =
        from pattern in Pattern
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from result in Parse.Ref(() => Expression)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new MatchArm(pattern, result, pattern.Span);

    private static TokenListParser<YSharpToken, Expr> Match { get; } =
        from keyword in Token.EqualTo(YSharpToken.Match)
        from value in Parse.Ref(() => Expression)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from arms in MatchArm.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select (Expr)new MatchExpr(value, arms.ToList(), keyword.Span);

    private static TokenListParser<YSharpToken, Expr> Parenthesized { get; } =
        from lparen in Token.EqualTo(YSharpToken.LParen)
        from expr in Parse.Ref(() => Expression)  // Recursive reference!
        from rparen in Token.EqualTo(YSharpToken.RParen)
        select expr;

    private static TokenListParser<YSharpToken, Expr> ArrayLiteral { get; } =
        from lbracket in Token.EqualTo(YSharpToken.LBracket)
        from elements in (
            from first in Parse.Ref(() => Expression)
            from rest in (
                from comma in Token.EqualTo(YSharpToken.Comma)
                from elem in Parse.Ref(() => Expression)
                select elem
            ).Try().Many()
            from trailing in Token.EqualTo(YSharpToken.Comma).Optional()
            select new[] { first }.Concat(rest).ToList()
        ).OptionalOrDefault(new List<Expr>())
        from rbracket in Token.EqualTo(YSharpToken.RBracket)
        select (Expr)new ArrayExpr(elements, lbracket.Span);

    private static TokenListParser<YSharpToken, Expr> Blocking { get; } =
        from keyword in Token.EqualTo(YSharpToken.Blocking)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from stmts in Parse.Ref(() => Statement).Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select (Expr)new BlockingExpr(stmts.ToList(), null, keyword.Span);

    private static TokenListParser<YSharpToken, Expr> ScopeBlock { get; } =
        from keyword in Token.EqualTo(YSharpToken.Scope)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from stmts in Parse.Ref(() => Statement).Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select (Expr)new ScopeExpr(stmts.ToList(), keyword.Span);

    private static TokenListParser<YSharpToken, Expr> ConcurrentBlock { get; } =
        from keyword in Token.EqualTo(YSharpToken.Concurrent)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from stmts in Parse.Ref(() => Statement).Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select (Expr)new ConcurrentExpr(stmts.ToList(), keyword.Span);

    private static TokenListParser<YSharpToken, Expr> Primary { get; } =
        Match.Try()  // Try() because match starts with keyword, needs backtrack
            .Or(Blocking.Try())  // Try() because blocking starts with keyword
            .Or(ScopeBlock.Try())  // Try() for scope keyword
            .Or(ConcurrentBlock.Try())  // Try() for concurrent keyword
            .Or(SomeExpr.Try())  // Some(value)
            .Or(ArrayLiteral)
            .Or(DoubleLiteral)  // Must come before IntLiteral
            .Or(IntLiteral)
            .Or(BoolLiteral)
            .Or(NoneLiteral)  // None
            .Or(InterpolatedStringLiteral)
            .Or(StringLiteral)
            .Or(This)
            .Or(Identifier)
            .Or(Parenthesized);

    private static TokenListParser<YSharpToken, Expr> Unary { get; } =
        (from op in Token.EqualTo(YSharpToken.Minus).Select(_ => "-")
                .Or(Token.EqualTo(YSharpToken.Bang).Select(_ => "!"))
         from operand in Parse.Ref(() => Unary)
         select (Expr)new UnaryExpr(op, operand, EmptySpan))
        .Or(Primary);

    private static TokenListParser<YSharpToken, List<TypeRef>> CallTypeArgs { get; } =
        from lt in Token.EqualTo(YSharpToken.LessThan)
        from types in TypeReference.ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        from gt in Token.EqualTo(YSharpToken.GreaterThan)
        select types.ToList();

    private static TokenListParser<YSharpToken, Expr> Postfix { get; } =
        from target in Unary
        from ops in (
            // Generic function call: <types>(args) - must Try() because < could be comparison
            (from typeArgs in CallTypeArgs
             from lparen in Token.EqualTo(YSharpToken.LParen)
             from args in CallArgList
             from rparen in Token.EqualTo(YSharpToken.RParen)
             select (Func<Expr, Expr>)(e => new CallExpr(e, typeArgs, args, EmptySpan))).Try()
            .Or(
            // Non-generic function call: (args)
            from lparen in Token.EqualTo(YSharpToken.LParen)
            from args in CallArgList
            from rparen in Token.EqualTo(YSharpToken.RParen)
            select (Func<Expr, Expr>)(e => new CallExpr(e, new List<TypeRef>(), args, EmptySpan)))
            .Or(
             from lbracket in Token.EqualTo(YSharpToken.LBracket)
             from index in Parse.Ref(() => Expression)
             from rbracket in Token.EqualTo(YSharpToken.RBracket)
             select (Func<Expr, Expr>)(e => new IndexAccessExpr(e, index, EmptySpan)))
            .Or(
            // Member access: .member -- allow keywords-as-names (e.g., `log.error`, `obj.get`).
             from dot in Token.EqualTo(YSharpToken.Dot)
             from member in MemberName
             select (Func<Expr, Expr>)(e => new MemberAccessExpr(e, member.ToStringValue(), EmptySpan)))
            .Or(
             from q in Token.EqualTo(YSharpToken.Question)
             select (Func<Expr, Expr>)(e => new TryExpr(e, EmptySpan)))
        ).Many()
        select ops.Aggregate(target, (expr, op) => op(expr));

    // Binary operators with precedence
    // Precedence (lowest to highest):
    // 3. Comparison: == != < > <= >=
    // 5. Additive: + -
    // 6. Multiplicative: * / %
    //
    // We parse from lowest to highest precedence.
    // Each level references the next higher level.

    private static TokenListParser<YSharpToken, Expr> Multiplicative { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.Star).Select(_ => "*")
                .Or(Token.EqualTo(YSharpToken.Slash).Select(_ => "/"))
                .Or(Token.EqualTo(YSharpToken.Percent).Select(_ => "%")),
            Postfix,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    private static TokenListParser<YSharpToken, Expr> Additive { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.Plus).Select(_ => "+")
                .Or(Token.EqualTo(YSharpToken.Minus).Select(_ => "-")),
            Multiplicative,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    private static TokenListParser<YSharpToken, Expr> Range { get; } =
        from left in Additive
        from range in (
            from dotdot in Token.EqualTo(YSharpToken.DotDot)
            from right in Additive
            select right
        ).OptionalOrDefault()
        select range != null ? (Expr)new RangeExpr(left, range, EmptySpan) : left;

    private static TokenListParser<YSharpToken, Expr> Comparison { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.DoubleEquals).Select(_ => "==")
                .Or(Token.EqualTo(YSharpToken.NotEquals).Select(_ => "!="))
                .Or(Token.EqualTo(YSharpToken.LessOrEqual).Select(_ => "<="))
                .Or(Token.EqualTo(YSharpToken.GreaterOrEqual).Select(_ => ">="))
                .Or(Token.EqualTo(YSharpToken.LessThan).Select(_ => "<"))
                .Or(Token.EqualTo(YSharpToken.GreaterThan).Select(_ => ">")),
            Range,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    private static TokenListParser<YSharpToken, Expr> LogicalAnd { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.AmpAmp).Select(_ => "&&"),
            Comparison,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    private static TokenListParser<YSharpToken, Expr> LogicalOr { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.PipePipe).Select(_ => "||"),
            LogicalAnd,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    private static TokenListParser<YSharpToken, (string, Expr)> WithFieldUpdate { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from colon in Token.EqualTo(YSharpToken.Colon)
        from value in Parse.Ref(() => Expression)
        select (name.ToStringValue(), value);

    private static TokenListParser<YSharpToken, Expr> WithExpression { get; } =
        from baseExpr in LogicalOr
        from withPart in (
            from withKw in Token.EqualTo(YSharpToken.With)
            from lbrace in Token.EqualTo(YSharpToken.LBrace)
            from updates in WithFieldUpdate.ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
            from rbrace in Token.EqualTo(YSharpToken.RBrace)
            select updates.ToList()
        ).OptionalOrDefault()
        select withPart != null && withPart.Count > 0
            ? (Expr)new WithExpr(baseExpr, withPart, EmptySpan)
            : baseExpr;

    private static TokenListParser<YSharpToken, Expr> SingleParamLambda { get; } =
        from param in Token.EqualTo(YSharpToken.Identifier)
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from body in Parse.Ref(() => Expression)
        select (Expr)new LambdaExpr(new List<string> { param.ToStringValue() }, body, param.Span);

    private static TokenListParser<YSharpToken, Expr> MultiParamLambda { get; } =
        from lparen in Token.EqualTo(YSharpToken.LParen)
        from parms in Token.EqualTo(YSharpToken.Identifier).ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        from rparen in Token.EqualTo(YSharpToken.RParen)
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from body in Parse.Ref(() => Expression)
        select (Expr)new LambdaExpr(parms.Select(p => p.ToStringValue()).ToList(), body, lparen.Span);

    private static TokenListParser<YSharpToken, Expr> Lambda { get; } =
        SingleParamLambda.Try()
            .Or(MultiParamLambda.Try())
            .Or(WithExpression);

    public static TokenListParser<YSharpToken, Expr> Expression { get; } = Lambda;

    private static TokenListParser<YSharpToken, Stmt> ReturnStatement { get; } =
        from ret in Token.EqualTo(YSharpToken.Return)
        from value in Expression.OptionalOrDefault()
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new ReturnStmt(value, ret.Span);

    private static TokenListParser<YSharpToken, Stmt> BreakStatement { get; } =
        from brk in Token.EqualTo(YSharpToken.Break)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new BreakStmt(brk.Span);

    private static TokenListParser<YSharpToken, Stmt> ContinueStatement { get; } =
        from cont in Token.EqualTo(YSharpToken.Continue)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new ContinueStmt(cont.Span);

    private static TokenListParser<YSharpToken, Stmt> ExpressionStatement { get; } =
        from expr in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new ExprStmt(expr, EmptySpan);

    private static TokenListParser<YSharpToken, BlockStmt> Block { get; } =
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from stmts in Parse.Ref(() => Statement).Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new BlockStmt(stmts.ToList(), lbrace.Span);

    private static TokenListParser<YSharpToken, Stmt> IfStatement { get; } =
        from ifKeyword in Token.EqualTo(YSharpToken.If)
        from condition in Expression
        from thenBlock in Block
        from elseBlock in (
            from elseKeyword in Token.EqualTo(YSharpToken.Else)
            from block in Block
            select block
        ).OptionalOrDefault()
        select (Stmt)new IfStmt(condition, thenBlock, elseBlock, ifKeyword.Span);

    private static TokenListParser<YSharpToken, Stmt> ForInStatement { get; } =
        from forKeyword in Token.EqualTo(YSharpToken.For)
        from variable in Token.EqualTo(YSharpToken.Identifier)
        from inKeyword in Token.EqualTo(YSharpToken.In)
        from iterable in Expression
        from body in Block
        select (Stmt)new ForStmt(variable.ToStringValue(), iterable, body, forKeyword.Span);

    private static TokenListParser<YSharpToken, Stmt> ForConditionStatement { get; } =
        from forKeyword in Token.EqualTo(YSharpToken.For)
        from condition in Expression
        from body in Block
        select (Stmt)new ForStmt(null, condition, body, forKeyword.Span);

    private static TokenListParser<YSharpToken, Stmt> ForStatement { get; } =
        ForInStatement.Try().Or(ForConditionStatement);

    private static TokenListParser<YSharpToken, Stmt> VarDeclaration { get; } =
        from keyword in Token.EqualTo(YSharpToken.Let).Or(Token.EqualTo(YSharpToken.Mut))
        from name in Token.EqualTo(YSharpToken.Identifier)
        from typeAnnotation in (
            from colon in Token.EqualTo(YSharpToken.Colon)
            from type in TypeReference
            select type
        ).OptionalOrDefault()
        from eq in Token.EqualTo(YSharpToken.Equals)
        from value in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new VarDeclStmt(
            keyword.Kind == YSharpToken.Mut,
            name.ToStringValue(),
            typeAnnotation,
            value,
            name.Span);

    private static TokenListParser<YSharpToken, Stmt> Assignment { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from eq in Token.EqualTo(YSharpToken.Equals)
        from value in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new AssignStmt(name.ToStringValue(), value, name.Span);

    private static TokenListParser<YSharpToken, Stmt> CompoundAssignment { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from op in Token.EqualTo(YSharpToken.PlusEquals).Select(_ => "+")
            .Or(Token.EqualTo(YSharpToken.MinusEquals).Select(_ => "-"))
            .Or(Token.EqualTo(YSharpToken.StarEquals).Select(_ => "*"))
            .Or(Token.EqualTo(YSharpToken.SlashEquals).Select(_ => "/"))
            .Or(Token.EqualTo(YSharpToken.PercentEquals).Select(_ => "%"))
        from value in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new CompoundAssignStmt(name.ToStringValue(), op, value, name.Span);

    public static TokenListParser<YSharpToken, Stmt> Statement { get; } =
        IfStatement
            .Or(ForStatement)
            .Or(ReturnStatement)
            .Or(BreakStatement)
            .Or(ContinueStatement)
            .Or(VarDeclaration)  // Starts with let/mut, unambiguous
            .Or(CompoundAssignment.Try())  // Try() - starts with Identifier
            .Or(Assignment.Try())  // Try() because starts with Identifier like ExpressionStatement
            .Or(ExpressionStatement);

    private static TokenListParser<YSharpToken, Param> Parameter { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from colon in Token.EqualTo(YSharpToken.Colon)
        from type in TypeReference
        select new Param(name.ToStringValue(), type, name.Span);

    private static TokenListParser<YSharpToken, List<Expr>> CallArgList { get; } =
        (
            from first in Parse.Ref(() => Expression)
            from rest in (
                from comma in Token.EqualTo(YSharpToken.Comma)
                from e in Parse.Ref(() => Expression)
                select e
            ).Try().Many()
            from trailing in Token.EqualTo(YSharpToken.Comma).Optional()
            select new[] { first }.Concat(rest).ToList()
        ).OptionalOrDefault(new List<Expr>());

    private static TokenListParser<YSharpToken, List<Param>> Parameters { get; } =
        from lparen in Token.EqualTo(YSharpToken.LParen)
        from parms in (
            from first in Parameter
            from rest in (
                from comma in Token.EqualTo(YSharpToken.Comma)
                from p in Parameter
                select p
            ).Try().Many()
            from trailing in Token.EqualTo(YSharpToken.Comma).Optional()
            select new[] { first }.Concat(rest).ToList()
        ).OptionalOrDefault(new List<Param>())
        from rparen in Token.EqualTo(YSharpToken.RParen)
        select parms;

    private static TokenListParser<YSharpToken, RecordField> RecordField { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from colon in Token.EqualTo(YSharpToken.Colon)
        from type in TypeReference
        from defaultValue in (
            from eq in Token.EqualTo(YSharpToken.Equals)
            from value in Expression
            select value
        ).OptionalOrDefault()
        from mods in Modifiers
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new RecordField(name.ToStringValue(), type, defaultValue, mods, name.Span);

    private static TokenListParser<YSharpToken, RecordDecl> Record { get; } =
        from keyword in Token.EqualTo(YSharpToken.Record)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from fields in RecordField.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new RecordDecl(name.ToStringValue(), fields.ToList(), keyword.Span);

    private static TokenListParser<YSharpToken, ClassField> ClassField { get; } =
        from mut in Token.EqualTo(YSharpToken.Mut).OptionalOrDefault()
        from name in Token.EqualTo(YSharpToken.Identifier)
        from colon in Token.EqualTo(YSharpToken.Colon)
        from type in TypeReference
        from defaultValue in (
            from eq in Token.EqualTo(YSharpToken.Equals)
            from value in Expression
            select value
        ).OptionalOrDefault()
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new ClassField(mut.HasValue, name.ToStringValue(), type, defaultValue, name.Span);

    private static TokenListParser<YSharpToken, object> ClassMember { get; } =
        Parse.Ref(() => Function).Try().Select(f => (object)f)
            .Or(ClassField.Try().Select(f => (object)f));

    private static TokenListParser<YSharpToken, List<string>> InterfaceList { get; } =
        from colon in Token.EqualTo(YSharpToken.Colon)
        from interfaces in Token.EqualTo(YSharpToken.Identifier)
            .ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        select interfaces.Select(t => t.ToStringValue()).ToList();

    private static TokenListParser<YSharpToken, ClassDecl> Class { get; } =
        from keyword in Token.EqualTo(YSharpToken.Class)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from ctorParams in Parameters.OptionalOrDefault()
        from interfaces in InterfaceList.OptionalOrDefault()
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from members in ClassMember.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new ClassDecl(
            name.ToStringValue(),
            ctorParams?.ToList() ?? new List<Param>(),
            interfaces ?? new List<string>(),
            members.OfType<ClassField>().ToList(),
            members.OfType<FnDecl>().ToList(),
            keyword.Span);

    private static TokenListParser<YSharpToken, MethodSig> MethodSignature { get; } =
        from fn in Token.EqualTo(YSharpToken.Fn)
        from name in Parse.Ref(() => FunctionName)
        from parms in Parameters
        from arrow in Token.EqualTo(YSharpToken.Arrow)
        from retType in TypeReference
        from mods in Modifiers
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new MethodSig(name.ToStringValue(), parms, retType, mods, fn.Span);

    private static TokenListParser<YSharpToken, InterfaceDecl> Interface { get; } =
        from keyword in Token.EqualTo(YSharpToken.Interface)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from methods in MethodSignature.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new InterfaceDecl(name.ToStringValue(), methods.ToList(), keyword.Span);

    private static TokenListParser<YSharpToken, EnumVariant> EnumVariantWithData { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from fields in RecordField.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new EnumVariant(name.ToStringValue(), fields.ToList(), name.Span);

    private static TokenListParser<YSharpToken, EnumVariant> EnumVariantSimple { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new EnumVariant(name.ToStringValue(), new List<RecordField>(), name.Span);

    private static TokenListParser<YSharpToken, EnumVariant> EnumVariant { get; } =
        EnumVariantWithData.Try().Or(EnumVariantSimple);

    private static TokenListParser<YSharpToken, EnumDecl> Enum { get; } =
        from keyword in Token.EqualTo(YSharpToken.Enum)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from variants in EnumVariant.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new EnumDecl(name.ToStringValue(), variants.ToList(), keyword.Span);

    private static TokenListParser<YSharpToken, ErrorDecl> Error { get; } =
        from keyword in Token.EqualTo(YSharpToken.ErrorType)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from variants in EnumVariant.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new ErrorDecl(name.ToStringValue(), variants.ToList(), keyword.Span);

    private static TokenListParser<YSharpToken, Token<YSharpToken>> ModifierName { get; } =
        Token.EqualTo(YSharpToken.Identifier)
            .Or(Token.EqualTo(YSharpToken.Blocking));

    private static TokenListParser<YSharpToken, Modifier> SingleModifier { get; } =
        from tilde in Token.EqualTo(YSharpToken.Tilde)
        from name in ModifierName
        from args in (
            from lparen in Token.EqualTo(YSharpToken.LParen)
            from exprs in Expression.ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
            from rparen in Token.EqualTo(YSharpToken.RParen)
            select exprs.ToList()
        ).OptionalOrDefault()
        select new Modifier(name.ToStringValue(), args, tilde.Span);

    private static TokenListParser<YSharpToken, List<Modifier>> Modifiers { get; } =
        SingleModifier.Many().Select(m => m.ToList());

    private static TokenListParser<YSharpToken, ServiceLifetime> Lifetime { get; } =
        Token.EqualTo(YSharpToken.Singleton).Select(_ => ServiceLifetime.Singleton)
            .Or(Token.EqualTo(YSharpToken.Scoped).Select(_ => ServiceLifetime.Scoped))
            .Or(Token.EqualTo(YSharpToken.Transient).Select(_ => ServiceLifetime.Transient));

    private static TokenListParser<YSharpToken, object> ServiceMember { get; } =
        Parse.Ref(() => Function).Select(f => (object)f)
            .Or(ClassField.Try().Select(f => (object)f));

    private static TokenListParser<YSharpToken, ServiceDecl> Service { get; } =
        from keyword in Token.EqualTo(YSharpToken.Service)
        from lifetime in Lifetime
        from name in Token.EqualTo(YSharpToken.Identifier)
        from ctorParams in Parameters.OptionalOrDefault()
        from iface in (
            from colon in Token.EqualTo(YSharpToken.Colon)
            from ifaceName in Token.EqualTo(YSharpToken.Identifier)
            select ifaceName.ToStringValue()
        ).OptionalOrDefault()
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from members in ServiceMember.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new ServiceDecl(
            lifetime,
            name.ToStringValue(),
            ctorParams?.ToList() ?? new List<Param>(),
            iface,
            members.OfType<ClassField>().ToList(),
            members.OfType<FnDecl>().ToList(),
            keyword.Span);

    private static TokenListParser<YSharpToken, BindingDecl> Binding { get; } =
        from keyword in Token.EqualTo(YSharpToken.Bind)
        from iface in Token.EqualTo(YSharpToken.Identifier)
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from impl in Token.EqualTo(YSharpToken.Identifier)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new BindingDecl(iface.ToStringValue(), impl.ToStringValue(), keyword.Span);

    private static TokenListParser<YSharpToken, ProvideDecl> Provide { get; } =
        from keyword in Token.EqualTo(YSharpToken.Provide)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from eq in Token.EqualTo(YSharpToken.Equals)
        from value in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new ProvideDecl(name.ToStringValue(), value, keyword.Span);

    private static TokenListParser<YSharpToken, object> ModuleMember { get; } =
        Binding.Select(b => (object)b)
            .Or(Provide.Select(p => (object)p));

    private static TokenListParser<YSharpToken, ModuleDecl> Module { get; } =
        from keyword in Token.EqualTo(YSharpToken.Module)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from extends in (
            from ext in Token.EqualTo(YSharpToken.Extends)
            from baseName in Token.EqualTo(YSharpToken.Identifier)
            select baseName.ToStringValue()
        ).OptionalOrDefault()
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from members in ModuleMember.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new ModuleDecl(
            name.ToStringValue(),
            extends,
            members.OfType<BindingDecl>().ToList(),
            members.OfType<ProvideDecl>().ToList(),
            keyword.Span);

    private static TokenListParser<YSharpToken, AppDecl> App { get; } =
        from keyword in Token.EqualTo(YSharpToken.App)
        from moduleName in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from mainFn in Parse.Ref(() => Function)
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new AppDecl(moduleName.ToStringValue(), mainFn, keyword.Span);

    private static TokenListParser<YSharpToken, Ast.HttpMethod> HttpMethodParser { get; } =
        Token.EqualTo(YSharpToken.Get).Select(_ => Ast.HttpMethod.Get)
            .Or(Token.EqualTo(YSharpToken.Post).Select(_ => Ast.HttpMethod.Post))
            .Or(Token.EqualTo(YSharpToken.Put).Select(_ => Ast.HttpMethod.Put))
            .Or(Token.EqualTo(YSharpToken.Delete).Select(_ => Ast.HttpMethod.Delete));

    private static TokenListParser<YSharpToken, RouteEndpoint> RouteEndpoint { get; } =
        from method in HttpMethodParser
        from path in Token.EqualTo(YSharpToken.String)
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from handler in Token.EqualTo(YSharpToken.Identifier)
        from mods in Modifiers
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new RouteEndpoint(method, path.ToStringValue().Trim('"'), handler.ToStringValue(), mods, path.Span);

    private static TokenListParser<YSharpToken, RouteDecl> Route { get; } =
        from keyword in Token.EqualTo(YSharpToken.Route)
        from basePath in Token.EqualTo(YSharpToken.String)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from endpoints in RouteEndpoint.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new RouteDecl(basePath.ToStringValue().Trim('"'), endpoints.ToList(), keyword.Span);

    private static TokenListParser<YSharpToken, ClassField> ModifierField { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from colon in Token.EqualTo(YSharpToken.Colon)
        from type in TypeReference
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new ClassField(false, name.ToStringValue(), type, null, name.Span);

    private static TokenListParser<YSharpToken, (List<Param> ConfigParams, BlockStmt Body)> ModifierApply { get; } =
        from fn in Token.EqualTo(YSharpToken.Fn)
        from apply in Token.EqualTo(YSharpToken.Identifier).Where(t => t.ToStringValue() == "apply")
        from parms in Parameters
        from arrow in Token.EqualTo(YSharpToken.Arrow)
        from retType in TypeReference
        from body in Block
        select (parms.Count > 1 ? parms.Take(parms.Count - 1).ToList() : new List<Param>(), body);

    private static TokenListParser<YSharpToken, ModifierDecl> ModifierDeclParser { get; } =
        from keyword in Token.EqualTo(YSharpToken.ModifierKw)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from fields in ModifierField.Try().Many()
        from applyFn in ModifierApply
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new ModifierDecl(name.ToStringValue(), fields.ToList(), applyFn.ConfigParams, applyFn.Body, keyword.Span);

    private static TokenListParser<YSharpToken, List<string>> TypeParams { get; } =
        (from lt in Token.EqualTo(YSharpToken.LessThan)
         from names in Token.EqualTo(YSharpToken.Identifier).ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
         from gt in Token.EqualTo(YSharpToken.GreaterThan)
         select names.Select(n => n.ToStringValue()).ToList())
        .OptionalOrDefault(new List<string>());

    private static TokenListParser<YSharpToken, Token<YSharpToken>> FunctionName { get; } =
        Token.EqualTo(YSharpToken.Identifier)
            .Or(Token.EqualTo(YSharpToken.Get))
            .Or(Token.EqualTo(YSharpToken.Post))
            .Or(Token.EqualTo(YSharpToken.Put))
            .Or(Token.EqualTo(YSharpToken.Delete));

    private static TokenListParser<YSharpToken, Token<YSharpToken>> MemberName { get; } =
        FunctionName.Or(Token.EqualTo(YSharpToken.ErrorType));

    private static TokenListParser<YSharpToken, FnDecl> FnWithBlock { get; } =
        from fn in Token.EqualTo(YSharpToken.Fn)
        from name in FunctionName
        from typeParams in TypeParams
        from parms in Parameters
        from arrow in Token.EqualTo(YSharpToken.Arrow)
        from retType in TypeReference
        from mods in Modifiers
        from body in Block
        select new FnDecl(name.ToStringValue(), typeParams, parms, retType, mods, body, null, fn.Span);

    private static TokenListParser<YSharpToken, FnDecl> FnWithExpr { get; } =
        from fn in Token.EqualTo(YSharpToken.Fn)
        from name in FunctionName
        from typeParams in TypeParams
        from parms in Parameters
        from arrow in Token.EqualTo(YSharpToken.Arrow)
        from retType in TypeReference
        from mods in Modifiers
        from fatArrow in Token.EqualTo(YSharpToken.FatArrow)
        from expr in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new FnDecl(name.ToStringValue(), typeParams, parms, retType, mods, null, expr, fn.Span);

    public static TokenListParser<YSharpToken, FnDecl> Function { get; } =
        FnWithExpr.Try().Or(FnWithBlock);

    public static TokenListParser<YSharpToken, Decl> Declaration { get; } =
        Record.Select(r => (Decl)r)
            .Or(Class.Select(c => (Decl)c))
            .Or(Interface.Select(i => (Decl)i))
            .Or(Enum.Select(e => (Decl)e))
            .Or(Error.Select(e => (Decl)e))
            .Or(Service.Select(s => (Decl)s))
            .Or(Module.Select(m => (Decl)m))
            .Or(App.Select(a => (Decl)a))
            .Or(Route.Select(r => (Decl)r))
            .Or(ModifierDeclParser.Select(m => (Decl)m))
            .Or(Function.Select(f => (Decl)f));

    public static TokenListParser<YSharpToken, List<Decl>> Program { get; } =
        Declaration.Many().Select(d => d.ToList());
}
