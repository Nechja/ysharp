using System.Text;
using YSharp.Core.Ast;

namespace YSharp.Core.Emit;

public partial class Transpiler
{
    private void TranspileDeclaration(Decl decl)
    {
        switch (decl)
        {
            case FnDecl fn:
                TranspileFunction(fn);
                break;
            case RecordDecl rec:
                TranspileRecord(rec);
                break;
            case ClassDecl cls:
                TranspileClass(cls);
                break;
            case InterfaceDecl iface:
                TranspileInterface(iface);
                break;
            case EnumDecl enm:
                TranspileEnum(enm);
                break;
            case ErrorDecl err:
                TranspileError(err);
                break;
            case ServiceDecl svc:
                TranspileService(svc);
                break;
            case ModuleDecl mod:
                TranspileModule(mod);
                break;
            case RouteDecl route:
                TranspileRoute(route);
                break;
            case AppDecl app:
                TranspileApp(app);
                break;
            case ModifierDecl:
            case BindingDecl:
            case ProvideDecl:
                break;
        }
    }

    private void TranspileFunction(FnDecl fn)
    {
        if (fn.Name == "main")
        {
            TranspileMainFunction(fn);
            return;
        }

        var isBlocking = IsBlocking(fn);
        var parameters = string.Join(", ", fn.Params.Select(p => $"{TranspileType(p.Type)} {EscapeIdent(p.Name)}"));
        var typeParams = fn.TypeParams.Count > 0 ? $"<{string.Join(", ", fn.TypeParams)}>" : "";

        if (isBlocking)
        {
            var returnType = TranspileType(fn.ReturnType);
            AppendLine($"static {returnType} {Capitalize(fn.Name)}{typeParams}({parameters})");
        }
        else
        {
            var returnType = GetReturnType(fn.ReturnType);
            AppendLine($"static async {returnType} {Capitalize(fn.Name)}{typeParams}({parameters})");
        }

        TranspileFunctionBody(fn);
        AppendLine("");
    }

    private void TranspileMainFunction(FnDecl fn)
    {
        if (fn.Body is not null)
        {
            foreach (var stmt in fn.Body.Statements)
                TranspileStatement(stmt);
        }
        else if (fn.ExprBody is not null)
        {
            Append("");
            TranspileExpression(fn.ExprBody);
            AppendLine(";");
        }
    }

    private void TranspileMethod(FnDecl method)
    {
        var isBlocking = IsBlocking(method);
        var parameters = string.Join(", ", method.Params.Select(p => $"{TranspileType(p.Type)} {EscapeIdent(p.Name)}"));
        var typeParams = method.TypeParams.Count > 0 ? $"<{string.Join(", ", method.TypeParams)}>" : "";

        if (isBlocking)
        {
            var returnType = TranspileType(method.ReturnType);
            AppendLine($"public {returnType} {Capitalize(method.Name)}{typeParams}({parameters})");
        }
        else
        {
            var returnType = GetReturnType(method.ReturnType);
            AppendLine($"public async {returnType} {Capitalize(method.Name)}{typeParams}({parameters})");
        }

        TranspileFunctionBody(method);
        AppendLine("");
    }

    private void TranspileFunctionBody(FnDecl fn)
    {
        if (fn.Body is not null)
        {
            AppendLine("{");
            _indent++;
            foreach (var stmt in fn.Body.Statements)
                TranspileStatement(stmt);
            _indent--;
            AppendLine("}");
        }
        else if (fn.ExprBody is not null)
        {
            Append("    => ");
            TranspileExpression(fn.ExprBody);
            _sb.AppendLine(";");
        }
    }

    private void TranspileInterface(InterfaceDecl iface)
    {
        AppendLine($"interface {iface.Name}");
        AppendLine("{");
        _indent++;

        foreach (var method in iface.Methods)
        {
            var isBlocking = method.Modifiers.Any(m => m.Name == "blocking");
            var returnType = isBlocking ? TranspileType(method.ReturnType) : GetReturnType(method.ReturnType);
            var parameters = string.Join(", ", method.Params.Select(p => $"{TranspileType(p.Type)} {EscapeIdent(p.Name)}"));
            AppendLine($"{returnType} {Capitalize(method.Name)}({parameters});");
        }

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileEnum(EnumDecl enm)
    {
        AppendLine($"abstract record {enm.Name}");
        AppendLine("{");
        _indent++;

        AppendLine($"private {enm.Name}() {{ }}");
        AppendLine("");

        foreach (var variant in enm.Variants)
            TranspileEnumVariant(enm.Name, variant);

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileError(ErrorDecl err)
    {
        AppendLine($"abstract record {err.Name}");
        AppendLine("{");
        _indent++;

        AppendLine($"private {err.Name}() {{ }}");
        AppendLine("");

        foreach (var variant in err.Variants)
            TranspileEnumVariant(err.Name, variant);

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileEnumVariant(string parentName, EnumVariant variant)
    {
        if (variant.Fields.Count == 0)
        {
            AppendLine($"public sealed record {variant.Name}() : {parentName};");
        }
        else
        {
            var parameters = string.Join(", ", variant.Fields.Select(f => $"{TranspileType(f.Type)} {Capitalize(f.Name)}"));
            AppendLine($"public sealed record {variant.Name}({parameters}) : {parentName};");
        }
    }

    private void TranspileRecord(RecordDecl rec)
    {
        var fieldsWithoutDefaults = rec.Fields.Where(f => f.DefaultValue is null).ToList();
        var fieldsWithDefaults = rec.Fields.Where(f => f.DefaultValue is not null).ToList();

        var primaryParams = string.Join(", ", fieldsWithoutDefaults.Select(f => $"{TranspileType(f.Type)} {Capitalize(f.Name)}"));

        if (fieldsWithDefaults.Count == 0)
        {
            AppendLine($"record {rec.Name}({primaryParams});");
        }
        else
        {
            AppendLine($"record {rec.Name}({primaryParams})");
            AppendLine("{");
            _indent++;

            foreach (var field in fieldsWithDefaults)
            {
                Append($"public {TranspileType(field.Type)} {Capitalize(field.Name)} {{ get; init; }} = ");
                TranspileExpression(field.DefaultValue!);
                _sb.AppendLine(";");
            }

            _indent--;
            AppendLine("}");
        }

        AppendLine("");
    }

    private void TranspileClass(ClassDecl cls)
    {
        var ctorParams = string.Join(", ", cls.ConstructorParams.Select(p => $"{TranspileType(p.Type)} {EscapeIdent(p.Name)}"));
        var interfaces = cls.Interfaces.Count > 0 ? " : " + string.Join(", ", cls.Interfaces) : "";

        AppendLine(ctorParams.Length > 0 ? $"class {cls.Name}({ctorParams}){interfaces}" : $"class {cls.Name}{interfaces}");
        AppendLine("{");
        _indent++;

        foreach (var field in cls.Fields)
            TranspileClassField(field);

        if (cls.Fields.Count > 0 && cls.Methods.Count > 0)
            AppendLine("");

        foreach (var method in cls.Methods)
            TranspileMethod(method);

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileClassField(ClassField field)
    {
        var modifier = field.IsMutable ? "" : "readonly ";
        Append($"public {modifier}{TranspileType(field.Type)} {Capitalize(field.Name)}");
        if (field.DefaultValue is not null)
        {
            _sb.Append(" = ");
            TranspileExpression(field.DefaultValue);
        }
        _sb.AppendLine(";");
    }

    private void TranspileService(ServiceDecl svc)
    {
        var ctorParams = string.Join(", ", svc.ConstructorParams.Select(p => $"{TranspileType(p.Type)} {EscapeIdent(p.Name)}"));
        var iface = svc.Interface is not null ? $" : {svc.Interface}" : "";

        AppendLine(ctorParams.Length > 0 ? $"class {svc.Name}({ctorParams}){iface}" : $"class {svc.Name}{iface}");
        AppendLine("{");
        _indent++;

        foreach (var field in svc.Fields)
            TranspileClassField(field);

        if (svc.Fields.Count > 0 && svc.Methods.Count > 0)
            AppendLine("");

        foreach (var method in svc.Methods)
            TranspileMethod(method);

        _indent--;
        AppendLine("}");
        AppendLine("");
    }

    private void TranspileModule(ModuleDecl mod)
    {
        AppendLine($"static class {mod.Name}Module");
        AppendLine("{");
        _indent++;

        AppendLine("public static IServiceCollection Configure(this IServiceCollection services)");
        AppendLine("{");
        _indent++;

        if (mod.Extends is not null)
            AppendLine($"{mod.Extends}Module.Configure(services);");

        foreach (var binding in mod.Bindings)
        {
            var register = _servicesByName.TryGetValue(binding.Implementation, out var svc)
                ? svc.Lifetime switch
                {
                    ServiceLifetime.Singleton => "AddSingleton",
                    ServiceLifetime.Transient => "AddTransient",
                    _ => "AddScoped"
                }
                : "AddScoped";
            AppendLine($"services.{register}<{binding.Interface}, {binding.Implementation}>();");
        }

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

            var routeBuilder = new StringBuilder();
            routeBuilder.Append($"app.{method}(\"{fullPath}\", {Capitalize(endpoint.Handler)})");

            foreach (var mod in endpoint.Modifiers)
            {
                if (_modifiers.TryGetValue(mod.Name, out var modDecl))
                    routeBuilder.Append(GenerateEndpointFilter(modDecl, mod.Args));
            }

            routeBuilder.Append(";");
            AppendLine(routeBuilder.ToString());
        }
        AppendLine("");
    }

    private string GenerateEndpointFilter(ModifierDecl modDecl, List<Expr>? args)
    {
        var sb = new StringBuilder();
        sb.Append(".AddEndpointFilter(async (context, next) => {");

        if (modDecl.ConfigParams.Count > 0 && args is not null && args.Count > 0)
        {
            for (int i = 0; i < Math.Min(modDecl.ConfigParams.Count, args.Count); i++)
            {
                var param = modDecl.ConfigParams[i];
                var argValue = TranspileExpressionToString(args[i]);
                sb.Append($" var {param.Name} = {argValue}; ");
            }
        }

        sb.Append(" return await next(context); })");
        return sb.ToString();
    }

    private void EmitJsonContext(List<RecordDecl> records)
    {
        AppendLine("[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]");
        foreach (var rec in records)
        {
            AppendLine($"[JsonSerializable(typeof({rec.Name}))]");
            AppendLine($"[JsonSerializable(typeof(System.Collections.Generic.List<{rec.Name}>))]");
        }
        AppendLine("internal partial class AppJsonContext : JsonSerializerContext { }");
        AppendLine("");
    }

    private void EmitLengthHelper()
    {
        AppendLine("static class __Y");
        AppendLine("{");
        AppendLine("    public static int Length(string s) => s.Length;");
        AppendLine("    public static int Length<T>(System.Collections.Generic.IReadOnlyCollection<T> c) => c.Count;");
        AppendLine("    public static int Length(System.Collections.IEnumerable e) { var n = 0; foreach (var _ in e) n++; return n; }");
        AppendLine("}");
        AppendLine("");
    }

    // Inline version used when the app is composed with routes: services are
    // already on builder.Services, so we just resolve injected params from
    // app.Services and run the main body as top-level statements.
    private void EmitAppStartup(AppDecl app)
    {
        var mainFn = app.MainFn;
        foreach (var param in mainFn.Params)
            AppendLine($"var {EscapeIdent(param.Name)} = app.Services.GetRequiredService<{TranspileType(param.Type)}>();");

        if (mainFn.Body is not null)
            foreach (var stmt in mainFn.Body.Statements)
                TranspileStatement(stmt);

        AppendLine("");
    }

    private void TranspileApp(AppDecl app)
    {
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

        var mainFn = app.MainFn;
        if (mainFn.Params.Count > 0)
        {
            foreach (var param in mainFn.Params)
                AppendLine($"var {param.Name} = provider.GetRequiredService<{TranspileType(param.Type)}>();");
            AppendLine("");
        }

        if (mainFn.Body is not null)
        {
            foreach (var stmt in mainFn.Body.Statements)
                TranspileStatement(stmt);
        }

        _indent--;
        AppendLine("}");

        _indent--;
        AppendLine("}");
        AppendLine("");
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
}
