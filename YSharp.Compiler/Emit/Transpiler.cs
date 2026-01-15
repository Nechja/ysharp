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
public partial class Transpiler(string assemblyName)
{
    private readonly StringBuilder _sb = new();
    private int _indent;
    private bool _usesResult;
    private bool _usesDi;
    private bool _usesRoutes;
    private readonly HashSet<string> _typeNames = [];
    private readonly HashSet<string> _asyncFunctions = [];
    private readonly HashSet<string> _voidFunctions = [];
    private int _tryCounter;
    private int _concurrentTaskCounter;
    private readonly Dictionary<string, ModifierDecl> _modifiers = [];

    public bool UsesDependencyInjection => _usesDi;
    public bool UsesHttpRoutes => _usesRoutes;

    public string Transpile(List<Decl> declarations)
    {
        _sb.Clear();
        _indent = 0;

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

        if (_usesRoutes)
        {
            AppendLine("var builder = WebApplication.CreateBuilder(args);");
            AppendLine("builder.WebHost.UseUrls(\"http://localhost:9900\");");
            AppendLine("var app = builder.Build();");
            AppendLine("");
        }

        if (mainFn is not null)
            TranspileFunction(mainFn);

        foreach (var fn in otherFns)
            TranspileFunction(fn);

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

        foreach (var route in routes)
            TranspileRoute(route);

        if (_usesRoutes)
        {
            AppendLine("app.Run();");
            AppendLine("");
        }

        if (appDecl is not null)
            TranspileApp(appDecl);

        if (_usesResult)
            EmitResultType();

        return _sb.ToString();
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
