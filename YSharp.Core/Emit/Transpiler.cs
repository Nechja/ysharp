using System.Text;
using YSharp.Core.Ast;

namespace YSharp.Core.Emit;

/// <summary>
/// Transpiles Y# AST to C# source code.
///
/// Y# is to C# what TypeScript is to JavaScript:
/// - Stricter types
/// - Cleaner syntax
/// - Domain-focused (HTTP services)
/// - Compiles to the host language
/// </summary>
public partial class Transpiler(string assemblyName)
{
    private readonly StringBuilder _topSb = new();
    private readonly StringBuilder _typeSb = new();
    private StringBuilder _sb;
    private int _indent;
    private bool _usesResult;
    private bool _usesDi;
    private bool _usesRoutes;
    private bool _usesLength;
    private bool _usesResultHttp;
    private bool _usesLog;
    private bool _usesConcat;
    private bool _usesHttpClient;
    private readonly HashSet<string> _typeNames = [];
    private readonly HashSet<string> _asyncFunctions = [];
    private readonly HashSet<string> _voidFunctions = [];
    private int _tryCounter;
    private int _concurrentTaskCounter;
    private readonly Dictionary<string, ModifierDecl> _modifiers = [];
    private readonly Dictionary<string, ServiceDecl> _servicesByName = [];
    private readonly Dictionary<string, FnDecl> _fnsByName = [];

    public bool UsesDependencyInjection => _usesDi;
    public bool UsesHttpRoutes => _usesRoutes;

    public string Transpile(List<Decl> declarations)
    {
        _topSb.Clear();
        _typeSb.Clear();
        _sb = _topSb;
        _indent = 0;
        _usesLength = false;
        _usesResultHttp = false;
        _usesLog = false;
        _usesConcat = false;
        _usesHttpClient = false;

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
        var modifiers = declarations.OfType<ModifierDecl>();

        foreach (var mod in modifiers)
            _modifiers[mod.Name] = mod;

        _servicesByName.Clear();
        foreach (var svc in services)
            _servicesByName[svc.Name] = svc;

        _fnsByName.Clear();
        foreach (var fn in declarations.OfType<FnDecl>())
            _fnsByName[fn.Name] = fn;

        CollectTypeNames(declarations);
        CollectAsyncFunctions(declarations);
        CollectVoidFunctions(declarations);
        ScanForResultUsage(declarations);

        _usesDi = services.Any() || modules.Any() || appDecl is not null;
        _usesRoutes = routes.Any();

        if (_usesDi)
        {
            AppendLine("using Microsoft.Extensions.DependencyInjection;");
            AppendLine("");
        }

        // JSON attributes show up on tagged unions (JsonPolymorphic), `~secret`
        // fields (JsonIgnore), and the route-side AppJsonContext.
        var needsJsonAttrs = _usesRoutes
            || enums.Any()
            || errors.Any()
            || records.Any(r => r.Fields.Any(f => f.Modifiers.Any(m => m.Name == "secret")));
        if (needsJsonAttrs)
        {
            AppendLine("using System.Text.Json.Serialization;");
            AppendLine("");
        }

        // When both routes and an app DI block are present, fold them into one
        // WebApplication: the module registers into builder.Services, the app's
        // main body runs once after Build(), and route handlers DI from app.Services.
        var combineAppAndRoutes = _usesRoutes && appDecl is not null;

        if (_usesRoutes)
        {
            // CreateSlimBuilder is the AOT-friendly minimal-host entry point.
            AppendLine("var builder = WebApplication.CreateSlimBuilder(args);");
            // Honor $PORT (cloud / container convention); fall back to 9900 for dev.
            AppendLine("var __port = System.Environment.GetEnvironmentVariable(\"PORT\") ?? \"9900\";");
            AppendLine("builder.WebHost.UseUrls($\"http://0.0.0.0:{__port}\");");
            // Wire the source-gen'd JSON context so System.Text.Json never reflects.
            AppendLine("builder.Services.ConfigureHttpJsonOptions(o =>");
            AppendLine("    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));");
            if (combineAppAndRoutes)
                AppendLine($"{appDecl!.ModuleName}Module.Configure(builder.Services);");
            AppendLine("var app = builder.Build();");
            AppendLine("");
        }

        // === TOP-LEVEL STATEMENTS (_sb == _topSb) ===
        if (mainFn is not null)
            TranspileFunction(mainFn);

        foreach (var fn in otherFns)
            TranspileFunction(fn);

        if (combineAppAndRoutes)
            EmitAppStartup(appDecl!);

        foreach (var route in routes)
            TranspileRoute(route);

        if (_usesRoutes)
        {
            AppendLine("app.Run();");
            AppendLine("");
        }

        // === TYPE DECLARATIONS (collected into _typeSb) ===
        _sb = _typeSb;

        foreach (var iface in interfaces)
            TranspileInterface(iface);

        foreach (var enm in enums)
            TranspileEnum(enm);

        foreach (var err in errors)
            TranspileError(err);

        foreach (var rec in records)
            TranspileRecord(rec);

        foreach (var cls in classes)
            TranspileClass(cls);

        foreach (var svc in services)
            TranspileService(svc);

        foreach (var mod in modules)
            TranspileModule(mod);

        // Only emit the standalone Program.Main when routes are NOT in play
        // (with routes, the app DI is wired into builder.Services above).
        if (appDecl is not null && !_usesRoutes)
            TranspileApp(appDecl);

        if (_usesResult)
            EmitResultType();

        if (_usesLength)
            EmitLengthHelper();

        if (_usesResultHttp)
            EmitResultHttpHelper();

        if (_usesLog)
            EmitLogHelper();

        if (_usesConcat)
            EmitConcatHelper();

        if (_usesHttpClient)
            EmitHttpHelper();

        if (_usesRoutes)
        {
            EmitJsonContext(records.ToList(), enums.ToList(), errors.ToList());
            BuildOpenApiSpec(assemblyName, routes.ToList(), records.ToList(), enums.ToList(), errors.ToList());
        }
        else
        {
            OpenApiSpec = null;
        }

        // === FINAL ASSEMBLY: top-level first, then types ===
        return _topSb.ToString() + _typeSb.ToString();
    }

    private void CollectTypeNames(List<Decl> declarations)
    {
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
                    foreach (var variant in enm.Variants)
                        _typeNames.Add($"{enm.Name}.{variant.Name}");
                    break;
                case ErrorDecl err:
                    _typeNames.Add(err.Name);
                    foreach (var variant in err.Variants)
                        _typeNames.Add($"{err.Name}.{variant.Name}");
                    break;
                case ServiceDecl svc:
                    _typeNames.Add(svc.Name);
                    break;
            }
        }
    }

    private void CollectAsyncFunctions(List<Decl> declarations)
    {
        foreach (var fn in declarations.OfType<FnDecl>())
        {
            if (!IsBlocking(fn) && fn.Name != "main")
                _asyncFunctions.Add(fn.Name);
        }

        foreach (var cls in declarations.OfType<ClassDecl>())
        {
            foreach (var method in cls.Methods)
            {
                if (!IsBlocking(method))
                    _asyncFunctions.Add($"{cls.Name}.{method.Name}");
            }
        }

        foreach (var svc in declarations.OfType<ServiceDecl>())
        {
            foreach (var method in svc.Methods)
            {
                if (!IsBlocking(method))
                    _asyncFunctions.Add($"{svc.Name}.{method.Name}");
            }
        }
    }

    private void CollectVoidFunctions(List<Decl> declarations)
    {
        foreach (var fn in declarations.OfType<FnDecl>())
        {
            if (fn.ReturnType is NamedTypeRef { Name: "void" })
                _voidFunctions.Add(fn.Name);
        }

        foreach (var cls in declarations.OfType<ClassDecl>())
        {
            foreach (var method in cls.Methods)
            {
                if (method.ReturnType is NamedTypeRef { Name: "void" })
                    _voidFunctions.Add($"{cls.Name}.{method.Name}");
            }
        }

        foreach (var svc in declarations.OfType<ServiceDecl>())
        {
            foreach (var method in svc.Methods)
            {
                if (method.ReturnType is NamedTypeRef { Name: "void" })
                    _voidFunctions.Add($"{svc.Name}.{method.Name}");
            }
        }
    }

    private void ScanForResultUsage(List<Decl> declarations)
    {
        foreach (var fn in declarations.OfType<FnDecl>())
        {
            if (fn.ReturnType is GenericTypeRef { Name: "Result" })
            {
                _usesResult = true;
                return;
            }
        }
    }
}
