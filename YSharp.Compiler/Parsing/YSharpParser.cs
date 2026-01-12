using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using YSharp.Compiler.Ast;
using YSharp.Compiler.Lexing;

namespace YSharp.Compiler.Parsing;

/// <summary>
/// Parses Y# tokens into an AST.
///
/// This uses "parser combinators" - small parsers combined into bigger ones.
/// Think of it like LEGO: small pieces snap together to build complex structures.
///
/// Key concepts:
/// - TokenListParser&lt;TToken, TResult&gt; - parses tokens, produces TResult
/// - Token.EqualTo(X) - matches exactly token X
/// - parser.Select(x => ...) - transform the result
/// - parser1.Then(parser2) - sequence: parse 1, then parse 2
/// - parser1.Or(parser2) - choice: try 1, if fails try 2
/// - parser.Many() - zero or more
/// - parser.AtLeastOnce() - one or more
/// - Parse.Ref(() => parser) - for recursive grammars
/// </summary>
public static class YSharpParser
{
    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>
    /// Creates an empty span. We'll use this when we don't have proper span info.
    /// In a production compiler, you'd track spans properly throughout.
    /// </summary>
    private static TextSpan EmptySpan => new();

    // =========================================================================
    // TYPES
    // =========================================================================

    /// <summary>
    /// Simple type name (no generics): int, bool, string, MyType
    /// </summary>
    private static TokenListParser<YSharpToken, TypeRef> SimpleType { get; } =
        Token.EqualTo(YSharpToken.Int).Select(t => (TypeRef)new NamedTypeRef("int", t.Span))
            .Or(Token.EqualTo(YSharpToken.Bool).Select(t => (TypeRef)new NamedTypeRef("bool", t.Span)))
            .Or(Token.EqualTo(YSharpToken.StringType).Select(t => (TypeRef)new NamedTypeRef("string", t.Span)))
            .Or(Token.EqualTo(YSharpToken.Void).Select(t => (TypeRef)new NamedTypeRef("void", t.Span)));

    /// <summary>
    /// Generic type: Result&lt;int, string&gt;, List&lt;User&gt;
    /// </summary>
    private static TokenListParser<YSharpToken, TypeRef> GenericType { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lt in Token.EqualTo(YSharpToken.LessThan)
        from args in Parse.Ref(() => TypeReference).ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        from gt in Token.EqualTo(YSharpToken.GreaterThan)
        select (TypeRef)new GenericTypeRef(name.ToStringValue(), args.ToList(), name.Span);

    /// <summary>
    /// Parse a type reference: int, Result&lt;int, string&gt;, MyType
    /// </summary>
    private static TokenListParser<YSharpToken, TypeRef> TypeReference { get; } =
        GenericType.Try()
            .Or(SimpleType)
            .Or(Token.EqualTo(YSharpToken.Identifier).Select(t => (TypeRef)new NamedTypeRef(t.ToStringValue(), t.Span)));

    // =========================================================================
    // EXPRESSIONS
    // =========================================================================

    /// <summary>Integer literal: 42</summary>
    private static TokenListParser<YSharpToken, Expr> IntLiteral { get; } =
        Token.EqualTo(YSharpToken.Integer)
            .Select(t => (Expr)new IntLiteralExpr(int.Parse(t.ToStringValue()), t.Span));

    /// <summary>Boolean literal: true, false</summary>
    private static TokenListParser<YSharpToken, Expr> BoolLiteral { get; } =
        Token.EqualTo(YSharpToken.True).Select(t => (Expr)new BoolLiteralExpr(true, t.Span))
            .Or(Token.EqualTo(YSharpToken.False).Select(t => (Expr)new BoolLiteralExpr(false, t.Span)));

    /// <summary>String literal: "hello"</summary>
    private static TokenListParser<YSharpToken, Expr> StringLiteral { get; } =
        Token.EqualTo(YSharpToken.String)
            .Select(t => (Expr)new StringLiteralExpr(t.ToStringValue().Trim('"'), t.Span));

    /// <summary>
    /// Interpolated string: $"hello {name}"
    /// Parses the content to extract text and expression parts.
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> InterpolatedStringLiteral { get; } =
        Token.EqualTo(YSharpToken.InterpolatedString)
            .Select(t => ParseInterpolatedString(t.ToStringValue(), t.Span));

    /// <summary>
    /// Parses the content of an interpolated string into parts.
    /// </summary>
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

        // Add remaining text
        if (textStart < content.Length)
        {
            parts.Add(new InterpolatedText(content[textStart..]));
        }

        return new InterpolatedStringExpr(parts, span);
    }

    /// <summary>
    /// Parses a simple expression from text (for interpolated strings).
    /// Handles: identifiers, member access (a.b.c), function calls (foo())
    /// </summary>
    private static Expr ParseSimpleExpression(string text, TextSpan span)
    {
        text = text.Trim();

        // Handle function calls: name() or name(args)
        var parenIndex = text.IndexOf('(');
        if (parenIndex > 0 && text.EndsWith(')'))
        {
            var funcName = text[..parenIndex];
            var argsText = text[(parenIndex + 1)..^1].Trim();

            // Parse target (could be member access like obj.method)
            Expr target = ParseMemberAccess(funcName, span);

            // Parse arguments
            var args = new List<Expr>();
            if (!string.IsNullOrEmpty(argsText))
            {
                // Simple split by comma (doesn't handle nested commas)
                foreach (var arg in argsText.Split(','))
                {
                    args.Add(ParseSimpleExpression(arg.Trim(), span));
                }
            }

            return new CallExpr(target, args, span);
        }

        return ParseMemberAccess(text, span);
    }

    /// <summary>
    /// Parses member access chain from text: a.b.c
    /// </summary>
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

    /// <summary>Identifier: foo, myVar</summary>
    private static TokenListParser<YSharpToken, Expr> Identifier { get; } =
        Token.EqualTo(YSharpToken.Identifier)
            .Select(t => (Expr)new IdentifierExpr(t.ToStringValue(), t.Span));

    /// <summary>This expression: this</summary>
    private static TokenListParser<YSharpToken, Expr> This { get; } =
        Token.EqualTo(YSharpToken.This)
            .Select(t => (Expr)new ThisExpr(t.Span));

    /// <summary>Wildcard pattern: _</summary>
    private static TokenListParser<YSharpToken, Expr> Wildcard { get; } =
        Token.EqualTo(YSharpToken.Underscore)
            .Select(t => (Expr)new WildcardExpr(t.Span));

    /// <summary>Pattern for match arms: literal, identifier, or wildcard</summary>
    private static TokenListParser<YSharpToken, Expr> Pattern { get; } =
        Wildcard
            .Or(IntLiteral)
            .Or(BoolLiteral)
            .Or(StringLiteral)
            .Or(Identifier);

    /// <summary>Match arm: pattern => result;</summary>
    private static TokenListParser<YSharpToken, MatchArm> MatchArm { get; } =
        from pattern in Pattern
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from result in Parse.Ref(() => Expression)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new MatchArm(pattern, result, pattern.Span);

    /// <summary>Match expression: match value { arms }</summary>
    private static TokenListParser<YSharpToken, Expr> Match { get; } =
        from keyword in Token.EqualTo(YSharpToken.Match)
        from value in Parse.Ref(() => Expression)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from arms in MatchArm.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select (Expr)new MatchExpr(value, arms.ToList(), keyword.Span);

    /// <summary>Parenthesized expression: (expr)</summary>
    private static TokenListParser<YSharpToken, Expr> Parenthesized { get; } =
        from lparen in Token.EqualTo(YSharpToken.LParen)
        from expr in Parse.Ref(() => Expression)  // Recursive reference!
        from rparen in Token.EqualTo(YSharpToken.RParen)
        select expr;

    /// <summary>Array literal: [1, 2, 3]</summary>
    private static TokenListParser<YSharpToken, Expr> ArrayLiteral { get; } =
        from lbracket in Token.EqualTo(YSharpToken.LBracket)
        from elements in Parse.Ref(() => Expression).ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        from rbracket in Token.EqualTo(YSharpToken.RBracket)
        select (Expr)new ArrayExpr(elements.ToList(), lbracket.Span);

    /// <summary>
    /// Blocking expression: blocking { statements }
    /// Executes synchronously within an async context.
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> Blocking { get; } =
        from keyword in Token.EqualTo(YSharpToken.Blocking)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from stmts in Parse.Ref(() => Statement).Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select (Expr)new BlockingExpr(stmts.ToList(), null, keyword.Span);

    /// <summary>
    /// Scope expression: scope { statements }
    /// Creates a new DI scope for scoped services.
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> ScopeBlock { get; } =
        from keyword in Token.EqualTo(YSharpToken.Scope)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from stmts in Parse.Ref(() => Statement).Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select (Expr)new ScopeExpr(stmts.ToList(), keyword.Span);

    /// <summary>
    /// Primary expression - the "atoms" of expressions.
    /// These are the simplest expressions that don't contain operators.
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> Primary { get; } =
        Match.Try()  // Try() because match starts with keyword, needs backtrack
            .Or(Blocking.Try())  // Try() because blocking starts with keyword
            .Or(ScopeBlock.Try())  // Try() for scope keyword
            .Or(ArrayLiteral)
            .Or(IntLiteral)
            .Or(BoolLiteral)
            .Or(InterpolatedStringLiteral)
            .Or(StringLiteral)
            .Or(This)
            .Or(Identifier)
            .Or(Parenthesized);

    /// <summary>
    /// Unary expression: -x, !flag
    /// Recursively handles multiple unary ops: --x, !!flag
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> Unary { get; } =
        (from op in Token.EqualTo(YSharpToken.Minus).Select(_ => "-")
                .Or(Token.EqualTo(YSharpToken.Bang).Select(_ => "!"))
         from operand in Parse.Ref(() => Unary)
         select (Expr)new UnaryExpr(op, operand, EmptySpan))
        .Or(Primary);

    /// <summary>
    /// Postfix operations: calls foo(args), member access foo.bar, index arr[i], try expr?
    /// These chain: Error.Validation("msg") is (Error.Validation)("msg")
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> Postfix { get; } =
        from target in Unary
        from ops in (
            // Function call: (args)
            (from lparen in Token.EqualTo(YSharpToken.LParen)
             from args in Expression.ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
             from rparen in Token.EqualTo(YSharpToken.RParen)
             select (Func<Expr, Expr>)(e => new CallExpr(e, args.ToList(), EmptySpan)))
            .Or(
            // Index access: [index]
             from lbracket in Token.EqualTo(YSharpToken.LBracket)
             from index in Parse.Ref(() => Expression)
             from rbracket in Token.EqualTo(YSharpToken.RBracket)
             select (Func<Expr, Expr>)(e => new IndexAccessExpr(e, index, EmptySpan)))
            .Or(
            // Member access: .member
             from dot in Token.EqualTo(YSharpToken.Dot)
             from member in Token.EqualTo(YSharpToken.Identifier)
             select (Func<Expr, Expr>)(e => new MemberAccessExpr(e, member.ToStringValue(), EmptySpan)))
            .Or(
            // Try/propagate: expr?
             from q in Token.EqualTo(YSharpToken.Question)
             select (Func<Expr, Expr>)(e => new TryExpr(e, EmptySpan)))
        ).Many()
        select ops.Aggregate(target, (expr, op) => op(expr));

    // -------------------------------------------------------------------------
    // Binary operators with precedence
    // -------------------------------------------------------------------------
    // Precedence (lowest to highest):
    // 1. LogicalOr: ||
    // 2. LogicalAnd: &&
    // 3. Comparison: == != < > <= >=
    // 4. Range: ..
    // 5. Additive: + -
    // 6. Multiplicative: * / %
    //
    // We parse from lowest to highest precedence.
    // Each level references the next higher level.

    /// <summary>Multiplicative: a * b, a / b, a % b</summary>
    private static TokenListParser<YSharpToken, Expr> Multiplicative { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.Star).Select(_ => "*")
                .Or(Token.EqualTo(YSharpToken.Slash).Select(_ => "/"))
                .Or(Token.EqualTo(YSharpToken.Percent).Select(_ => "%")),
            Postfix,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    /// <summary>Additive: a + b, a - b</summary>
    private static TokenListParser<YSharpToken, Expr> Additive { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.Plus).Select(_ => "+")
                .Or(Token.EqualTo(YSharpToken.Minus).Select(_ => "-")),
            Multiplicative,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    /// <summary>Range: start..end</summary>
    private static TokenListParser<YSharpToken, Expr> Range { get; } =
        from left in Additive
        from range in (
            from dotdot in Token.EqualTo(YSharpToken.DotDot)
            from right in Additive
            select right
        ).OptionalOrDefault()
        select range != null ? (Expr)new RangeExpr(left, range, EmptySpan) : left;

    /// <summary>Comparison: a == b, a < b, etc.</summary>
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

    /// <summary>Logical AND: a && b</summary>
    private static TokenListParser<YSharpToken, Expr> LogicalAnd { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.AmpAmp).Select(_ => "&&"),
            Comparison,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    /// <summary>Logical OR: a || b</summary>
    private static TokenListParser<YSharpToken, Expr> LogicalOr { get; } =
        Parse.Chain(
            Token.EqualTo(YSharpToken.PipePipe).Select(_ => "||"),
            LogicalAnd,
            (op, left, right) => (Expr)new BinaryExpr(left, op, right, EmptySpan));

    /// <summary>
    /// Lambda with single parameter: x => expr
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> SingleParamLambda { get; } =
        from param in Token.EqualTo(YSharpToken.Identifier)
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from body in Parse.Ref(() => Expression)
        select (Expr)new LambdaExpr(new List<string> { param.ToStringValue() }, body, param.Span);

    /// <summary>
    /// Lambda with multiple parameters: (x, y) => expr
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> MultiParamLambda { get; } =
        from lparen in Token.EqualTo(YSharpToken.LParen)
        from parms in Token.EqualTo(YSharpToken.Identifier).ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        from rparen in Token.EqualTo(YSharpToken.RParen)
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from body in Parse.Ref(() => Expression)
        select (Expr)new LambdaExpr(parms.Select(p => p.ToStringValue()).ToList(), body, lparen.Span);

    /// <summary>
    /// Lambda expression: x => expr or (x, y) => expr
    /// </summary>
    private static TokenListParser<YSharpToken, Expr> Lambda { get; } =
        SingleParamLambda.Try()
            .Or(MultiParamLambda.Try())
            .Or(LogicalOr);

    /// <summary>Top-level expression parser</summary>
    public static TokenListParser<YSharpToken, Expr> Expression { get; } = Lambda;

    // =========================================================================
    // STATEMENTS
    // =========================================================================

    /// <summary>Return statement: return expr;</summary>
    private static TokenListParser<YSharpToken, Stmt> ReturnStatement { get; } =
        from ret in Token.EqualTo(YSharpToken.Return)
        from value in Expression.OptionalOrDefault()
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new ReturnStmt(value, ret.Span);

    /// <summary>Expression statement: expr; (for side effects like function calls)</summary>
    private static TokenListParser<YSharpToken, Stmt> ExpressionStatement { get; } =
        from expr in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new ExprStmt(expr, EmptySpan);

    /// <summary>Block: { stmt1; stmt2; }</summary>
    private static TokenListParser<YSharpToken, BlockStmt> Block { get; } =
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from stmts in Parse.Ref(() => Statement).Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new BlockStmt(stmts.ToList(), lbrace.Span);

    /// <summary>If statement: if cond { } else { }</summary>
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

    /// <summary>
    /// For statement with binding: for item in collection { }
    /// </summary>
    private static TokenListParser<YSharpToken, Stmt> ForInStatement { get; } =
        from forKeyword in Token.EqualTo(YSharpToken.For)
        from variable in Token.EqualTo(YSharpToken.Identifier)
        from inKeyword in Token.EqualTo(YSharpToken.In)
        from iterable in Expression
        from body in Block
        select (Stmt)new ForStmt(variable.ToStringValue(), iterable, body, forKeyword.Span);

    /// <summary>
    /// For statement without binding (while-style): for condition { }
    /// </summary>
    private static TokenListParser<YSharpToken, Stmt> ForConditionStatement { get; } =
        from forKeyword in Token.EqualTo(YSharpToken.For)
        from condition in Expression
        from body in Block
        select (Stmt)new ForStmt(null, condition, body, forKeyword.Span);

    /// <summary>
    /// Unified for statement - try "for x in" first, fall back to "for condition"
    /// </summary>
    private static TokenListParser<YSharpToken, Stmt> ForStatement { get; } =
        ForInStatement.Try().Or(ForConditionStatement);

    /// <summary>
    /// Variable declaration: let x = 5; or mut x = 5; or let x: int = 5;
    /// Requires explicit let/mut keyword to distinguish from assignment.
    /// </summary>
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

    /// <summary>
    /// Assignment statement: x = expr; (modifies existing variable)
    /// </summary>
    private static TokenListParser<YSharpToken, Stmt> Assignment { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from eq in Token.EqualTo(YSharpToken.Equals)
        from value in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select (Stmt)new AssignStmt(name.ToStringValue(), value, name.Span);

    /// <summary>
    /// Compound assignment: x += expr; x -= expr; etc.
    /// </summary>
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

    /// <summary>Any statement</summary>
    public static TokenListParser<YSharpToken, Stmt> Statement { get; } =
        IfStatement
            .Or(ForStatement)
            .Or(ReturnStatement)
            .Or(VarDeclaration)  // Starts with let/mut, unambiguous
            .Or(CompoundAssignment.Try())  // Try() - starts with Identifier
            .Or(Assignment.Try())  // Try() because starts with Identifier like ExpressionStatement
            .Or(ExpressionStatement);

    // =========================================================================
    // DECLARATIONS
    // =========================================================================

    /// <summary>Function parameter: name: type</summary>
    private static TokenListParser<YSharpToken, Param> Parameter { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from colon in Token.EqualTo(YSharpToken.Colon)
        from type in TypeReference
        select new Param(name.ToStringValue(), type, name.Span);

    /// <summary>Parameter list: (a: int, b: int)</summary>
    private static TokenListParser<YSharpToken, List<Param>> Parameters { get; } =
        from lparen in Token.EqualTo(YSharpToken.LParen)
        from parms in Parameter.ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        from rparen in Token.EqualTo(YSharpToken.RParen)
        select parms.ToList();

    // =========================================================================
    // RECORDS
    // =========================================================================

    /// <summary>Record field: name: type or name: type = default;</summary>
    private static TokenListParser<YSharpToken, RecordField> RecordField { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from colon in Token.EqualTo(YSharpToken.Colon)
        from type in TypeReference
        from defaultValue in (
            from eq in Token.EqualTo(YSharpToken.Equals)
            from value in Expression
            select value
        ).OptionalOrDefault()
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new RecordField(name.ToStringValue(), type, defaultValue, name.Span);

    /// <summary>Record declaration: record Name { fields }</summary>
    private static TokenListParser<YSharpToken, RecordDecl> Record { get; } =
        from keyword in Token.EqualTo(YSharpToken.Record)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from fields in RecordField.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new RecordDecl(name.ToStringValue(), fields.ToList(), keyword.Span);

    // =========================================================================
    // CLASSES
    // =========================================================================

    /// <summary>Class field: name: type; or mut name: type = default;</summary>
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

    /// <summary>Class member: either a field or a method</summary>
    private static TokenListParser<YSharpToken, object> ClassMember { get; } =
        Parse.Ref(() => Function).Select(f => (object)f)
            .Or(ClassField.Try().Select(f => (object)f));

    /// <summary>Interface list: : Interface1, Interface2</summary>
    private static TokenListParser<YSharpToken, List<string>> InterfaceList { get; } =
        from colon in Token.EqualTo(YSharpToken.Colon)
        from interfaces in Token.EqualTo(YSharpToken.Identifier)
            .ManyDelimitedBy(Token.EqualTo(YSharpToken.Comma))
        select interfaces.Select(t => t.ToStringValue()).ToList();

    /// <summary>Class declaration: class Name(params) : Interfaces { fields; methods }</summary>
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

    // =========================================================================
    // INTERFACES
    // =========================================================================

    /// <summary>Method signature: fn name(params) -> type;</summary>
    private static TokenListParser<YSharpToken, MethodSig> MethodSignature { get; } =
        from fn in Token.EqualTo(YSharpToken.Fn)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from parms in Parameters
        from arrow in Token.EqualTo(YSharpToken.Arrow)
        from retType in TypeReference
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new MethodSig(name.ToStringValue(), parms, retType, fn.Span);

    /// <summary>Interface declaration: interface Name { method signatures }</summary>
    private static TokenListParser<YSharpToken, InterfaceDecl> Interface { get; } =
        from keyword in Token.EqualTo(YSharpToken.Interface)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from methods in MethodSignature.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new InterfaceDecl(name.ToStringValue(), methods.ToList(), keyword.Span);

    // =========================================================================
    // ENUMS (Tagged Unions)
    // =========================================================================

    /// <summary>
    /// Enum variant with associated data: VariantName { field: type; };
    /// </summary>
    private static TokenListParser<YSharpToken, EnumVariant> EnumVariantWithData { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from fields in RecordField.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new EnumVariant(name.ToStringValue(), fields.ToList(), name.Span);

    /// <summary>
    /// Simple enum variant (no data): VariantName;
    /// </summary>
    private static TokenListParser<YSharpToken, EnumVariant> EnumVariantSimple { get; } =
        from name in Token.EqualTo(YSharpToken.Identifier)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new EnumVariant(name.ToStringValue(), new List<RecordField>(), name.Span);

    /// <summary>
    /// Enum variant: either simple or with associated data
    /// Try with-data first since it's more specific
    /// </summary>
    private static TokenListParser<YSharpToken, EnumVariant> EnumVariant { get; } =
        EnumVariantWithData.Try().Or(EnumVariantSimple);

    /// <summary>
    /// Enum declaration: enum Name { Variant1; Variant2 { data: type; }; }
    /// </summary>
    private static TokenListParser<YSharpToken, EnumDecl> Enum { get; } =
        from keyword in Token.EqualTo(YSharpToken.Enum)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from variants in EnumVariant.Many()
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new EnumDecl(name.ToStringValue(), variants.ToList(), keyword.Span);

    // =========================================================================
    // MODIFIERS
    // =========================================================================

    /// <summary>
    /// Modifier name: can be an identifier or the 'blocking' keyword
    /// </summary>
    private static TokenListParser<YSharpToken, Token<YSharpToken>> ModifierName { get; } =
        Token.EqualTo(YSharpToken.Identifier)
            .Or(Token.EqualTo(YSharpToken.Blocking));

    /// <summary>
    /// Single modifier: ~name or ~name(args)
    /// </summary>
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

    /// <summary>
    /// Zero or more modifiers: ~blocking ~log(info) ~timeout(5000)
    /// </summary>
    private static TokenListParser<YSharpToken, List<Modifier>> Modifiers { get; } =
        SingleModifier.Many().Select(m => m.ToList());

    // =========================================================================
    // DEPENDENCY INJECTION
    // =========================================================================

    /// <summary>
    /// Service lifetime: singleton, scoped, transient
    /// </summary>
    private static TokenListParser<YSharpToken, ServiceLifetime> Lifetime { get; } =
        Token.EqualTo(YSharpToken.Singleton).Select(_ => ServiceLifetime.Singleton)
            .Or(Token.EqualTo(YSharpToken.Scoped).Select(_ => ServiceLifetime.Scoped))
            .Or(Token.EqualTo(YSharpToken.Transient).Select(_ => ServiceLifetime.Transient));

    /// <summary>
    /// Service member: either a field or a method (same as class)
    /// </summary>
    private static TokenListParser<YSharpToken, object> ServiceMember { get; } =
        Parse.Ref(() => Function).Select(f => (object)f)
            .Or(ClassField.Try().Select(f => (object)f));

    /// <summary>
    /// Service declaration: service singleton Name(deps) : Interface { fields; methods }
    /// </summary>
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

    /// <summary>
    /// Binding: bind Interface => Implementation;
    /// </summary>
    private static TokenListParser<YSharpToken, BindingDecl> Binding { get; } =
        from keyword in Token.EqualTo(YSharpToken.Bind)
        from iface in Token.EqualTo(YSharpToken.Identifier)
        from arrow in Token.EqualTo(YSharpToken.FatArrow)
        from impl in Token.EqualTo(YSharpToken.Identifier)
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new BindingDecl(iface.ToStringValue(), impl.ToStringValue(), keyword.Span);

    /// <summary>
    /// Provide: provide name = expr;
    /// </summary>
    private static TokenListParser<YSharpToken, ProvideDecl> Provide { get; } =
        from keyword in Token.EqualTo(YSharpToken.Provide)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from eq in Token.EqualTo(YSharpToken.Equals)
        from value in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new ProvideDecl(name.ToStringValue(), value, keyword.Span);

    /// <summary>
    /// Module member: binding or provide
    /// </summary>
    private static TokenListParser<YSharpToken, object> ModuleMember { get; } =
        Binding.Select(b => (object)b)
            .Or(Provide.Select(p => (object)p));

    /// <summary>
    /// Module declaration: module Name [extends Base] { bindings; provides; }
    /// </summary>
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

    /// <summary>
    /// App declaration: app ModuleName { fn main(services) { } }
    /// </summary>
    private static TokenListParser<YSharpToken, AppDecl> App { get; } =
        from keyword in Token.EqualTo(YSharpToken.App)
        from moduleName in Token.EqualTo(YSharpToken.Identifier)
        from lbrace in Token.EqualTo(YSharpToken.LBrace)
        from mainFn in Parse.Ref(() => Function)
        from rbrace in Token.EqualTo(YSharpToken.RBrace)
        select new AppDecl(moduleName.ToStringValue(), mainFn, keyword.Span);

    // =========================================================================
    // FUNCTIONS
    // =========================================================================

    /// <summary>
    /// Function with block body: fn name(params) -> type ~modifiers { body }
    /// </summary>
    private static TokenListParser<YSharpToken, FnDecl> FnWithBlock { get; } =
        from fn in Token.EqualTo(YSharpToken.Fn)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from parms in Parameters
        from arrow in Token.EqualTo(YSharpToken.Arrow)
        from retType in TypeReference
        from mods in Modifiers
        from body in Block
        select new FnDecl(name.ToStringValue(), parms, retType, mods, body, null, fn.Span);

    /// <summary>
    /// Function with expression body: fn name(params) -> type ~modifiers => expr;
    /// </summary>
    private static TokenListParser<YSharpToken, FnDecl> FnWithExpr { get; } =
        from fn in Token.EqualTo(YSharpToken.Fn)
        from name in Token.EqualTo(YSharpToken.Identifier)
        from parms in Parameters
        from arrow in Token.EqualTo(YSharpToken.Arrow)
        from retType in TypeReference
        from mods in Modifiers
        from fatArrow in Token.EqualTo(YSharpToken.FatArrow)
        from expr in Expression
        from semi in Token.EqualTo(YSharpToken.Semicolon)
        select new FnDecl(name.ToStringValue(), parms, retType, mods, null, expr, fn.Span);

    /// <summary>
    /// Function declaration (either style).
    /// Try() is important here - if FnWithExpr fails partway through,
    /// we backtrack and try FnWithBlock instead.
    /// </summary>
    public static TokenListParser<YSharpToken, FnDecl> Function { get; } =
        FnWithExpr.Try().Or(FnWithBlock);

    /// <summary>Any top-level declaration</summary>
    public static TokenListParser<YSharpToken, Decl> Declaration { get; } =
        Record.Select(r => (Decl)r)
            .Or(Class.Select(c => (Decl)c))
            .Or(Interface.Select(i => (Decl)i))
            .Or(Enum.Select(e => (Decl)e))
            .Or(Service.Select(s => (Decl)s))
            .Or(Module.Select(m => (Decl)m))
            .Or(App.Select(a => (Decl)a))
            .Or(Function.Select(f => (Decl)f));

    /// <summary>A complete program: list of declarations</summary>
    public static TokenListParser<YSharpToken, List<Decl>> Program { get; } =
        Declaration.Many().Select(d => d.ToList());
}
