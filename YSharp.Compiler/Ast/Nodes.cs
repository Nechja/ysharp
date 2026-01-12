using Superpower.Model;

namespace YSharp.Compiler.Ast;

/// <summary>
/// Base class for all AST nodes.
/// Every node tracks where it came from in the source (Span).
/// This is essential for error messages: "Error on line 5, column 12"
/// </summary>
public abstract record AstNode(TextSpan Span);

// =============================================================================
// EXPRESSIONS - Things that produce a value
// =============================================================================

/// <summary>Base for all expressions (things that have a value)</summary>
public abstract record Expr(TextSpan Span) : AstNode(Span);

/// <summary>Integer literal: 42</summary>
public record IntLiteralExpr(int Value, TextSpan Span) : Expr(Span);

/// <summary>Boolean literal: true, false</summary>
public record BoolLiteralExpr(bool Value, TextSpan Span) : Expr(Span);

/// <summary>String literal: "hello"</summary>
public record StringLiteralExpr(string Value, TextSpan Span) : Expr(Span);

/// <summary>
/// Interpolated string: $"Hello {name}!"
/// Parts alternate between string literals and expressions.
/// </summary>
public record InterpolatedStringExpr(List<InterpolatedPart> Parts, TextSpan Span) : Expr(Span);

/// <summary>Part of an interpolated string - either text or an expression</summary>
public abstract record InterpolatedPart;
public record InterpolatedText(string Text) : InterpolatedPart;
public record InterpolatedExpr(Expr Expression) : InterpolatedPart;

/// <summary>Variable or function name: foo, myVar, add</summary>
public record IdentifierExpr(string Name, TextSpan Span) : Expr(Span);

/// <summary>
/// Lambda expression: x => x + 1, (a, b) => a + b
/// </summary>
public record LambdaExpr(List<string> Parameters, Expr Body, TextSpan Span) : Expr(Span);

/// <summary>
/// Unary operation: -x, !flag
/// </summary>
public record UnaryExpr(string Op, Expr Operand, TextSpan Span) : Expr(Span);

/// <summary>
/// Try/propagate expression: expr? (unwrap Result or propagate error)
/// Rust-style early return for error handling.
/// </summary>
public record TryExpr(Expr Operand, TextSpan Span) : Expr(Span);

/// <summary>
/// Binary operation: a + b, x > y, left == right
/// Left and Right are expressions, Op is the operator symbol.
/// </summary>
public record BinaryExpr(Expr Left, string Op, Expr Right, TextSpan Span) : Expr(Span);

/// <summary>
/// Function call: add(1, 2), print("hello")
/// Target is what we're calling (usually an identifier).
/// Args are the arguments passed.
/// </summary>
public record CallExpr(Expr Target, List<Expr> Args, TextSpan Span) : Expr(Span);

/// <summary>
/// Member access: Error.Validation, user.name
/// </summary>
public record MemberAccessExpr(Expr Target, string Member, TextSpan Span) : Expr(Span);

/// <summary>
/// This expression: this (self-reference in class methods)
/// </summary>
public record ThisExpr(TextSpan Span) : Expr(Span);

/// <summary>
/// Match arm: pattern => result
/// Pattern can be: literal, identifier (binding), or wildcard (_)
/// </summary>
public record MatchArm(Expr Pattern, Expr Result, TextSpan Span) : AstNode(Span);

/// <summary>
/// Wildcard pattern: _ (matches anything, no binding)
/// </summary>
public record WildcardExpr(TextSpan Span) : Expr(Span);

/// <summary>
/// Match expression: match value { pattern => result; ... }
/// </summary>
public record MatchExpr(Expr Value, List<MatchArm> Arms, TextSpan Span) : Expr(Span);

/// <summary>
/// Range expression: start..end (exclusive end, like Rust)
/// </summary>
public record RangeExpr(Expr Start, Expr End, TextSpan Span) : Expr(Span);

/// <summary>
/// Array literal: [1, 2, 3]
/// </summary>
public record ArrayExpr(List<Expr> Elements, TextSpan Span) : Expr(Span);

/// <summary>
/// Index access: arr[0], matrix[i][j]
/// </summary>
public record IndexAccessExpr(Expr Target, Expr Index, TextSpan Span) : Expr(Span);

/// <summary>
/// Blocking expression: blocking { statements; result_expr }
/// Executes synchronously within an async context.
/// The last expression (if any) is the result value.
/// </summary>
public record BlockingExpr(List<Stmt> Statements, Expr? ResultExpr, TextSpan Span) : Expr(Span);

// =============================================================================
// TYPES - Type annotations
// =============================================================================

/// <summary>Base for type references</summary>
public abstract record TypeRef(TextSpan Span) : AstNode(Span);

/// <summary>Simple type name: int, bool, string, MyRecord</summary>
public record NamedTypeRef(string Name, TextSpan Span) : TypeRef(Span);

/// <summary>Generic type: Result&lt;int, string&gt;, List&lt;User&gt;</summary>
public record GenericTypeRef(string Name, List<TypeRef> TypeArgs, TextSpan Span) : TypeRef(Span);

// =============================================================================
// STATEMENTS - Things that do something but don't produce a value
// =============================================================================

/// <summary>Base for all statements</summary>
public abstract record Stmt(TextSpan Span) : AstNode(Span);

/// <summary>A block of statements: { stmt1; stmt2; }</summary>
public record BlockStmt(List<Stmt> Statements, TextSpan Span) : Stmt(Span);

/// <summary>Return statement: return expr;</summary>
public record ReturnStmt(Expr? Value, TextSpan Span) : Stmt(Span);

/// <summary>Expression used as statement: foo(); (call for side effect)</summary>
public record ExprStmt(Expr Expression, TextSpan Span) : Stmt(Span);

/// <summary>
/// If statement: if cond { then } else { else }
/// ElseBlock is optional (null if no else clause).
/// </summary>
public record IfStmt(Expr Condition, BlockStmt ThenBlock, BlockStmt? ElseBlock, TextSpan Span) : Stmt(Span);

/// <summary>
/// Unified for statement. Three forms:
/// - for item in collection { }  → foreach
/// - for i in start..end { }     → for with range
/// - for condition { }           → while
///
/// When Variable is null, it's a while-style loop.
/// When Variable is set, Iterable is the collection/range.
/// </summary>
public record ForStmt(
    string? Variable,      // Loop variable (null for while-style)
    Expr Iterable,         // Collection, range, or condition
    BlockStmt Body,
    TextSpan Span
) : Stmt(Span);

/// <summary>
/// Variable declaration: let x = 5; or mut x = 5;
/// IsMutable is true if declared with 'mut'.
/// Type is optional (inferred if not specified).
/// </summary>
public record VarDeclStmt(
    bool IsMutable,
    string Name,
    TypeRef? Type,
    Expr Value,
    TextSpan Span
) : Stmt(Span);

/// <summary>
/// Assignment statement: x = expr; (modifies existing variable)
/// </summary>
public record AssignStmt(string Target, Expr Value, TextSpan Span) : Stmt(Span);

/// <summary>
/// Compound assignment: x += expr; x -= expr; etc.
/// Op is the base operator (+, -, *, /, %)
/// </summary>
public record CompoundAssignStmt(string Target, string Op, Expr Value, TextSpan Span) : Stmt(Span);

// =============================================================================
// DECLARATIONS - Top-level program elements
// =============================================================================

/// <summary>Base for top-level declarations (functions, records, etc.)</summary>
public abstract record Decl(TextSpan Span) : AstNode(Span);

/// <summary>Function parameter: name: type</summary>
public record Param(string Name, TypeRef Type, TextSpan Span) : AstNode(Span);

/// <summary>
/// Function modifier: ~blocking, ~log(info), ~timeout(5000)
/// Args is null for simple modifiers, contains expressions for parameterized ones.
/// </summary>
public record Modifier(string Name, List<Expr>? Args, TextSpan Span) : AstNode(Span);

/// <summary>
/// Function declaration: fn name(params) -> returnType ~modifiers { body }
///
/// Can have either:
/// - Block body: fn add(a: int) -> int { return a + 1; }
/// - Expression body: fn add(a: int) -> int => a + 1;
/// </summary>
public record FnDecl(
    string Name,
    List<Param> Params,
    TypeRef ReturnType,
    List<Modifier> Modifiers,  // ~blocking, ~log(info), etc.
    BlockStmt? Body,           // Block body { ... }
    Expr? ExprBody,            // Expression body => expr
    TextSpan Span
) : Decl(Span);

/// <summary>
/// Record field: name: type or name: type = default
/// </summary>
public record RecordField(
    string Name,
    TypeRef Type,
    Expr? DefaultValue,
    TextSpan Span
) : AstNode(Span);

/// <summary>
/// Record declaration: record Name { field1: type; field2: type = default; }
/// </summary>
public record RecordDecl(
    string Name,
    List<RecordField> Fields,
    TextSpan Span
) : Decl(Span);

/// <summary>
/// Class field: name: type or mut name: type = default
/// </summary>
public record ClassField(
    bool IsMutable,
    string Name,
    TypeRef Type,
    Expr? DefaultValue,
    TextSpan Span
) : AstNode(Span);

/// <summary>
/// Class declaration: class Name(params) : Interface1, Interface2 { fields; methods }
/// Primary constructor params become immutable fields.
/// </summary>
public record ClassDecl(
    string Name,
    List<Param> ConstructorParams,  // Primary constructor
    List<string> Interfaces,         // Implemented interfaces
    List<ClassField> Fields,         // Additional fields
    List<FnDecl> Methods,            // Member functions
    TextSpan Span
) : Decl(Span);

/// <summary>
/// Method signature for interfaces: fn name(params) -> returnType;
/// No body - just the contract.
/// </summary>
public record MethodSig(
    string Name,
    List<Param> Params,
    TypeRef ReturnType,
    TextSpan Span
) : AstNode(Span);

/// <summary>
/// Interface declaration: interface Name { method signatures }
/// </summary>
public record InterfaceDecl(
    string Name,
    List<MethodSig> Methods,
    TextSpan Span
) : Decl(Span);

/// <summary>
/// Enum variant: Name or Name { field: type; }
/// Variants can be simple (no data) or carry associated data (like Rust enums).
/// </summary>
public record EnumVariant(
    string Name,
    List<RecordField> Fields,  // Empty for simple variants
    TextSpan Span
) : AstNode(Span);

/// <summary>
/// Enum declaration: enum Name { Variant1; Variant2 { data: type; }; }
/// Tagged unions / algebraic data types.
/// </summary>
public record EnumDecl(
    string Name,
    List<EnumVariant> Variants,
    TextSpan Span
) : Decl(Span);

// =============================================================================
// DEPENDENCY INJECTION - Services, Modules, and DI Configuration
// =============================================================================

/// <summary>
/// Service lifetime for dependency injection.
/// </summary>
public enum ServiceLifetime
{
    Singleton,  // One instance for app lifetime
    Scoped,     // One instance per scope (request)
    Transient   // New instance every time
}

/// <summary>
/// Service declaration: service singleton MyService : IService { fields; methods }
/// </summary>
public record ServiceDecl(
    ServiceLifetime Lifetime,
    string Name,
    List<Param> ConstructorParams,  // Primary constructor (auto-injected dependencies)
    string? Interface,              // Optional interface implementation
    List<ClassField> Fields,        // Additional fields
    List<FnDecl> Methods,           // Service methods
    TextSpan Span
) : Decl(Span);

/// <summary>
/// Binding declaration: bind IService => MyService;
/// </summary>
public record BindingDecl(
    string Interface,
    string Implementation,
    TextSpan Span
) : AstNode(Span);

/// <summary>
/// Provide declaration: provide connection_string = env("DATABASE_URL");
/// </summary>
public record ProvideDecl(
    string Name,
    Expr Value,
    TextSpan Span
) : AstNode(Span);

/// <summary>
/// Module declaration: module Services [extends BaseModule] { bindings; provides; }
/// </summary>
public record ModuleDecl(
    string Name,
    string? Extends,                // Optional base module
    List<BindingDecl> Bindings,
    List<ProvideDecl> Provides,
    TextSpan Span
) : Decl(Span);

/// <summary>
/// App declaration: app ModuleName { fn main(svc: Service) { } }
/// Entry point with DI-injected services.
/// </summary>
public record AppDecl(
    string ModuleName,
    FnDecl MainFn,
    TextSpan Span
) : Decl(Span);

/// <summary>
/// Scope expression: scope { statements }
/// Creates a new DI scope for scoped services.
/// </summary>
public record ScopeExpr(List<Stmt> Statements, TextSpan Span) : Expr(Span);
