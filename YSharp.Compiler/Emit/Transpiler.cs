using System.Text;
using YSharp.Compiler.Ast;

namespace YSharp.Compiler.Emit;

/// <summary>
/// Transpiles Y# AST to C# source code.
///
/// Y# is to C# what TypeScript is to JavaScript:
/// - Stricter types
/// - Cleaner syntax
/// - Domain-focused (HTTP services)
/// - Compiles to the host language
/// </summary>
public class Transpiler(string assemblyName)
{
    private readonly StringBuilder _sb = new();
    private int _indent = 0;
    private bool _usesResult = false;  // Track if Result<T> is used
    private bool _usesDi = false;      // Track if DI features are used
    private bool _usesRoutes = false;  // Track if HTTP routes are used
    private readonly HashSet<string> _typeNames = new();  // Track declared types for instantiation
    private readonly HashSet<string> _asyncFunctions = new();  // Track async functions for auto-await
    private int _tryCounter = 0;  // Counter for temporary variables in ? expressions

    /// <summary>
    /// Returns true if the last Transpile call used DI features.
    /// Builder uses this to add package references.
    /// </summary>
    public bool UsesDependencyInjection => _usesDi;

    /// <summary>
    /// Returns true if the last Transpile call used HTTP routes.
    /// Builder uses this to use Web SDK.
    /// </summary>
    public bool UsesHttpRoutes => _usesRoutes;

    /// <summary>
    /// Transpile declarations to C# source code.
    /// </summary>
    public string Transpile(List<Decl> declarations)
    {
        _sb.Clear();
        _indent = 0;

        // C# requires top-level statements BEFORE type declarations.
        // Order: 1) Result helpers (if needed), 2) main, 3) other fns, 4) types

        var mainFn = declarations.OfType<FnDecl>().FirstOrDefault(f => f.Name == "main");
        var otherFns = declarations.OfType<FnDecl>().Where(f => f.Name != "main");
        var records = declarations.OfType<RecordDecl>();
        var classes = declarations.OfType<ClassDecl>();
        var interfaces = declarations.OfType<InterfaceDecl>();
        var enums = declarations.OfType<EnumDecl>();
        var errors = declarations.OfType<ErrorDecl>();
        var services = declarations.OfType<ServiceDecl>();
        var modules = declarations.OfType<ModuleDecl>();
        var appDecl = declarations.OfType<AppDecl>().FirstOrDefault();
        var routes = declarations.OfType<RouteDecl>();

        // First pass - collect type names, async functions, and check if Result is used
        CollectTypeNames(declarations);
        CollectAsyncFunctions(declarations);
        ScanForResultUsage(declarations);

        // Check if DI features are used
        _usesDi = services.Any() || modules.Any() || appDecl != null;
        _usesRoutes = routes.Any();

        // Emit using statements for DI if needed
        if (_usesDi)
        {
            AppendLine("using Microsoft.Extensions.DependencyInjection;");
            AppendLine("");
        }

        // Emit using statements for HTTP routing if needed
        if (_usesRoutes)
        {
            AppendLine("var builder = WebApplication.CreateBuilder(args);");
            AppendLine("builder.WebHost.UseUrls(\"http://localhost:9900\");");
            AppendLine("var app = builder.Build();");
            AppendLine("");
        }

        // 1. Main function (as top-level statements)
        if (mainFn != null)
        {
            TranspileFunction(mainFn);
        }

        // 2. Other functions
        foreach (var fn in otherFns)
        {
            TranspileFunction(fn);
        }

        // 3. Interfaces (type declarations go last)
        foreach (var iface in interfaces)
        {
            TranspileInterface(iface);
        }

        // 4. Enums (tagged unions)
        foreach (var enm in enums)
        {
            TranspileEnum(enm);
        }

        // 4b. Error types (domain-specific error tagged unions)
        foreach (var err in errors)
        {
            TranspileError(err);
        }

        // 5. Records
        foreach (var rec in records)
        {
            TranspileRecord(rec);
        }

        // 6. Classes
        foreach (var cls in classes)
        {
            TranspileClass(cls);
        }

        // 7. Services (DI)
        foreach (var svc in services)
        {
            TranspileService(svc);
        }

        // 8. Modules (DI configuration)
        foreach (var mod in modules)
        {
            TranspileModule(mod);
        }

        // 9. Routes (HTTP endpoints)
        foreach (var route in routes)
        {
            TranspileRoute(route);
        }

        // Start the web app if routes were defined
        if (_usesRoutes)
        {
            AppendLine("app.Run();");
            AppendLine("");
        }

        // 10. App entry point (if using DI)
        if (appDecl != null)
        {
            TranspileApp(appDecl);
        }

        // 11. Result<T> type if used
        if (_usesResult)
        {
            EmitResultType();
        }

        return _sb.ToString();
    }

    private void CollectTypeNames(List<Decl> declarations)
    {
        // Collect all type names for instantiation detection
        foreach (var decl in declarations)
        {
            switch (decl)
            {
                case RecordDecl rec:
                    _typeNames.Add(rec.Name);
                    break;
                case ClassDecl cls:
                    _typeNames.Add(cls.Name);
                    break;
                case EnumDecl enm:
                    _typeNames.Add(enm.Name);
                    // Also add enum variant names as constructors
                    foreach (var variant in enm.Variants)
                    {
                        _typeNames.Add($"{enm.Name}.{variant.Name}");
                    }
                    break;
                case ErrorDecl err:
                    _typeNames.Add(err.Name);
                    // Also add error variant names as constructors
                    foreach (var variant in err.Variants)
                    {
                        _typeNames.Add($"{err.Name}.{variant.Name}");
                    }
                    break;
                case ServiceDecl svc:
                    _typeNames.Add(svc.Name);
                    break;
            }
        }
    }

    private void CollectAsyncFunctions(List<Decl> declarations)
    {
        // Collect all async function names (functions without ~blocking modifier)
        foreach (var fn in declarations.OfType<FnDecl>())
        {
            if (!IsBlocking(fn) && fn.Name != "main")
            {
                _asyncFunctions.Add(fn.Name);
            }
        }

        // Also collect async methods from classes
        foreach (var cls in declarations.OfType<ClassDecl>())
        {
            foreach (var method in cls.Methods)
            {
                if (!IsBlocking(method))
                {
                    // Store as ClassName.MethodName for method calls
                    _asyncFunctions.Add($"{cls.Name}.{method.Name}");
                }
            }
        }

        // Also collect async methods from services
        foreach (var svc in declarations.OfType<ServiceDecl>())
        {
            foreach (var method in svc.Methods)
            {
                if (!IsBlocking(method))
                {
                    _asyncFunctions.Add($"{svc.Name}.{method.Name}");
                }
            }
        }
    }

    private void ScanForResultUsage(List<Decl> declarations)
    {
        // Check if any function uses Result types or Ok/Err calls
        foreach (var fn in declarations.OfType<FnDecl>())
        {
            if (fn.ReturnType is GenericTypeRef generic && generic.Name == "Result")
            {
                _usesResult = true;
                return;
            }
        }
    }

    private void EmitResultType()
    {
        AppendLine("// Result type for error handling");
        AppendLine("readonly record struct Error(string Code, string Description, ErrorKind Kind)");
        AppendLine("{");
        AppendLine("    public static Error Failure(string description = \"\") => new(\"Failure\", description, ErrorKind.Failure);");
        AppendLine("    public static Error Validation(string description = \"\") => new(\"Validation\", description, ErrorKind.Validation);");
        AppendLine("    public static Error NotFound(string description = \"\") => new(\"NotFound\", description, ErrorKind.NotFound);");
        AppendLine("    public static Error Unexpected(string description = \"\") => new(\"Unexpected\", description, ErrorKind.Unexpected);");
        AppendLine("}");
        AppendLine("");
        AppendLine("enum ErrorKind { Failure, Validation, NotFound, Unexpected }");
        AppendLine("");
        AppendLine("// Single-parameter Result for built-in Error type");
        AppendLine("readonly struct Result<T>");
        AppendLine("{");
        AppendLine("    public T? Value { get; }");
        AppendLine("    public Error? Error { get; }");
        AppendLine("    public bool IsError => Error.HasValue;");
        AppendLine("    public bool IsOk => !IsError;");
        AppendLine("    private Result(T value) { Value = value; Error = null; }");
        AppendLine("    private Result(Error error) { Value = default; Error = error; }");
        AppendLine("    public static implicit operator Result<T>(T value) => new(value);");
        AppendLine("    public static implicit operator Result<T>(Error error) => new(error);");
        AppendLine("    public override string ToString() => IsError ? $\"Error: {Error}\" : $\"Ok: {Value}\";");
        AppendLine("    // Combinators");
        AppendLine("    public Result<U> Map<U>(Func<T, U> fn) => IsError ? Error!.Value : fn(Value!);");
        AppendLine("    public Result<U> Then<U>(Func<T, Result<U>> fn) => IsError ? Error!.Value : fn(Value!);");
        AppendLine("    public T UnwrapOr(T defaultValue) => IsOk ? Value! : defaultValue;");
        AppendLine("    public T UnwrapOrElse(Func<Error, T> fn) => IsOk ? Value! : fn(Error!.Value);");
        AppendLine("}");
        AppendLine("");
        AppendLine("// Two-parameter Result for custom error types");
        AppendLine("readonly struct Result<T, E>");
        AppendLine("{");
        AppendLine("    public T? Value { get; }");
        AppendLine("    public E? Error { get; }");
        AppendLine("    public bool IsError => Error is not null;");
        AppendLine("    public bool IsOk => !IsError;");
        AppendLine("    private Result(T value) { Value = value; Error = default; }");
        AppendLine("    private Result(E error) { Value = default; Error = error; }");
        AppendLine("    public static implicit operator Result<T, E>(T value) => new(value);");
        AppendLine("    public static implicit operator Result<T, E>(E error) => new(error);");
        AppendLine("    public override string ToString() => IsError ? $\"Error: {Error}\" : $\"Ok: {Value}\";");
        AppendLine("    // Combinators");
        AppendLine("    public Result<U, E> Map<U>(Func<T, U> fn) => IsError ? Error! : fn(Value!);");
        AppendLine("    public Result<U, E> Then<U>(Func<T, Result<U, E>> fn) => IsError ? Error! : fn(Value!);");
        AppendLine("    public T UnwrapOr(T defaultValue) => IsOk ? Value! : defaultValue;");
        AppendLine("    public T UnwrapOrElse(Func<E, T> fn) => IsOk ? Value! : fn(Error!);");
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileDeclaration(Decl decl)
    {
        switch (decl)
        {
            case RecordDecl rec:
                TranspileRecord(rec);
                break;
            case FnDecl fn:
                TranspileFunction(fn);
                break;
            default:
                AppendLine($"// TODO: {decl.GetType().Name}");
                break;
        }
    }

    private void TranspileInterface(InterfaceDecl iface)
    {
        AppendLine($"interface {iface.Name}");
        AppendLine("{");
        _indent++;

        foreach (var method in iface.Methods)
        {
            // Interface methods are async by default (matching implementation default)
            var returnType = GetAsyncReturnType(method.ReturnType);
            var parameters = string.Join(", ", method.Params.Select(p =>
                $"{TranspileType(p.Type)} {p.Name}"));
            AppendLine($"{returnType} {Capitalize(method.Name)}({parameters});");
        }

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileEnum(EnumDecl enm)
    {
        // Discriminated union pattern:
        // abstract record EnumName { sealed record Variant1 : EnumName; ... }
        AppendLine($"abstract record {enm.Name}");
        AppendLine("{");
        _indent++;

        // Private constructor to prevent external subclassing
        AppendLine($"private {enm.Name}() {{ }}");
        AppendLine("");

        foreach (var variant in enm.Variants)
        {
            if (variant.Fields.Count == 0)
            {
                // Simple variant: sealed record Pending : Status;
                AppendLine($"public sealed record {variant.Name}() : {enm.Name};");
            }
            else
            {
                // Variant with data: sealed record Active(DateTime Since) : Status;
                var parameters = string.Join(", ", variant.Fields.Select(f =>
                    $"{TranspileType(f.Type)} {Capitalize(f.Name)}"));
                AppendLine($"public sealed record {variant.Name}({parameters}) : {enm.Name};");
            }
        }

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileError(ErrorDecl err)
    {
        // Error types are discriminated unions similar to enums
        // but semantically represent domain errors
        AppendLine($"abstract record {err.Name}");
        AppendLine("{");
        _indent++;

        // Private constructor to prevent external subclassing
        AppendLine($"private {err.Name}() {{ }}");
        AppendLine("");

        foreach (var variant in err.Variants)
        {
            if (variant.Fields.Count == 0)
            {
                // Simple variant: sealed record NotFound : OrderError;
                AppendLine($"public sealed record {variant.Name}() : {err.Name};");
            }
            else
            {
                // Variant with data: sealed record ValidationFailed(string Message) : OrderError;
                var parameters = string.Join(", ", variant.Fields.Select(f =>
                    $"{TranspileType(f.Type)} {Capitalize(f.Name)}"));
                AppendLine($"public sealed record {variant.Name}({parameters}) : {err.Name};");
            }
        }

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileRecord(RecordDecl rec)
    {
        // Separate fields with and without defaults
        var fieldsWithoutDefaults = rec.Fields.Where(f => f.DefaultValue == null).ToList();
        var fieldsWithDefaults = rec.Fields.Where(f => f.DefaultValue != null).ToList();

        // Primary constructor parameters (fields without defaults)
        var primaryParams = string.Join(", ", fieldsWithoutDefaults.Select(f =>
            $"{TranspileType(f.Type)} {Capitalize(f.Name)}"));

        if (fieldsWithDefaults.Count == 0)
        {
            // Simple record with just primary constructor
            AppendLine($"record {rec.Name}({primaryParams});");
        }
        else
        {
            // Record with default values needs a body
            AppendLine($"record {rec.Name}({primaryParams})");
            AppendLine("{");
            _indent++;

            foreach (var field in fieldsWithDefaults)
            {
                Append($"public {TranspileType(field.Type)} {Capitalize(field.Name)} {{ get; init; }} = ");
                TranspileExpression(field.DefaultValue!);
                _sb.AppendLine(";");  // No indent - continues from expression
            }

            _indent--;
            AppendLine("}");
        }

        AppendLine("");
    }

    private void TranspileClass(ClassDecl cls)
    {
        // Build primary constructor parameters
        var ctorParams = string.Join(", ", cls.ConstructorParams.Select(p =>
            $"{TranspileType(p.Type)} {p.Name}"));

        // Build interface list
        var interfaces = cls.Interfaces.Count > 0
            ? " : " + string.Join(", ", cls.Interfaces)
            : "";

        // Class declaration with primary constructor and interfaces
        if (ctorParams.Length > 0)
        {
            AppendLine($"class {cls.Name}({ctorParams}){interfaces}");
        }
        else
        {
            AppendLine($"class {cls.Name}{interfaces}");
        }
        AppendLine("{");
        _indent++;

        // Fields
        foreach (var field in cls.Fields)
        {
            var modifier = field.IsMutable ? "" : "readonly ";
            Append($"public {modifier}{TranspileType(field.Type)} {Capitalize(field.Name)}");
            if (field.DefaultValue != null)
            {
                _sb.Append(" = ");
                TranspileExpression(field.DefaultValue);
            }
            _sb.AppendLine(";");
        }

        if (cls.Fields.Count > 0 && cls.Methods.Count > 0)
        {
            AppendLine("");
        }

        // Methods
        foreach (var method in cls.Methods)
        {
            TranspileMethod(method);
        }

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileService(ServiceDecl svc)
    {
        // Build constructor parameters (dependencies)
        var ctorParams = string.Join(", ", svc.ConstructorParams.Select(p =>
            $"{TranspileType(p.Type)} {p.Name}"));

        // Interface implementation
        var iface = svc.Interface != null ? $" : {svc.Interface}" : "";

        // Class declaration with primary constructor
        if (ctorParams.Length > 0)
        {
            AppendLine($"class {svc.Name}({ctorParams}){iface}");
        }
        else
        {
            AppendLine($"class {svc.Name}{iface}");
        }
        AppendLine("{");
        _indent++;

        // Fields
        foreach (var field in svc.Fields)
        {
            var modifier = field.IsMutable ? "" : "readonly ";
            Append($"public {modifier}{TranspileType(field.Type)} {Capitalize(field.Name)}");
            if (field.DefaultValue != null)
            {
                _sb.Append(" = ");
                TranspileExpression(field.DefaultValue);
            }
            _sb.AppendLine(";");
        }

        if (svc.Fields.Count > 0 && svc.Methods.Count > 0)
        {
            AppendLine("");
        }

        // Methods
        foreach (var method in svc.Methods)
        {
            TranspileMethod(method);
        }

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileModule(ModuleDecl mod)
    {
        // Generate an extension method for IServiceCollection
        AppendLine($"static class {mod.Name}Module");
        AppendLine("{");
        _indent++;

        var extends = mod.Extends != null ? $"{mod.Extends}Module.Configure(services);\n        " : "";

        AppendLine($"public static IServiceCollection Configure(this IServiceCollection services)");
        AppendLine("{");
        _indent++;

        if (mod.Extends != null)
        {
            AppendLine($"{mod.Extends}Module.Configure(services);");
        }

        // Emit bindings
        foreach (var binding in mod.Bindings)
        {
            // For now, assume scoped - we'd need to track lifetime from ServiceDecl
            AppendLine($"services.AddScoped<{binding.Interface}, {binding.Implementation}>();");
        }

        // Emit provides (configuration values)
        foreach (var provide in mod.Provides)
        {
            Append($"services.AddSingleton(\"{provide.Name}\", ");
            TranspileExpression(provide.Value);
            _sb.AppendLine(");");
        }

        AppendLine("return services;");
        _indent--;
        AppendLine("}");

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileRoute(RouteDecl route)
    {
        foreach (var endpoint in route.Endpoints)
        {
            var method = endpoint.Method switch
            {
                Ast.HttpMethod.Get => "MapGet",
                Ast.HttpMethod.Post => "MapPost",
                Ast.HttpMethod.Put => "MapPut",
                Ast.HttpMethod.Delete => "MapDelete",
                _ => "MapGet"
            };

            var fullPath = (route.BasePath.TrimEnd('/') + "/" + endpoint.Path.TrimStart('/')).TrimEnd('/');
            if (string.IsNullOrEmpty(fullPath)) fullPath = "/";
            fullPath = fullPath.Replace("//", "/");

            AppendLine($"app.{method}(\"{fullPath}\", {Capitalize(endpoint.Handler)});");
        }
        AppendLine("");
    }

    private void TranspileConcurrent(ConcurrentExpr concurrent)
    {
        // For each statement, we need to:
        // 1. Start async tasks without awaiting
        // 2. Run Task.WhenAll to wait for all
        // 3. Extract results into variables
        //
        // Only async function calls are treated as concurrent tasks.
        // Non-async expressions are evaluated normally.

        var tasks = new List<(string varName, string taskName)>();
        var syncVars = new List<(string varName, Expr value)>();
        var taskCounter = 0;

        // First pass: categorize statements as async or sync
        foreach (var stmt in concurrent.Statements)
        {
            if (stmt is VarDeclStmt varDecl)
            {
                // Check if the value is an async call
                if (IsAsyncCall(varDecl.Value))
                {
                    var varName = varDecl.Name;
                    var taskName = $"_task{taskCounter++}";
                    tasks.Add((varName, taskName));

                    // Start the task without awaiting
                    Append($"var {taskName} = ");
                    TranspileExpressionNoAwait(varDecl.Value);
                    _sb.AppendLine(";");
                }
                else
                {
                    // Sync value - just transpile normally
                    syncVars.Add((varDecl.Name, varDecl.Value));
                }
            }
            else
            {
                // Non-var-decl statements, transpile normally
                TranspileStatement(stmt);
            }
        }

        // Run all async tasks concurrently
        if (tasks.Count > 0)
        {
            AppendLine($"await Task.WhenAll({string.Join(", ", tasks.Select(t => t.taskName))});");

            // Extract results into variables
            foreach (var (varName, taskName) in tasks)
            {
                AppendLine($"var {varName} = {taskName}.Result;");
            }
        }

        // Emit sync variable declarations
        foreach (var (varName, value) in syncVars)
        {
            Append($"var {varName} = ");
            TranspileExpression(value);
            _sb.AppendLine(";");
        }
    }

    private bool IsAsyncCall(Expr expr)
    {
        // Check if the expression is a call to an async function
        if (expr is CallExpr call)
        {
            if (call.Target is IdentifierExpr id)
            {
                return _asyncFunctions.Contains(id.Name);
            }
            if (call.Target is MemberAccessExpr member && member.Target is IdentifierExpr targetId)
            {
                // Check for method calls like service.method()
                return _asyncFunctions.Contains($"{targetId.Name}.{member.Member}");
            }
        }
        return false;
    }

    private void TranspileBlockingStmt(BlockingExpr blocking)
    {
        // Blocking block as a statement - execute statements synchronously
        foreach (var stmt in blocking.Statements)
        {
            TranspileStatement(stmt);
        }
    }

    private void TranspileExpressionNoAwait(Expr expr)
    {
        // Transpile expression without adding await for top-level calls
        // This is used in concurrent blocks where we want to start tasks
        switch (expr)
        {
            case CallExpr call:
                // For calls, don't await - we want the Task
                TranspileCallNoAwait(call);
                break;
            default:
                // For other expressions, just transpile normally
                TranspileExpression(expr);
                break;
        }
    }

    private void TranspileCallNoAwait(CallExpr call)
    {
        // Similar to TranspileCall but doesn't add await
        switch (call.Target)
        {
            case IdentifierExpr id:
                var typeArgs = call.TypeArgs.Count > 0
                    ? $"<{string.Join(", ", call.TypeArgs.Select(TranspileType))}>"
                    : "";
                _sb.Append($"{Capitalize(id.Name)}{typeArgs}");
                _sb.Append("(");
                TranspileArgs(call.Args);
                _sb.Append(")");
                return;

            case MemberAccessExpr member:
                TranspileExpression(member.Target);
                _sb.Append($".{Capitalize(member.Member)}(");
                TranspileArgs(call.Args);
                _sb.Append(")");
                return;

            default:
                TranspileExpression(call.Target);
                _sb.Append("(");
                TranspileArgs(call.Args);
                _sb.Append(")");
                break;
        }
    }

    private void TranspileApp(AppDecl app)
    {
        // Generate a Main method that sets up DI
        AppendLine("// DI Entry Point");
        AppendLine("static class Program");
        AppendLine("{");
        _indent++;

        AppendLine("public static async Task Main(string[] args)");
        AppendLine("{");
        _indent++;

        AppendLine("var services = new ServiceCollection();");
        AppendLine($"{app.ModuleName}Module.Configure(services);");
        AppendLine("var provider = services.BuildServiceProvider();");
        AppendLine("");

        // Resolve injected parameters and call main
        var mainFn = app.MainFn;
        if (mainFn.Params.Count > 0)
        {
            foreach (var param in mainFn.Params)
            {
                AppendLine($"var {param.Name} = provider.GetRequiredService<{TranspileType(param.Type)}>();");
            }
            AppendLine("");
        }

        // Call the main function body
        if (mainFn.Body != null)
        {
            foreach (var stmt in mainFn.Body.Statements)
            {
                TranspileStatement(stmt);
            }
        }

        _indent--;
        AppendLine("}");

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileMethod(FnDecl method)
    {
        // Check if method is blocking (synchronous) or async (default)
        var isBlocking = IsBlocking(method);
        var parameters = string.Join(", ", method.Params.Select(p =>
            $"{TranspileType(p.Type)} {p.Name}"));

        // Generic type parameters
        var typeParams = method.TypeParams.Count > 0
            ? $"<{string.Join(", ", method.TypeParams)}>"
            : "";

        if (isBlocking)
        {
            // Blocking method: synchronous
            var returnType = TranspileType(method.ReturnType);
            AppendLine($"public {returnType} {Capitalize(method.Name)}{typeParams}({parameters})");
        }
        else
        {
            // Async method: default
            var returnType = GetAsyncReturnType(method.ReturnType);
            AppendLine($"public async {returnType} {Capitalize(method.Name)}{typeParams}({parameters})");
        }

        if (method.Body != null)
        {
            AppendLine("{");
            _indent++;
            foreach (var stmt in method.Body.Statements)
            {
                TranspileStatement(stmt);
            }
            _indent--;
            AppendLine("}");
        }
        else if (method.ExprBody != null)
        {
            Append("    => ");
            TranspileExpression(method.ExprBody);
            _sb.AppendLine(";");
        }

        AppendLine("");
    }

    private void TranspileFunction(FnDecl fn)
    {
        // For main, use top-level statements (simpler)
        if (fn.Name == "main")
        {
            if (fn.Body != null)
            {
                foreach (var stmt in fn.Body.Statements)
                {
                    TranspileStatement(stmt);
                }
            }
            else if (fn.ExprBody != null)
            {
                Append("");
                TranspileExpression(fn.ExprBody);
                AppendLine(";");
            }
            return;
        }

        // Check if function is blocking (synchronous) or async (default)
        var isBlocking = IsBlocking(fn);
        var parameters = string.Join(", ", fn.Params.Select(p =>
            $"{TranspileType(p.Type)} {p.Name}"));

        // Generic type parameters
        var typeParams = fn.TypeParams.Count > 0
            ? $"<{string.Join(", ", fn.TypeParams)}>"
            : "";

        if (isBlocking)
        {
            // Blocking function: synchronous
            var returnType = TranspileType(fn.ReturnType);
            AppendLine($"static {returnType} {Capitalize(fn.Name)}{typeParams}({parameters})");
        }
        else
        {
            // Async function: default
            var returnType = GetAsyncReturnType(fn.ReturnType);
            AppendLine($"static async {returnType} {Capitalize(fn.Name)}{typeParams}({parameters})");
        }

        if (fn.Body != null)
        {
            AppendLine("{");
            _indent++;
            foreach (var stmt in fn.Body.Statements)
            {
                TranspileStatement(stmt);
            }
            _indent--;
            AppendLine("}");
        }
        else if (fn.ExprBody != null)
        {
            Append("    => ");
            TranspileExpression(fn.ExprBody);
            AppendLine(";");
        }

        AppendLine("");
    }

    private void TranspileStatement(Stmt stmt)
    {
        switch (stmt)
        {
            case ExprStmt expr:
                // Handle standalone try expression: foo()?;
                if (expr.Expression is TryExpr tryExpr)
                {
                    TranspileTryStmt(tryExpr);
                }
                // Handle concurrent expression specially - it emits full statements
                else if (expr.Expression is ConcurrentExpr concurrentExpr)
                {
                    TranspileConcurrent(concurrentExpr);
                }
                // Handle blocking expression specially - it's executed for side effects
                else if (expr.Expression is BlockingExpr blockingExpr)
                {
                    TranspileBlockingStmt(blockingExpr);
                }
                else
                {
                    Append("");
                    TranspileExpression(expr.Expression);
                    _sb.AppendLine(";");
                }
                break;

            case ReturnStmt ret:
                Append("return");
                if (ret.Value != null)
                {
                    _sb.Append(" ");
                    TranspileExpression(ret.Value);
                }
                _sb.AppendLine(";");
                break;

            case BreakStmt:
                AppendLine("break;");
                break;

            case ContinueStmt:
                AppendLine("continue;");
                break;

            case IfStmt ifStmt:
                Append("if (");
                TranspileExpression(ifStmt.Condition);
                _sb.AppendLine(")");
                AppendLine("{");
                _indent++;
                foreach (var s in ifStmt.ThenBlock.Statements)
                    TranspileStatement(s);
                _indent--;
                AppendLine("}");

                if (ifStmt.ElseBlock != null)
                {
                    AppendLine("else");
                    AppendLine("{");
                    _indent++;
                    foreach (var s in ifStmt.ElseBlock.Statements)
                        TranspileStatement(s);
                    _indent--;
                    AppendLine("}");
                }
                break;

            case VarDeclStmt varDecl:
                // Handle ? propagation operator specially
                if (varDecl.Value is TryExpr varTryExpr)
                {
                    TranspileTryVarDecl(varDecl, varTryExpr);
                }
                else
                {
                    Append(varDecl.IsMutable ? "" : "");  // C# doesn't have immutable locals by default
                    if (varDecl.Type != null)
                    {
                        _sb.Append($"{TranspileType(varDecl.Type)} ");
                    }
                    else
                    {
                        _sb.Append("var ");
                    }
                    _sb.Append($"{varDecl.Name} = ");
                    TranspileExpression(varDecl.Value);
                    _sb.AppendLine(";");
                }
                break;

            case ForStmt forStmt:
                TranspileFor(forStmt);
                break;

            case AssignStmt assign:
                Append($"{assign.Target} = ");
                TranspileExpression(assign.Value);
                _sb.AppendLine(";");
                break;

            case CompoundAssignStmt compound:
                Append($"{compound.Target} {compound.Op}= ");
                TranspileExpression(compound.Value);
                _sb.AppendLine(";");
                break;

            default:
                AppendLine($"// TODO: {stmt.GetType().Name}");
                break;
        }
    }

    private void TranspileFor(ForStmt forStmt)
    {
        if (forStmt.Variable == null)
        {
            // While-style: for condition { } → while (condition) { }
            Append("while (");
            TranspileExpression(forStmt.Iterable);
            _sb.AppendLine(")");
        }
        else if (forStmt.Iterable is RangeExpr range)
        {
            // Range-style: for i in 0..10 { } → for (var i = 0; i < 10; i++) { }
            Append($"for (var {forStmt.Variable} = ");
            TranspileExpression(range.Start);
            _sb.Append($"; {forStmt.Variable} < ");
            TranspileExpression(range.End);
            _sb.AppendLine($"; {forStmt.Variable}++)");
        }
        else
        {
            // Foreach-style: for item in collection { } → foreach (var item in collection) { }
            Append($"foreach (var {forStmt.Variable} in ");
            TranspileExpression(forStmt.Iterable);
            _sb.AppendLine(")");
        }

        AppendLine("{");
        _indent++;
        foreach (var s in forStmt.Body.Statements)
            TranspileStatement(s);
        _indent--;
        AppendLine("}");
    }

    /// <summary>
    /// Transpile let x = expr?; to early-return pattern:
    /// var _try0 = expr; if (_try0.IsError) return _try0.Error; var x = _try0.Value;
    /// </summary>
    private void TranspileTryVarDecl(VarDeclStmt varDecl, TryExpr tryExpr)
    {
        var tempVar = $"_try{_tryCounter++}";

        // var _tryN = expr;
        Append($"var {tempVar} = ");
        TranspileExpression(tryExpr.Operand);
        _sb.AppendLine(";");

        // if (_tryN.IsError) return _tryN.Error.Value;
        AppendLine($"if ({tempVar}.IsError) return {tempVar}.Error.Value;");

        // var varName = _tryN.Value;
        if (varDecl.Type != null)
        {
            AppendLine($"{TranspileType(varDecl.Type)} {varDecl.Name} = {tempVar}.Value;");
        }
        else
        {
            AppendLine($"var {varDecl.Name} = {tempVar}.Value;");
        }
    }

    /// <summary>
    /// Transpile standalone expr?; to early-return pattern (discards value):
    /// var _try0 = expr; if (_try0.IsError) return _try0.Error.Value;
    /// </summary>
    private void TranspileTryStmt(TryExpr tryExpr)
    {
        var tempVar = $"_try{_tryCounter++}";

        // var _tryN = expr;
        Append($"var {tempVar} = ");
        TranspileExpression(tryExpr.Operand);
        _sb.AppendLine(";");

        // if (_tryN.IsError) return _tryN.Error.Value;
        AppendLine($"if ({tempVar}.IsError) return {tempVar}.Error.Value;");
    }

    private void TranspileExpression(Expr expr)
    {
        switch (expr)
        {
            case StringLiteralExpr str:
                _sb.Append($"\"{Escape(str.Value)}\"");
                break;

            case InterpolatedStringExpr interp:
                TranspileInterpolatedString(interp);
                break;

            case IntLiteralExpr num:
                _sb.Append(num.Value);
                break;

            case DoubleLiteralExpr dbl:
                _sb.Append(dbl.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;

            case BoolLiteralExpr b:
                _sb.Append(b.Value ? "true" : "false");
                break;

            case NoneExpr:
                _sb.Append("null");
                break;

            case SomeExpr some:
                // Some(x) just unwraps to x in C# nullable semantics
                TranspileExpression(some.Value);
                break;

            case IdentifierExpr id:
                _sb.Append(id.Name);
                break;

            case BinaryExpr bin:
                _sb.Append("(");
                TranspileExpression(bin.Left);
                _sb.Append($" {bin.Op} ");
                TranspileExpression(bin.Right);
                _sb.Append(")");
                break;

            case UnaryExpr unary:
                _sb.Append($"({unary.Op}");
                TranspileExpression(unary.Operand);
                _sb.Append(")");
                break;

            case TryExpr tryExpr:
                // The ? operator is best used in let x = expr?; form
                // In expression position, we just output .Value (assuming success)
                TranspileExpression(tryExpr.Operand);
                _sb.Append(".Value");
                break;

            case LambdaExpr lambda:
                TranspileLambda(lambda);
                break;

            case CallExpr call:
                TranspileCall(call);
                break;

            case MemberAccessExpr member:
                TranspileExpression(member.Target);
                _sb.Append($".{Capitalize(member.Member)}");
                break;

            case ThisExpr:
                _sb.Append("this");
                break;

            case WildcardExpr:
                _sb.Append("_");
                break;

            case MatchExpr matchExpr:
                TranspileMatch(matchExpr);
                break;

            case RangeExpr range:
                // Range as expression: Enumerable.Range(start, end - start)
                _sb.Append("Enumerable.Range(");
                TranspileExpression(range.Start);
                _sb.Append(", ");
                TranspileExpression(range.End);
                _sb.Append(" - ");
                TranspileExpression(range.Start);
                _sb.Append(")");
                break;

            case ArrayExpr array:
                // Array literal: [1, 2, 3] → new[] { 1, 2, 3 }
                _sb.Append("new[] { ");
                for (int i = 0; i < array.Elements.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    TranspileExpression(array.Elements[i]);
                }
                _sb.Append(" }");
                break;

            case IndexAccessExpr indexAccess:
                // Index access: arr[0] → arr[0]
                TranspileExpression(indexAccess.Target);
                _sb.Append("[");
                TranspileExpression(indexAccess.Index);
                _sb.Append("]");
                break;

            case BlockingExpr blockingExpr:
                // Blocking expression: executes synchronously
                // Transpile as an immediately-invoked action/func
                _sb.AppendLine("((Action)(() => {");
                _indent++;
                foreach (var stmt in blockingExpr.Statements)
                {
                    TranspileStatement(stmt);
                }
                _indent--;
                Append("}))()");
                break;

            case ConcurrentExpr concurrentExpr:
                // Concurrent expression: runs statements in parallel using Task.WhenAll
                TranspileConcurrent(concurrentExpr);
                break;

            case WithExpr withExpr:
                // With expression: record with { field = value }
                TranspileExpression(withExpr.Base);
                _sb.Append(" with { ");
                for (int i = 0; i < withExpr.Updates.Count; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    var (name, value) = withExpr.Updates[i];
                    _sb.Append($"{Capitalize(name)} = ");
                    TranspileExpression(value);
                }
                _sb.Append(" }");
                break;

            case ScopeExpr scopeExpr:
                // Scope expression: creates a new DI scope
                // Transpile as using block with service scope
                _sb.AppendLine("((Func<Task>(async () => {");
                _indent++;
                AppendLine("using var _scope = provider.CreateScope();");
                AppendLine("var scopedProvider = _scope.ServiceProvider;");
                foreach (var stmt in scopeExpr.Statements)
                {
                    TranspileStatement(stmt);
                }
                _indent--;
                Append("}))()");
                break;

            default:
                _sb.Append($"/* TODO: {expr.GetType().Name} */");
                break;
        }
    }

    private void TranspileLambda(LambdaExpr lambda)
    {
        if (lambda.Parameters.Count == 1)
        {
            // Single param: x => expr
            _sb.Append($"{lambda.Parameters[0]} => ");
        }
        else
        {
            // Multiple params: (x, y) => expr
            _sb.Append($"({string.Join(", ", lambda.Parameters)}) => ");
        }
        TranspileExpression(lambda.Body);
    }

    private void TranspileInterpolatedString(InterpolatedStringExpr interp)
    {
        _sb.Append("$\"");
        foreach (var part in interp.Parts)
        {
            switch (part)
            {
                case InterpolatedText text:
                    _sb.Append(Escape(text.Text));
                    break;
                case InterpolatedExpr expr:
                    _sb.Append("{");
                    TranspileExpression(expr.Expression);
                    _sb.Append("}");
                    break;
            }
        }
        _sb.Append("\"");
    }

    private void TranspileMatch(MatchExpr match)
    {
        // C# switch expression: value switch { pattern => result, ... }
        TranspileExpression(match.Value);
        _sb.Append(" switch { ");
        for (int i = 0; i < match.Arms.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            TranspileMatchArm(match.Arms[i]);
        }
        _sb.Append(" }");
    }

    private void TranspileMatchArm(MatchArm arm)
    {
        TranspileExpression(arm.Pattern);
        _sb.Append(" => ");
        TranspileExpression(arm.Result);
    }

    private void TranspileCall(CallExpr call)
    {
        // Format type arguments if present
        var typeArgs = call.TypeArgs.Count > 0
            ? $"<{string.Join(", ", call.TypeArgs.Select(TranspileType))}>"
            : "";

        // Handle built-ins
        if (call.Target is IdentifierExpr id)
        {
            switch (id.Name)
            {
                case "print":
                    _sb.Append("Console.WriteLine(");
                    TranspileArgs(call.Args);
                    _sb.Append(")");
                    return;
                default:
                    // Check if this is a type constructor (needs 'new')
                    if (_typeNames.Contains(id.Name))
                    {
                        _sb.Append($"new {id.Name}{typeArgs}(");
                        TranspileArgs(call.Args);
                        _sb.Append(")");
                        return;
                    }
                    // User-defined function - check if async and add await
                    if (_asyncFunctions.Contains(id.Name))
                    {
                        _sb.Append("await ");
                    }
                    _sb.Append($"{Capitalize(id.Name)}{typeArgs}");
                    _sb.Append("(");
                    TranspileArgs(call.Args);
                    _sb.Append(")");
                    return;
            }
        }

        // Check for enum variant constructor: Status.Pending()
        if (call.Target is MemberAccessExpr member && member.Target is IdentifierExpr enumName)
        {
            var fullName = $"{enumName.Name}.{member.Member}";
            if (_typeNames.Contains(fullName))
            {
                _sb.Append($"new {enumName.Name}.{member.Member}(");
                TranspileArgs(call.Args);
                _sb.Append(")");
                return;
            }

            // Built-in Error constructor: Error.Validation(), Error.NotFound(), etc.
            // These are not async methods, don't await them
            if (enumName.Name == "Error")
            {
                _sb.Append($"Error.{member.Member}(");
                TranspileArgs(call.Args);
                _sb.Append(")");
                return;
            }
        }

        // Method call on object: obj.method(args) - capitalize method name
        // Only await if the target is an identifier (service instance).
        // Don't await chained method calls like result.Map(...) or ParseInt().Then(...)
        if (call.Target is MemberAccessExpr methodCall)
        {
            // Only await if target is an identifier (like service.method())
            // This excludes chained calls like result.Map() or ParseInt().Then()
            if (methodCall.Target is IdentifierExpr targetId && _asyncFunctions.Contains($"{targetId.Name}"))
            {
                _sb.Append("await ");
            }
            TranspileExpression(methodCall.Target);
            _sb.Append($".{Capitalize(methodCall.Member)}(");
            TranspileArgs(call.Args);
            _sb.Append(")");
            return;
        }

        // Other complex target
        TranspileExpression(call.Target);
        _sb.Append("(");
        TranspileArgs(call.Args);
        _sb.Append(")");
    }

    private void TranspileArgs(List<Expr> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            TranspileExpression(args[i]);
        }
    }

    private static string TranspileType(TypeRef typeRef)
    {
        return typeRef switch
        {
            NamedTypeRef named => named.Name switch
            {
                "void" => "void",
                "int" => "int",
                "long" => "long",
                "float" => "float",
                "double" => "double",
                "bool" => "bool",
                "string" => "string",
                _ => named.Name
            },
            GenericTypeRef generic => $"{generic.Name}<{string.Join(", ", generic.TypeArgs.Select(TranspileType))}>",
            OptionalTypeRef optional => $"{TranspileType(optional.Inner)}?",
            _ => "object"
        };
    }

    private void Append(string text)
    {
        _sb.Append(new string(' ', _indent * 4));
        _sb.Append(text);
    }

    private void AppendLine(string text)
    {
        _sb.Append(new string(' ', _indent * 4));
        _sb.AppendLine(text);
    }

    /// <summary>
    /// Check if a function has the ~blocking modifier.
    /// Blocking functions are synchronous; all others are async by default.
    /// </summary>
    private static bool IsBlocking(FnDecl fn) =>
        fn.Modifiers.Any(m => m.Name == "blocking");

    /// <summary>
    /// Get the async return type for a function.
    /// void -> Task, T -> Task&lt;T&gt;
    /// </summary>
    private string GetAsyncReturnType(TypeRef returnType)
    {
        var baseType = TranspileType(returnType);
        return baseType == "void" ? "Task" : $"Task<{baseType}>";
    }

    private static string Capitalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Convert snake_case to PascalCase: order_id -> OrderId
        if (name.Contains('_'))
        {
            return string.Concat(name.Split('_')
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => char.ToUpper(s[0]) + s[1..]));
        }

        return char.ToUpper(name[0]) + name[1..];
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\n", "\\n")
         .Replace("\r", "\\r");
}
