using Superpower.Model;

namespace YSharp.Core.Ast;

public abstract record AstNode(TextSpan Span);

public abstract record Expr(TextSpan Span) : AstNode(Span);

public record IntLiteralExpr(int Value, TextSpan Span) : Expr(Span);
public record BoolLiteralExpr(bool Value, TextSpan Span) : Expr(Span);
public record StringLiteralExpr(string Value, TextSpan Span) : Expr(Span);
public record DoubleLiteralExpr(double Value, TextSpan Span) : Expr(Span);
public record NoneExpr(TextSpan Span) : Expr(Span);
public record SomeExpr(Expr Value, TextSpan Span) : Expr(Span);

public record InterpolatedStringExpr(List<InterpolatedPart> Parts, TextSpan Span) : Expr(Span);
public abstract record InterpolatedPart;
public record InterpolatedText(string Text) : InterpolatedPart;
public record InterpolatedExpr(Expr Expression) : InterpolatedPart;

public record IdentifierExpr(string Name, TextSpan Span) : Expr(Span);
public record LambdaExpr(List<string> Parameters, Expr Body, TextSpan Span) : Expr(Span);
public record UnaryExpr(string Op, Expr Operand, TextSpan Span) : Expr(Span);
public record TryExpr(Expr Operand, TextSpan Span) : Expr(Span);
public record BinaryExpr(Expr Left, string Op, Expr Right, TextSpan Span) : Expr(Span);
public record CallExpr(Expr Target, List<TypeRef> TypeArgs, List<Expr> Args, TextSpan Span) : Expr(Span);
public record MemberAccessExpr(Expr Target, string Member, TextSpan Span) : Expr(Span);
public record ThisExpr(TextSpan Span) : Expr(Span);

public record MatchArm(Expr Pattern, Expr Result, TextSpan Span) : AstNode(Span);
public record WildcardExpr(TextSpan Span) : Expr(Span);
public record MatchExpr(Expr Value, List<MatchArm> Arms, TextSpan Span) : Expr(Span);

public record RangeExpr(Expr Start, Expr End, TextSpan Span) : Expr(Span);
public record ArrayExpr(List<Expr> Elements, TextSpan Span) : Expr(Span);
public record IndexAccessExpr(Expr Target, Expr Index, TextSpan Span) : Expr(Span);

/// Sync block inside an async context. The last expression is the result.
public record BlockingExpr(List<Stmt> Statements, Expr? ResultExpr, TextSpan Span) : Expr(Span);

/// `target with { field: value, ... }` produces a modified copy of a record.
public record WithExpr(Expr Base, List<(string Name, Expr Value)> Updates, TextSpan Span) : Expr(Span);

public abstract record TypeRef(TextSpan Span) : AstNode(Span);
public record NamedTypeRef(string Name, TextSpan Span) : TypeRef(Span);
public record GenericTypeRef(string Name, List<TypeRef> TypeArgs, TextSpan Span) : TypeRef(Span);

/// `T?` sugar for `Option<T>`.
public record OptionalTypeRef(TypeRef Inner, TextSpan Span) : TypeRef(Span);

public abstract record Stmt(TextSpan Span) : AstNode(Span);

public record BlockStmt(List<Stmt> Statements, TextSpan Span) : Stmt(Span);
public record ReturnStmt(Expr? Value, TextSpan Span) : Stmt(Span);
public record ExprStmt(Expr Expression, TextSpan Span) : Stmt(Span);
public record IfStmt(Expr Condition, BlockStmt ThenBlock, BlockStmt? ElseBlock, TextSpan Span) : Stmt(Span);

/// Three forms: foreach (`for item in coll`), range (`for i in a..b`),
/// while-style (`for cond`). Variable is null for the while form.
public record ForStmt(
    string? Variable,
    Expr Iterable,
    BlockStmt Body,
    TextSpan Span
) : Stmt(Span);

public record VarDeclStmt(
    bool IsMutable,
    string Name,
    TypeRef? Type,
    Expr Value,
    TextSpan Span
) : Stmt(Span);

public record AssignStmt(string Target, Expr Value, TextSpan Span) : Stmt(Span);

/// Op is the base operator (+, -, *, /, %), the `=` is implied.
public record CompoundAssignStmt(string Target, string Op, Expr Value, TextSpan Span) : Stmt(Span);

public record BreakStmt(TextSpan Span) : Stmt(Span);
public record ContinueStmt(TextSpan Span) : Stmt(Span);

public abstract record Decl(TextSpan Span) : AstNode(Span);

public record Param(string Name, TypeRef Type, TextSpan Span) : AstNode(Span);

/// `~name` or `~name(arg, ...)` -- Args is null for the bare form.
public record Modifier(string Name, List<Expr>? Args, TextSpan Span) : AstNode(Span);

/// Either Body (block form) or ExprBody (=> form) is set, never both.
public record FnDecl(
    string Name,
    List<string> TypeParams,
    List<Param> Params,
    TypeRef ReturnType,
    List<Modifier> Modifiers,
    BlockStmt? Body,
    Expr? ExprBody,
    TextSpan Span
) : Decl(Span);

public record RecordField(
    string Name,
    TypeRef Type,
    Expr? DefaultValue,
    List<Modifier> Modifiers,
    TextSpan Span
) : AstNode(Span);

public record RecordDecl(
    string Name,
    List<RecordField> Fields,
    TextSpan Span
) : Decl(Span);

public record ClassField(
    bool IsMutable,
    string Name,
    TypeRef Type,
    Expr? DefaultValue,
    TextSpan Span
) : AstNode(Span);

public record ClassDecl(
    string Name,
    List<Param> ConstructorParams,
    List<string> Interfaces,
    List<ClassField> Fields,
    List<FnDecl> Methods,
    TextSpan Span
) : Decl(Span);

public record MethodSig(
    string Name,
    List<Param> Params,
    TypeRef ReturnType,
    List<Modifier> Modifiers,
    TextSpan Span
) : AstNode(Span);

public record InterfaceDecl(
    string Name,
    List<MethodSig> Methods,
    TextSpan Span
) : Decl(Span);

public record EnumVariant(
    string Name,
    List<RecordField> Fields,
    TextSpan Span
) : AstNode(Span);

public record EnumDecl(
    string Name,
    List<EnumVariant> Variants,
    TextSpan Span
) : Decl(Span);

public record ErrorDecl(
    string Name,
    List<EnumVariant> Variants,
    TextSpan Span
) : Decl(Span);

public enum ServiceLifetime { Singleton, Scoped, Transient }

public record ServiceDecl(
    ServiceLifetime Lifetime,
    string Name,
    List<Param> ConstructorParams,
    string? Interface,
    List<ClassField> Fields,
    List<FnDecl> Methods,
    TextSpan Span
) : Decl(Span);

public record BindingDecl(
    string Interface,
    string Implementation,
    TextSpan Span
) : Decl(Span);

public record ProvideDecl(
    string Name,
    Expr Value,
    TextSpan Span
) : Decl(Span);

public record ModuleDecl(
    string Name,
    string? Extends,
    List<BindingDecl> Bindings,
    List<ProvideDecl> Provides,
    TextSpan Span
) : Decl(Span);

public record AppDecl(
    string ModuleName,
    FnDecl MainFn,
    TextSpan Span
) : Decl(Span);

public record ScopeExpr(List<Stmt> Statements, TextSpan Span) : Expr(Span);

/// Runs all statements in parallel via Task.WhenAll.
public record ConcurrentExpr(List<Stmt> Statements, TextSpan Span) : Expr(Span);

public enum HttpMethod { Get, Post, Put, Delete }

public record RouteEndpoint(
    HttpMethod Method,
    string Path,
    string Handler,
    List<Modifier> Modifiers,
    TextSpan Span
) : AstNode(Span);

public record RouteDecl(
    string BasePath,
    List<RouteEndpoint> Endpoints,
    TextSpan Span
) : Decl(Span);

/// Middleware. ConfigParams come from `apply(config)(req)`; Dependencies
/// are fields injected from the DI container.
public record ModifierDecl(
    string Name,
    List<ClassField> Dependencies,
    List<Param> ConfigParams,
    BlockStmt Body,
    TextSpan Span
) : Decl(Span);
