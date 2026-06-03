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
        var logged = fn.Modifiers.Any(m => m.Name == "log");
        if (logged) _usesLog = true;

        if (fn.Body is not null)
        {
            AppendLine("{");
            _indent++;
            if (logged) BeginLogScope(fn.Name);
            foreach (var stmt in fn.Body.Statements)
                TranspileStatement(stmt);
            if (logged) EndLogScope(fn.Name);
            _indent--;
            AppendLine("}");
        }
        else if (fn.ExprBody is not null)
        {
            if (logged)
            {
                // Promote expression-body to statement body so we can wrap with timing.
                AppendLine("{");
                _indent++;
                BeginLogScope(fn.Name);
                Append("return ");
                TranspileExpression(fn.ExprBody);
                _sb.AppendLine(";");
                EndLogScope(fn.Name);
                _indent--;
                AppendLine("}");
            }
            else
            {
                Append("    => ");
                TranspileExpression(fn.ExprBody);
                _sb.AppendLine(";");
            }
        }
    }

    private void BeginLogScope(string fnName)
    {
        AppendLine($"var __sw = System.Diagnostics.Stopwatch.StartNew();");
        AppendLine($"__Y.Log(\"info\", \"fn={fnName} enter\");");
        AppendLine("try {");
        _indent++;
    }

    private void EndLogScope(string fnName)
    {
        _indent--;
        AppendLine("} finally {");
        _indent++;
        AppendLine("__sw.Stop();");
        AppendLine($"__Y.Log(\"info\", $\"fn={fnName} exit ms={{__sw.ElapsedMilliseconds}}\");");
        _indent--;
        AppendLine("}");
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

    private void TranspileEnum(EnumDecl enm) =>
        TranspileTaggedUnion(enm.Name, enm.Variants);

    private void TranspileError(ErrorDecl err) =>
        TranspileTaggedUnion(err.Name, err.Variants);

    // Shared shape for `enum` and `error` declarations: an abstract record with
    // sealed-record variants, tagged with JSON polymorphism so they round-trip
    // as `{"kind":"VariantName", ...fields}` through System.Text.Json.
    private void TranspileTaggedUnion(string name, List<EnumVariant> variants)
    {
        AppendLine("[JsonPolymorphic(TypeDiscriminatorPropertyName = \"kind\")]");
        foreach (var v in variants)
            AppendLine($"[JsonDerivedType(typeof({name}.{v.Name}), \"{v.Name}\")]");

        AppendLine($"abstract record {name}");
        AppendLine("{");
        _indent++;

        AppendLine($"private {name}() {{ }}");
        AppendLine("");

        foreach (var variant in variants)
            TranspileEnumVariant(name, variant);

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

        // `~secret` lifts to [property: JsonIgnore] on positional record params
        // so the field exists in the type but never appears in serialized JSON.
        var primaryParams = string.Join(", ", fieldsWithoutDefaults.Select(f =>
        {
            var attrs = IsSecret(f) ? "[property: JsonIgnore] " : "";
            return $"{attrs}{TranspileType(f.Type)} {Capitalize(f.Name)}";
        }));

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
                if (IsSecret(field)) AppendLine("[JsonIgnore]");
                Append($"public {TranspileType(field.Type)} {Capitalize(field.Name)} {{ get; init; }} = ");
                TranspileExpression(field.DefaultValue!);
                _sb.AppendLine(";");
            }

            _indent--;
            AppendLine("}");
        }

        AppendLine("");
    }

    private static bool IsSecret(RecordField f) => f.Modifiers.Any(m => m.Name == "secret");

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
        // Keep the user's original casing -- method bodies reference `entries` and
        // emitted C# must match (capitalizing would produce undefined-name errors).
        var modifier = field.IsMutable ? "" : "readonly ";
        Append($"public {modifier}{TranspileType(field.Type)} {EscapeIdent(field.Name)}");
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
            var handlerExpr = BuildHandlerExpression(endpoint.Handler);
            routeBuilder.Append($"app.{method}(\"{fullPath}\", {handlerExpr})");

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

    // Wraps a route handler in a lambda when codegen needs to do something with
    // the return value: ~created/~accepted/~noContent → status-code IResult,
    // Result<T> → status-mapped IResult. Otherwise hand the method group to MapGet as-is.
    private string BuildHandlerExpression(string handlerName)
    {
        if (!_fnsByName.TryGetValue(handlerName, out var fn))
            return Capitalize(handlerName);

        var statusModifier = fn.Modifiers.FirstOrDefault(m =>
            m.Name is "created" or "accepted" or "noContent");

        if (statusModifier is null && !IsResultReturn(fn.ReturnType))
            return Capitalize(handlerName);

        var paramList = string.Join(", ", fn.Params.Select(p => $"{TranspileType(p.Type)} {EscapeIdent(p.Name)}"));
        var argList = string.Join(", ", fn.Params.Select(p => EscapeIdent(p.Name)));
        var call = $"{Capitalize(handlerName)}({argList})";

        // Async-by-default handlers need `await` and an `async` lambda. Blocking ones
        // can stay sync (no `~blocking` on the fn → it's async).
        var isAsync = !IsBlocking(fn);
        var asyncKw = isAsync ? "async " : "";
        var awaited = isAsync ? $"await {call}" : call;

        if (statusModifier is not null)
        {
            var wrap = statusModifier.Name switch
            {
                "created"   => $"Microsoft.AspNetCore.Http.Results.Created((string?)null, {awaited})",
                "accepted"  => $"Microsoft.AspNetCore.Http.Results.Accepted((string?)null, {awaited})",
                "noContent" => $"{{ {awaited}; return Microsoft.AspNetCore.Http.Results.NoContent(); }}",
                _ => awaited
            };
            return $"{asyncKw}({paramList}) => {wrap}";
        }

        _usesResultHttp = true;
        return $"{asyncKw}({paramList}) => __Y.Wrap({awaited})";
    }

    private static bool IsResultReturn(TypeRef t) =>
        t is GenericTypeRef { Name: "Result" };

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

    private void EmitJsonContext(List<RecordDecl> records, List<EnumDecl> enums, List<ErrorDecl> errors)
    {
        AppendLine("[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]");
        foreach (var rec in records)
        {
            AppendLine($"[JsonSerializable(typeof({rec.Name}))]");
            AppendLine($"[JsonSerializable(typeof(System.Collections.Generic.List<{rec.Name}>))]");
        }
        // Enums/errors are abstract base records; STJ picks up variants via JsonDerivedType.
        foreach (var enm in enums)
            AppendLine($"[JsonSerializable(typeof({enm.Name}))]");
        foreach (var err in errors)
            AppendLine($"[JsonSerializable(typeof({err.Name}))]");
        AppendLine("internal partial class AppJsonContext : JsonSerializerContext { }");
        AppendLine("");
    }

    // Outbound HTTP. Wraps HttpClient in Result<string> per Y# error conventions.
    // HttpClient itself is AOT-safe since .NET 8.
    private void EmitHttpHelper()
    {
        AppendLine("static partial class __Y");
        AppendLine("{");
        AppendLine("    private static readonly System.Net.Http.HttpClient __http = new();");
        AppendLine("    public static async System.Threading.Tasks.Task<Result<string>> HttpGet(string url)");
        AppendLine("    {");
        AppendLine("        try { return await __http.GetStringAsync(url); }");
        AppendLine("        catch (System.Exception ex) { return Error.Failure(ex.Message); }");
        AppendLine("    }");
        AppendLine("    public static async System.Threading.Tasks.Task<Result<string>> HttpPost(string url, string body)");
        AppendLine("    {");
        AppendLine("        try {");
        AppendLine("            var resp = await __http.PostAsync(url, new System.Net.Http.StringContent(body));");
        AppendLine("            return await resp.Content.ReadAsStringAsync();");
        AppendLine("        }");
        AppendLine("        catch (System.Exception ex) { return Error.Failure(ex.Message); }");
        AppendLine("    }");
        AppendLine("}");
        AppendLine("");
    }

    private void EmitConcatHelper()
    {
        AppendLine("static partial class __Y");
        AppendLine("{");
        AppendLine("    public static System.Collections.Generic.List<T> Concat<T>(System.Collections.Generic.IEnumerable<T> a, System.Collections.Generic.IEnumerable<T> b)");
        AppendLine("    {");
        AppendLine("        var r = new System.Collections.Generic.List<T>(a);");
        AppendLine("        r.AddRange(b);");
        AppendLine("        return r;");
        AppendLine("    }");
        AppendLine("}");
        AppendLine("");
    }

    private void EmitLogHelper()
    {
        AppendLine("static partial class __Y");
        AppendLine("{");
        AppendLine("    public static void Log(string level, string message)");
        AppendLine("    {");
        AppendLine("        // JsonEncodedText.Encode is reflection-free, so this survives AOT trimming.");
        AppendLine("        var ts = System.DateTime.UtcNow.ToString(\"o\");");
        AppendLine("        var msg = System.Text.Json.JsonEncodedText.Encode(message);");
        AppendLine("        System.Console.WriteLine($\"{{\\\"level\\\":\\\"{level}\\\",\\\"ts\\\":\\\"{ts}\\\",\\\"msg\\\":\\\"{msg}\\\"}}\");");
        AppendLine("    }");
        AppendLine("}");
        AppendLine("");
    }

    private void EmitResultHttpHelper()
    {
        AppendLine("static partial class __Y");
        AppendLine("{");
        AppendLine("    public static Microsoft.AspNetCore.Http.IResult Wrap<T>(Result<T> r) =>");
        AppendLine("        r.IsOk");
        AppendLine("            ? Microsoft.AspNetCore.Http.Results.Ok(r.Value)");
        AppendLine("            : r.Error.Kind switch");
        AppendLine("              {");
        AppendLine("                  ErrorKind.NotFound   => Microsoft.AspNetCore.Http.Results.NotFound(new { error = r.Error.Code, message = r.Error.Description }),");
        AppendLine("                  ErrorKind.Validation => Microsoft.AspNetCore.Http.Results.BadRequest(new { error = r.Error.Code, message = r.Error.Description }),");
        AppendLine("                  _                    => Microsoft.AspNetCore.Http.Results.Problem(r.Error.Description, statusCode: 500),");
        AppendLine("              };");
        AppendLine("}");
        AppendLine("");
    }

    private void EmitLengthHelper()
    {
        AppendLine("static partial class __Y");
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
        AppendLine("// Single-parameter Result for the built-in Error type.");
        AppendLine("// Error is always defined (default-valued when Ok) so user code can write");
        AppendLine("// `r.error.description` without `.Value` unwrapping. IsError discriminates.");
        AppendLine("readonly struct Result<T>");
        AppendLine("{");
        AppendLine("    public T? Value { get; }");
        AppendLine("    public Error Error { get; }");
        AppendLine("    public bool IsError { get; }");
        AppendLine("    public bool IsOk => !IsError;");
        AppendLine("    private Result(T value) { Value = value; Error = default; IsError = false; }");
        AppendLine("    private Result(Error error) { Value = default; Error = error; IsError = true; }");
        AppendLine("    public static implicit operator Result<T>(T value) => new(value);");
        AppendLine("    public static implicit operator Result<T>(Error error) => new(error);");
        AppendLine("    public override string ToString() => IsError ? $\"Error: {Error}\" : $\"Ok: {Value}\";");
        AppendLine("    public Result<U> Map<U>(Func<T, U> fn) => IsError ? Error : fn(Value!);");
        AppendLine("    public Result<U> Then<U>(Func<T, Result<U>> fn) => IsError ? Error : fn(Value!);");
        AppendLine("    public T UnwrapOr(T defaultValue) => IsOk ? Value! : defaultValue;");
        AppendLine("    public T UnwrapOrElse(Func<Error, T> fn) => IsOk ? Value! : fn(Error);");
        AppendLine("}");
        AppendLine("");
        AppendLine("// Two-parameter Result for custom error types.");
        AppendLine("readonly struct Result<T, E>");
        AppendLine("{");
        AppendLine("    public T? Value { get; }");
        AppendLine("    public E? Error { get; }");
        AppendLine("    public bool IsError { get; }");
        AppendLine("    public bool IsOk => !IsError;");
        AppendLine("    private Result(T value) { Value = value; Error = default; IsError = false; }");
        AppendLine("    private Result(E error) { Value = default; Error = error; IsError = true; }");
        AppendLine("    public static implicit operator Result<T, E>(T value) => new(value);");
        AppendLine("    public static implicit operator Result<T, E>(E error) => new(error);");
        AppendLine("    public override string ToString() => IsError ? $\"Error: {Error}\" : $\"Ok: {Value}\";");
        AppendLine("    public Result<U, E> Map<U>(Func<T, U> fn) => IsError ? Error! : fn(Value!);");
        AppendLine("    public Result<U, E> Then<U>(Func<T, Result<U, E>> fn) => IsError ? Error! : fn(Value!);");
        AppendLine("    public T UnwrapOr(T defaultValue) => IsOk ? Value! : defaultValue;");
        AppendLine("    public T UnwrapOrElse(Func<E, T> fn) => IsOk ? Value! : fn(Error!);");
        AppendLine("}");
        AppendLine("");
    }
}
