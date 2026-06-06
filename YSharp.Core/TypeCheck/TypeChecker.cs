using YSharp.Core.Ast;

namespace YSharp.Core.TypeCheck;

public class TypeChecker
{
    private static readonly HashSet<string> BuiltinIdentifiers =
    [
        "print", "env", "log", "http",
        "Error", "true", "false", "null",
        "HttpRequest", "HttpResponse"
    ];

    private static readonly HashSet<string> BuiltinTypes =
    [
        "int", "long", "float", "double", "bool", "string", "void",
        "List", "Result", "Option", "Task",
        "HttpRequest", "HttpResponse"
    ];

    private readonly Dictionary<string, FnDecl> _functions = new();
    private readonly HashSet<string> _types = new();           // record/class/interface/service/enum/error names
    private readonly HashSet<string> _enumVariantsByQualified = new();  // "EnumName.VariantName"
    private readonly Stack<HashSet<string>> _scopes = new();
    private readonly HashSet<string> _methodScope = new();
    private readonly List<Diagnostic> _diagnostics = new();

    private FnDecl? _currentFn;

    public List<Diagnostic> Check(List<Decl> declarations)
    {
        _diagnostics.Clear();
        _functions.Clear();
        _types.Clear();
        _enumVariantsByQualified.Clear();
        _scopes.Clear();

        RegisterTopLevel(declarations);

        foreach (var fn in declarations.OfType<FnDecl>())
            CheckFunction(fn);

        foreach (var cls in declarations.OfType<ClassDecl>())
            foreach (var m in cls.Methods)
                CheckFunction(m, classCtorParams: cls.ConstructorParams,
                              siblingMethods: cls.Methods,
                              siblingFields: cls.Fields.Select(f => f.Name));

        foreach (var svc in declarations.OfType<ServiceDecl>())
            foreach (var m in svc.Methods)
                CheckFunction(m, classCtorParams: svc.ConstructorParams,
                              siblingMethods: svc.Methods,
                              siblingFields: svc.Fields.Select(f => f.Name));

        return _diagnostics;
    }

    private void RegisterTopLevel(List<Decl> declarations)
    {
        foreach (var d in declarations)
        {
            switch (d)
            {
                case FnDecl fn:
                    _functions[fn.Name] = fn;
                    break;
                case RecordDecl r: _types.Add(r.Name); break;
                case ClassDecl c:  _types.Add(c.Name); break;
                case InterfaceDecl i: _types.Add(i.Name); break;
                case ServiceDecl s: _types.Add(s.Name); break;
                case EnumDecl e:
                    _types.Add(e.Name);
                    foreach (var v in e.Variants)
                        _enumVariantsByQualified.Add($"{e.Name}.{v.Name}");
                    break;
                case ErrorDecl er:
                    _types.Add(er.Name);
                    foreach (var v in er.Variants)
                        _enumVariantsByQualified.Add($"{er.Name}.{v.Name}");
                    break;
            }
        }
    }

    private void CheckFunction(
        FnDecl fn,
        List<Param>? classCtorParams = null,
        IEnumerable<FnDecl>? siblingMethods = null,
        IEnumerable<string>? siblingFields = null)
    {
        _currentFn = fn;
        _scopes.Push(new HashSet<string>());

        if (classCtorParams is not null)
            foreach (var p in classCtorParams)
                _scopes.Peek().Add(p.Name);

        if (siblingFields is not null)
            foreach (var fname in siblingFields)
                _scopes.Peek().Add(fname);

        // Sibling methods are callable by bare name from within their class/service.
        // Stash them in a frame-local "method" scope used only for call resolution.
        if (siblingMethods is not null)
            foreach (var sm in siblingMethods)
                _methodScope.Add(sm.Name);

        foreach (var p in fn.Params)
            _scopes.Peek().Add(p.Name);

        if (fn.Body is not null)
        {
            foreach (var stmt in fn.Body.Statements)
                CheckStmt(stmt);

            CheckReturnReachability(fn);
        }
        else if (fn.ExprBody is not null)
        {
            // Expression-body fns: the expression IS the return.
            CheckExpr(fn.ExprBody);
            // Type mismatch check between expr and ReturnType is in Tier 2.
        }

        _scopes.Pop();
        _methodScope.Clear();
        _currentFn = null;
    }

    private void CheckReturnReachability(FnDecl fn)
    {
        var isVoid = fn.ReturnType is NamedTypeRef { Name: "void" };
        if (isVoid || fn.Name == "main") return;

        // Generic permissive check: a return reachable somewhere in the body.
        if (!BodyContainsReturn(fn.Body!))
        {
            Report("typecheck",
                $"function '{fn.Name}' returns {TypeName(fn.ReturnType)} but has no return statement",
                fn.Span);
        }
    }

    private static bool BodyContainsReturn(BlockStmt block)
    {
        foreach (var s in block.Statements)
            if (ContainsReturn(s)) return true;
        return false;
    }

    private static bool ContainsReturn(Stmt s) => s switch
    {
        ReturnStmt => true,
        IfStmt ifs => BodyContainsReturn(ifs.ThenBlock) && (ifs.ElseBlock is null || BodyContainsReturn(ifs.ElseBlock)),
        BlockStmt b => BodyContainsReturn(b),
        ForStmt fs => BodyContainsReturn(fs.Body),
        _ => false
    };

    private void CheckStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case VarDeclStmt vd:
                CheckExpr(vd.Value);
                _scopes.Peek().Add(vd.Name);
                break;

            case AssignStmt a:
                if (!ResolveIdent(a.Target))
                    Report("typecheck", $"undefined identifier '{a.Target}'", a.Span);
                CheckExpr(a.Value);
                break;

            case CompoundAssignStmt c:
                if (!ResolveIdent(c.Target))
                    Report("typecheck", $"undefined identifier '{c.Target}'", c.Span);
                CheckExpr(c.Value);
                break;

            case ReturnStmt r:
                CheckReturnStmt(r);
                if (r.Value is not null) CheckExpr(r.Value);
                break;

            case ExprStmt es:
                CheckExpr(es.Expression);
                break;

            case IfStmt ifs:
                CheckExpr(ifs.Condition);
                _scopes.Push(new HashSet<string>());
                foreach (var s in ifs.ThenBlock.Statements) CheckStmt(s);
                _scopes.Pop();
                if (ifs.ElseBlock is not null)
                {
                    _scopes.Push(new HashSet<string>());
                    foreach (var s in ifs.ElseBlock.Statements) CheckStmt(s);
                    _scopes.Pop();
                }
                break;

            case ForStmt fs:
                if (fs.Iterable is not null) CheckExpr(fs.Iterable);
                _scopes.Push(new HashSet<string>());
                if (fs.Variable is not null) _scopes.Peek().Add(fs.Variable);
                foreach (var s in fs.Body.Statements) CheckStmt(s);
                _scopes.Pop();
                break;

            case BlockStmt b:
                _scopes.Push(new HashSet<string>());
                foreach (var s in b.Statements) CheckStmt(s);
                _scopes.Pop();
                break;

            // BreakStmt / ContinueStmt: nothing to check
        }
    }

    private void CheckReturnStmt(ReturnStmt r)
    {
        if (_currentFn is null) return;
        var isVoid = _currentFn.ReturnType is NamedTypeRef { Name: "void" };

        if (isVoid && r.Value is not null)
        {
            Report("typecheck",
                $"function '{_currentFn.Name}' returns void but a value is being returned",
                r.Span);
        }
        else if (!isVoid && r.Value is null)
        {
            Report("typecheck",
                $"function '{_currentFn.Name}' returns {TypeName(_currentFn.ReturnType)} but `return;` has no value",
                r.Span);
        }
    }

    private void CheckExpr(Expr expr)
    {
        switch (expr)
        {
            case IdentifierExpr id:
                if (!ResolveIdent(id.Name))
                    Report("typecheck", $"undefined identifier '{id.Name}'", id.Span);
                break;

            case CallExpr c:
                CheckCall(c);
                break;

            case BinaryExpr b:
                CheckExpr(b.Left);
                CheckExpr(b.Right);
                break;

            case UnaryExpr u:
                CheckExpr(u.Operand);
                break;

            case MemberAccessExpr m:
                CheckExpr(m.Target);
                break;

            case IndexAccessExpr ia:
                CheckExpr(ia.Target);
                CheckExpr(ia.Index);
                break;

            case ArrayExpr a:
                foreach (var e in a.Elements) CheckExpr(e);
                break;

            case InterpolatedStringExpr i:
                foreach (var part in i.Parts)
                    if (part is InterpolatedExpr ie) CheckExpr(ie.Expression);
                break;

            case LambdaExpr lambda:
                _scopes.Push(new HashSet<string>());
                foreach (var p in lambda.Parameters) _scopes.Peek().Add(p);
                CheckExpr(lambda.Body);
                _scopes.Pop();
                break;

            case TryExpr t:
                CheckExpr(t.Operand);
                break;

            case MatchExpr mx:
                CheckExpr(mx.Value);
                foreach (var arm in mx.Arms)
                {
                    // Pattern bindings come into scope for the arm body.
                    _scopes.Push(new HashSet<string>());
                    CollectPatternBindings(arm.Pattern);
                    CheckExpr(arm.Result);
                    _scopes.Pop();
                }
                break;

            case RangeExpr r:
                CheckExpr(r.Start);
                CheckExpr(r.End);
                break;

            case WithExpr w:
                CheckExpr(w.Base);
                foreach (var update in w.Updates) CheckExpr(update.Value);
                break;

            case ConcurrentExpr cx:
                // Bindings declared inside `concurrent { let x = ... }` are intentionally
                // visible in the enclosing scope -- the transpiler emits them inline.
                foreach (var s in cx.Statements) CheckStmt(s);
                break;

            case BlockingExpr bx:
                _scopes.Push(new HashSet<string>());
                foreach (var s in bx.Statements) CheckStmt(s);
                if (bx.ResultExpr is not null) CheckExpr(bx.ResultExpr);
                _scopes.Pop();
                break;

            case ScopeExpr sx:
                _scopes.Push(new HashSet<string>());
                foreach (var s in sx.Statements) CheckStmt(s);
                _scopes.Pop();
                break;

            case SomeExpr s:
                CheckExpr(s.Value);
                break;
        }
    }

    private void CollectPatternBindings(Expr pattern)
    {
        // Variant destructure: Type.Variant(bind1, bind2) -- bindings are IdentifierExpr args.
        if (pattern is CallExpr { Target: MemberAccessExpr } call)
        {
            foreach (var arg in call.Args)
                if (arg is IdentifierExpr id)
                    _scopes.Peek().Add(id.Name);
        }
    }

    private void CheckCall(CallExpr call)
    {
        // Recurse into args first (their identifiers should resolve regardless of target).
        foreach (var a in call.Args) CheckExpr(a);

        // Arity check against a known top-level fn.
        if (call.Target is IdentifierExpr id)
        {
            if (_types.Contains(id.Name)) return; // record/service constructor -- skip arity (records have synthesized ctor)
            if (BuiltinIdentifiers.Contains(id.Name)) return; // print/env/etc -- variadic-ish

            if (_functions.TryGetValue(id.Name, out var fn))
            {
                if (call.Args.Count != fn.Params.Count)
                {
                    Report("typecheck",
                        $"'{id.Name}' takes {fn.Params.Count} argument(s), got {call.Args.Count}",
                        id.Span);
                }
                return;
            }

            // Calls to a sibling method on the same class/service: don't arity-check
            // yet (we'd need richer member-resolution); just confirm it exists.
            if (_methodScope.Contains(id.Name)) return;

            Report("typecheck", $"undefined function '{id.Name}'", id.Span);
            return;
        }

        // Member call (obj.method) or other complex target -- recurse into target.
        CheckExpr(call.Target);
    }

    private bool ResolveIdent(string name)
    {
        foreach (var scope in _scopes)
            if (scope.Contains(name)) return true;
        if (_methodScope.Contains(name)) return true;
        if (_functions.ContainsKey(name)) return true;
        if (_types.Contains(name)) return true;
        if (BuiltinIdentifiers.Contains(name)) return true;
        if (BuiltinTypes.Contains(name)) return true;
        return false;
    }

    private static string TypeName(TypeRef t) => t switch
    {
        NamedTypeRef n => n.Name,
        GenericTypeRef g => $"{g.Name}<...>",
        OptionalTypeRef o => $"{TypeName(o.Inner)}?",
        _ => "?"
    };

    private void Report(string kind, string message, Superpower.Model.TextSpan span) =>
        _diagnostics.Add(new Diagnostic(kind, message, span));
}
