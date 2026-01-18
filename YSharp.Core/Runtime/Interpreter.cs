using YSharp.Core.Ast;

namespace YSharp.Core.Runtime;

/// <summary>
/// Simple AST interpreter for Y# - executes code directly without compilation.
/// Used for the language tour/playground to show immediate results.
/// </summary>
public class Interpreter
{
    private readonly List<string> _output = [];
    private readonly Dictionary<string, FnDecl> _functions = [];
    private readonly Dictionary<string, RecordDecl> _records = [];
    private readonly Dictionary<string, EnumDecl> _enums = [];
    private readonly Dictionary<string, ErrorDecl> _errors = [];
    private readonly Dictionary<string, ServiceDecl> _services = [];
    private readonly Dictionary<string, InterfaceDecl> _interfaces = [];
    private readonly Dictionary<string, string> _bindings = []; // interface -> service
    private readonly Dictionary<string, object> _singletons = [];
    private readonly Stack<Scope> _scopes = new();

    public IReadOnlyList<string> Output => _output;

    public void Run(List<Decl> declarations)
    {
        _output.Clear();
        _functions.Clear();
        _records.Clear();
        _enums.Clear();
        _errors.Clear();
        _services.Clear();
        _interfaces.Clear();
        _bindings.Clear();
        _singletons.Clear();
        _scopes.Clear();
        _scopes.Push(new Scope());

        // First pass: collect declarations
        foreach (var decl in declarations)
        {
            switch (decl)
            {
                case FnDecl fn:
                    _functions[fn.Name] = fn;
                    break;
                case RecordDecl rec:
                    _records[rec.Name] = rec;
                    break;
                case EnumDecl enm:
                    _enums[enm.Name] = enm;
                    break;
                case ErrorDecl err:
                    _errors[err.Name] = err;
                    break;
                case ServiceDecl svc:
                    _services[svc.Name] = svc;
                    break;
                case InterfaceDecl iface:
                    _interfaces[iface.Name] = iface;
                    break;
                case ModuleDecl mod:
                    // Register bindings from module
                    foreach (var binding in mod.Bindings)
                    {
                        _bindings[binding.Interface] = binding.Implementation;
                    }
                    break;
            }
        }

        // Check for app declaration (composition root with DI)
        var appDecl = declarations.OfType<AppDecl>().FirstOrDefault();
        if (appDecl != null)
        {
            // Resolve dependencies for app's main function
            var args = new List<object?>();
            foreach (var param in appDecl.MainFn.Params)
            {
                var typeName = param.Type switch
                {
                    NamedTypeRef named => named.Name,
                    _ => ""
                };
                args.Add(ResolveService(typeName));
            }
            ExecuteFunction(appDecl.MainFn, args);
        }
        // Otherwise execute standalone main if it exists
        else if (_functions.TryGetValue("main", out var main))
        {
            ExecuteFunction(main, []);
        }
    }

    private object? ExecuteFunction(FnDecl fn, List<object?> args)
    {
        _scopes.Push(new Scope());

        // Bind parameters
        for (int i = 0; i < fn.Params.Count && i < args.Count; i++)
        {
            CurrentScope[fn.Params[i].Name] = args[i];
        }

        // Check for modifiers
        var hasLog = fn.Modifiers.Any(m => m.Name == "log");
        var hasTimed = fn.Modifiers.Any(m => m.Name == "timed");
        System.Diagnostics.Stopwatch? stopwatch = null;

        // Pre-execution modifier behavior
        if (hasLog)
            _output.Add($"[log] entering {fn.Name}");
        if (hasTimed)
            stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            object? result = null;
            if (fn.Body != null)
            {
                var blockResult = ExecuteBlock(fn.Body);
                if (blockResult is ReturnValue rv)
                    result = rv.Value;
                else if (blockResult is PropagateError pe)
                    result = pe.Result;
                else
                    result = blockResult;
            }
            else if (fn.ExprBody != null)
            {
                result = Evaluate(fn.ExprBody);
            }

            // Post-execution modifier behavior
            stopwatch?.Stop();
            if (hasTimed)
                _output.Add($"[timed] {fn.Name} completed in {stopwatch!.ElapsedMilliseconds}ms");
            if (hasLog)
                _output.Add($"[log] exiting {fn.Name}");

            return result;
        }
        finally
        {
            _scopes.Pop();
        }
    }

    private object? ExecuteBlock(BlockStmt block)
    {
        foreach (var stmt in block.Statements)
        {
            var result = ExecuteStatement(stmt);
            if (result is ReturnValue or BreakSignal or ContinueSignal or PropagateError)
                return result;
        }
        return null;
    }

    private object? ExecuteStatement(Stmt stmt)
    {
        switch (stmt)
        {
            case ExprStmt expr:
                Evaluate(expr.Expression);
                return null;

            case ReturnStmt ret:
                return new ReturnValue(ret.Value != null ? Evaluate(ret.Value) : null);

            case VarDeclStmt varDecl:
                var value = Evaluate(varDecl.Value);
                if (value is PropagateError)
                    return value;
                CurrentScope[varDecl.Name] = value;
                return null;

            case AssignStmt assign:
                SetVariable(assign.Target, Evaluate(assign.Value));
                return null;

            case IfStmt ifStmt:
                var condition = Evaluate(ifStmt.Condition);
                if (IsTruthy(condition))
                {
                    var result = ExecuteBlock(ifStmt.ThenBlock);
                    if (result is ReturnValue or BreakSignal or ContinueSignal) return result;
                }
                else if (ifStmt.ElseBlock != null)
                {
                    var result = ExecuteBlock(ifStmt.ElseBlock);
                    if (result is ReturnValue or BreakSignal or ContinueSignal) return result;
                }
                return null;

            case ForStmt forStmt:
                return ExecuteFor(forStmt);


            case BreakStmt:
                return new BreakSignal();

            case ContinueStmt:
                return new ContinueSignal();

            case BlockStmt block:
                return ExecuteBlock(block);

            default:
                return null;
        }
    }

    private object? ExecuteFor(ForStmt forStmt)
    {
        _scopes.Push(new Scope());
        try
        {
            if (forStmt.Variable == null)
            {
                // While-style: for condition { }
                while (IsTruthy(Evaluate(forStmt.Iterable)))
                {
                    var result = ExecuteBlock(forStmt.Body);
                    if (result is ReturnValue) return result;
                    if (result is BreakSignal) break;
                }
            }
            else if (forStmt.Iterable is RangeExpr range)
            {
                // Range: for i in 0..10 { }
                var start = ToInt(Evaluate(range.Start));
                var end = ToInt(Evaluate(range.End));
                for (int i = start; i < end; i++)
                {
                    CurrentScope[forStmt.Variable] = i;
                    var result = ExecuteBlock(forStmt.Body);
                    if (result is ReturnValue) return result;
                    if (result is BreakSignal) break;
                }
            }
            else
            {
                // Foreach: for item in collection { }
                var collection = Evaluate(forStmt.Iterable);
                if (collection is IEnumerable<object?> items)
                {
                    foreach (var item in items)
                    {
                        CurrentScope[forStmt.Variable] = item;
                        var result = ExecuteBlock(forStmt.Body);
                        if (result is ReturnValue) return result;
                        if (result is BreakSignal) break;
                    }
                }
            }
            return null;
        }
        finally
        {
            _scopes.Pop();
        }
    }


    private object? Evaluate(Expr expr)
    {
        switch (expr)
        {
            case IntLiteralExpr i:
                return i.Value;

            case DoubleLiteralExpr d:
                return d.Value;

            case BoolLiteralExpr b:
                return b.Value;

            case StringLiteralExpr s:
                return s.Value;

            case NoneExpr:
                return null;

            case SomeExpr some:
                return Evaluate(some.Value);

            case IdentifierExpr id:
                return GetVariable(id.Name);

            case BinaryExpr bin:
                return EvaluateBinary(bin);

            case UnaryExpr unary:
                return EvaluateUnary(unary);

            case CallExpr call:
                return EvaluateCall(call);

            case MemberAccessExpr member:
                return EvaluateMemberAccess(member);

            case ArrayExpr arr:
                return arr.Elements.Select(Evaluate).ToList();

            case IndexAccessExpr idx:
                return EvaluateIndex(idx);

            case InterpolatedStringExpr interp:
                return EvaluateInterpolatedString(interp);

            case MatchExpr match:
                return EvaluateMatch(match);


            case WithExpr with:
                return EvaluateWith(with);

            case RangeExpr range:
                var start = ToInt(Evaluate(range.Start));
                var end = ToInt(Evaluate(range.End));
                return Enumerable.Range(start, end - start).Cast<object?>().ToList();

            case LambdaExpr lambda:
                return new LambdaValue(lambda, CaptureScope());

            case TryExpr tryExpr:
                var result = Evaluate(tryExpr.Operand);
                if (result is ResultValue rv)
                {
                    if (rv.IsError)
                        return new PropagateError(rv);
                    return rv.Value;
                }
                return result;

            default:
                return null;
        }
    }

    private object? EvaluateBinary(BinaryExpr bin)
    {
        // Short-circuit for logical operators
        if (bin.Op == "&&")
        {
            var left = Evaluate(bin.Left);
            if (!IsTruthy(left)) return false;
            return IsTruthy(Evaluate(bin.Right));
        }
        if (bin.Op == "||")
        {
            var left = Evaluate(bin.Left);
            if (IsTruthy(left)) return true;
            return IsTruthy(Evaluate(bin.Right));
        }

        var l = Evaluate(bin.Left);
        var r = Evaluate(bin.Right);

        // Numeric promotion: if either is double, promote both
        if (l is int li && r is double rd) { l = (double)li; }
        if (l is double ld && r is int ri) { r = (double)ri; }

        return bin.Op switch
        {
            "+" when l is int a && r is int b => a + b,
            "+" when l is double a && r is double b => a + b,
            "+" when l is string || r is string => $"{l}{r}",
            "-" when l is int a && r is int b => a - b,
            "-" when l is double a && r is double b => a - b,
            "*" when l is int a && r is int b => a * b,
            "*" when l is double a && r is double b => a * b,
            "/" when l is int a && r is int b => a / b,
            "/" when l is double a && r is double b => a / b,
            "%" when l is int a && r is int b => a % b,
            "==" => Equals(l, r),
            "!=" => !Equals(l, r),
            "<" when l is int a && r is int b => a < b,
            "<" when l is double a && r is double b => a < b,
            ">" when l is int a && r is int b => a > b,
            ">" when l is double a && r is double b => a > b,
            "<=" when l is int a && r is int b => a <= b,
            "<=" when l is double a && r is double b => a <= b,
            ">=" when l is int a && r is int b => a >= b,
            ">=" when l is double a && r is double b => a >= b,
            _ => null
        };
    }

    private object? EvaluateUnary(UnaryExpr unary)
    {
        var operand = Evaluate(unary.Operand);
        return unary.Op switch
        {
            "-" when operand is int i => -i,
            "-" when operand is double d => -d,
            "!" when operand is bool b => !b,
            "!" => !IsTruthy(operand),
            _ => null
        };
    }

    private object? EvaluateCall(CallExpr call)
    {
        // Handle print specially
        if (call.Target is IdentifierExpr { Name: "print" })
        {
            var args = call.Args.Select(Evaluate).ToList();
            var output = string.Join(" ", args.Select(FormatValue));
            _output.Add(output);
            return null;
        }

        // Handle resolve<Interface>() for DI
        if (call.Target is IdentifierExpr { Name: "resolve" } && call.TypeArgs.Count > 0)
        {
            var typeArg = call.TypeArgs[0];
            var interfaceName = typeArg switch
            {
                NamedTypeRef named => named.Name,
                _ => typeArg.ToString() ?? ""
            };
            return ResolveService(interfaceName);
        }

        // Handle record construction
        if (call.Target is IdentifierExpr id && _records.TryGetValue(id.Name, out var recordDecl))
        {
            var args = call.Args.Select(Evaluate).ToList();
            var instance = new RecordInstance(recordDecl.Name);
            for (int i = 0; i < recordDecl.Fields.Count && i < args.Count; i++)
            {
                instance.Fields[recordDecl.Fields[i].Name] = args[i];
            }
            return instance;
        }

        // Handle Error.Validation, Error.NotFound, etc. (built-in error types)
        if (call.Target is MemberAccessExpr errorAccess &&
            errorAccess.Target is IdentifierExpr errorId &&
            errorId.Name == "Error")
        {
            var args = call.Args.Select(Evaluate).ToList();
            var message = args.Count > 0 ? args[0]?.ToString() ?? "" : "";
            return new ResultValue(null, new ErrorValue(errorAccess.Member, message));
        }

        // Handle custom error type construction (e.g., TrailError.PermitRequired(...))
        if (call.Target is MemberAccessExpr customErrorAccess &&
            customErrorAccess.Target is IdentifierExpr customErrorName &&
            _errors.TryGetValue(customErrorName.Name, out var errorDecl))
        {
            var variant = errorDecl.Variants.FirstOrDefault(v => v.Name == customErrorAccess.Member);
            if (variant != null)
            {
                var args = call.Args.Select(Evaluate).ToList();
                var errorInstance = new CustomErrorInstance(customErrorName.Name, variant.Name);
                for (int i = 0; i < variant.Fields.Count && i < args.Count; i++)
                {
                    errorInstance.Fields[variant.Fields[i].Name] = args[i];
                }
                return new ResultValue(null, errorInstance);
            }
        }

        // Handle enum variant construction (no args = singleton)
        if (call.Target is MemberAccessExpr enumAccess &&
            enumAccess.Target is IdentifierExpr enumName &&
            _enums.TryGetValue(enumName.Name, out var enumDecl))
        {
            var variant = enumDecl.Variants.FirstOrDefault(v => v.Name == enumAccess.Member);
            if (variant != null)
            {
                var args = call.Args.Select(Evaluate).ToList();
                var instance = new EnumInstance(enumName.Name, variant.Name);
                for (int i = 0; i < variant.Fields.Count && i < args.Count; i++)
                {
                    instance.Fields[variant.Fields[i].Name] = args[i];
                }
                return instance;
            }
        }

        // Handle user-defined functions
        if (call.Target is IdentifierExpr fnId && _functions.TryGetValue(fnId.Name, out var fn))
        {
            var args = call.Args.Select(Evaluate).ToList();
            return ExecuteFunction(fn, args);
        }

        // Handle lambda calls
        if (call.Target is IdentifierExpr lambdaId)
        {
            var maybeVal = GetVariable(lambdaId.Name);
            if (maybeVal is LambdaValue lambda)
            {
                return ExecuteLambda(lambda, call.Args.Select(Evaluate).ToList());
            }
        }

        // Handle method calls on objects
        if (call.Target is MemberAccessExpr methodCall)
        {
            var target = Evaluate(methodCall.Target);
            var args = call.Args.Select(Evaluate).ToList();
            return EvaluateMethodCall(target, methodCall.Member, args);
        }

        return null;
    }

    private object? ExecuteLambda(LambdaValue lambda, List<object?> args)
    {
        _scopes.Push(new Scope(lambda.CapturedScope));

        for (int i = 0; i < lambda.Lambda.Parameters.Count && i < args.Count; i++)
        {
            CurrentScope[lambda.Lambda.Parameters[i]] = args[i];
        }

        try
        {
            return Evaluate(lambda.Lambda.Body);
        }
        finally
        {
            _scopes.Pop();
        }
    }

    private object? EvaluateMethodCall(object? target, string method, List<object?> args)
    {
        // List methods - using if/else for side effects
        if (target is List<object?> list)
        {
            if (method is "len" or "length" or "count")
                return list.Count;
            if (method == "push")
            {
                list.Add(args.FirstOrDefault());
                return null;
            }
            if (method == "pop" && list.Count > 0)
            {
                var last = list[^1];
                list.RemoveAt(list.Count - 1);
                return last;
            }
            if (method == "first" && list.Count > 0)
                return list[0];
            if (method == "last" && list.Count > 0)
                return list[^1];
            if (method == "isEmpty")
                return list.Count == 0;
        }

        // String methods
        if (target is string s)
        {
            return method switch
            {
                "len" or "length" => s.Length,
                "upper" => s.ToUpper(),
                "lower" => s.ToLower(),
                "trim" => s.Trim(),
                "contains" => args.Count > 0 && s.Contains(args[0]?.ToString() ?? ""),
                "startsWith" => args.Count > 0 && s.StartsWith(args[0]?.ToString() ?? ""),
                "endsWith" => args.Count > 0 && s.EndsWith(args[0]?.ToString() ?? ""),
                "split" => args.Count > 0
                    ? s.Split(args[0]?.ToString() ?? " ").Cast<object?>().ToList()
                    : s.Split(' ').Cast<object?>().ToList(),
                "replace" => args.Count >= 2
                    ? s.Replace(args[0]?.ToString() ?? "", args[1]?.ToString() ?? "")
                    : s,
                "substring" => args.Count >= 2
                    ? s.Substring(ToInt(args[0]), ToInt(args[1]))
                    : args.Count == 1 ? s.Substring(ToInt(args[0])) : s,
                _ => null
            };
        }

        // Record methods
        if (target is RecordInstance record)
        {
            if (method == "toString")
                return record.ToString();
        }

        // Service methods
        if (target is ServiceInstance service)
        {
            return ExecuteServiceMethod(service, method, args);
        }

        // Result combinators - work on ResultValue or treat plain values as Ok
        if (method == "Map" && args.Count > 0 && args[0] is LambdaValue mapLambda)
        {
            if (target is ResultValue rv)
            {
                if (rv.IsError) return rv;
                var mapped = ExecuteLambda(mapLambda, [rv.Value]);
                return new ResultValue(mapped);
            }
            // Plain value treated as Ok
            var result = ExecuteLambda(mapLambda, [target]);
            return new ResultValue(result);
        }

        if (method == "Then" && args.Count > 0 && args[0] is LambdaValue thenLambda)
        {
            if (target is ResultValue rv)
            {
                if (rv.IsError) return rv;
                var result = ExecuteLambda(thenLambda, [rv.Value]);
                // Then expects the lambda to return a Result
                if (result is ResultValue) return result;
                return new ResultValue(result);
            }
            // Plain value treated as Ok
            var thenResult = ExecuteLambda(thenLambda, [target]);
            if (thenResult is ResultValue) return thenResult;
            return new ResultValue(thenResult);
        }

        if (method == "UnwrapOr" && args.Count > 0)
        {
            if (target is ResultValue rv)
            {
                return rv.IsOk ? rv.Value : args[0];
            }
            // Plain value treated as Ok, return the value
            return target;
        }

        return null;
    }

    private object? EvaluateMemberAccess(MemberAccessExpr member)
    {
        // Check for enum variant without args first
        if (member.Target is IdentifierExpr enumId && _enums.TryGetValue(enumId.Name, out var enumDecl))
        {
            var variant = enumDecl.Variants.FirstOrDefault(v => v.Name == member.Member);
            if (variant != null && variant.Fields.Count == 0)
            {
                return new EnumInstance(enumId.Name, variant.Name);
            }
        }

        var target = Evaluate(member.Target);

        if (target is RecordInstance record)
        {
            return record.Fields.GetValueOrDefault(member.Member);
        }

        if (target is EnumInstance enumInst)
        {
            return enumInst.Fields.GetValueOrDefault(member.Member);
        }

        if (target is ResultValue result)
        {
            return member.Member switch
            {
                "IsOk" => result.IsOk,
                "IsError" => result.IsError,
                "Value" => result.Value,
                "Error" => result.Error,
                _ => null
            };
        }

        // For non-ResultValue types, treat as Ok result (for functions returning Result<T>)
        if (member.Member == "IsOk") return target != null;
        if (member.Member == "IsError") return target == null;
        if (member.Member == "Value") return target;

        return null;
    }

    private object? EvaluateIndex(IndexAccessExpr idx)
    {
        var target = Evaluate(idx.Target);
        var index = Evaluate(idx.Index);

        if (target is List<object?> list && index is int i)
        {
            return i >= 0 && i < list.Count ? list[i] : null;
        }

        if (target is string s && index is int si)
        {
            return si >= 0 && si < s.Length ? s[si].ToString() : null;
        }

        return null;
    }

    private string EvaluateInterpolatedString(InterpolatedStringExpr interp)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in interp.Parts)
        {
            switch (part)
            {
                case InterpolatedText text:
                    sb.Append(text.Text);
                    break;
                case InterpolatedExpr expr:
                    sb.Append(FormatValue(Evaluate(expr.Expression)));
                    break;
            }
        }
        return sb.ToString();
    }

    private object? EvaluateMatch(MatchExpr match)
    {
        var value = Evaluate(match.Value);

        foreach (var arm in match.Arms)
        {
            if (MatchPattern(value, arm.Pattern, out var bindings))
            {
                _scopes.Push(new Scope());
                try
                {
                    foreach (var (name, val) in bindings)
                    {
                        CurrentScope[name] = val;
                    }
                    return Evaluate(arm.Result);
                }
                finally
                {
                    _scopes.Pop();
                }
            }
        }

        return null;
    }

    private bool MatchPattern(object? value, Expr pattern, out List<(string, object?)> bindings)
    {
        bindings = [];

        switch (pattern)
        {
            case WildcardExpr:
                return true;

            case IntLiteralExpr i:
                return Equals(value, i.Value);

            case BoolLiteralExpr b:
                return Equals(value, b.Value);

            case StringLiteralExpr s:
                return Equals(value, s.Value);

            case IdentifierExpr id:
                // Check if it's an enum variant match
                if (value is EnumInstance enumVal && id.Name == enumVal.VariantName)
                    return true;
                // Otherwise bind the value
                bindings.Add((id.Name, value));
                return true;

            case MemberAccessExpr memberPattern when value is EnumInstance enumInst:
                // Match Enum.Variant
                if (memberPattern.Target is IdentifierExpr enumType &&
                    enumType.Name == enumInst.EnumName &&
                    memberPattern.Member == enumInst.VariantName)
                    return true;
                return false;

            case CallExpr callPattern when value is EnumInstance enumInst:
                // Match Enum.Variant(fields) with destructuring
                if (callPattern.Target is MemberAccessExpr variantAccess &&
                    variantAccess.Target is IdentifierExpr enumTypeId &&
                    enumTypeId.Name == enumInst.EnumName &&
                    variantAccess.Member == enumInst.VariantName)
                {
                    // Extract field bindings
                    var fieldNames = enumInst.Fields.Keys.ToList();
                    for (int i = 0; i < callPattern.Args.Count && i < fieldNames.Count; i++)
                    {
                        if (callPattern.Args[i] is IdentifierExpr fieldId)
                        {
                            bindings.Add((fieldId.Name, enumInst.Fields[fieldNames[i]]));
                        }
                    }
                    return true;
                }
                return false;

            default:
                return false;
        }
    }


    private object? EvaluateWith(WithExpr with)
    {
        var baseVal = Evaluate(with.Base);
        if (baseVal is not RecordInstance original) return null;

        var copy = new RecordInstance(original.TypeName);
        foreach (var (key, val) in original.Fields)
        {
            copy.Fields[key] = val;
        }

        foreach (var (name, valueExpr) in with.Updates)
        {
            copy.Fields[name] = Evaluate(valueExpr);
        }

        return copy;
    }

    private object? ResolveService(string interfaceName)
    {
        // Look up binding: interface -> service implementation
        if (!_bindings.TryGetValue(interfaceName, out var serviceName))
        {
            // No binding found, try direct service lookup
            serviceName = interfaceName;
        }

        if (!_services.TryGetValue(serviceName, out var serviceDecl))
        {
            return null;
        }

        // Check if singleton already exists
        if (serviceDecl.Lifetime == ServiceLifetime.Singleton)
        {
            if (_singletons.TryGetValue(serviceName, out var existing))
            {
                return existing;
            }
        }

        // Create service instance
        var instance = new ServiceInstance(serviceDecl);

        // Store singleton
        if (serviceDecl.Lifetime == ServiceLifetime.Singleton)
        {
            _singletons[serviceName] = instance;
        }

        return instance;
    }

    private object? ExecuteServiceMethod(ServiceInstance service, string methodName, List<object?> args)
    {
        var method = service.ServiceDecl.Methods.FirstOrDefault(m => m.Name == methodName);
        if (method == null) return null;

        _scopes.Push(new Scope());
        try
        {
            // Bind parameters
            for (int i = 0; i < method.Params.Count && i < args.Count; i++)
            {
                CurrentScope[method.Params[i].Name] = args[i];
            }

            if (method.Body != null)
            {
                var result = ExecuteBlock(method.Body);
                if (result is ReturnValue rv) return rv.Value;
                return result;
            }
            else if (method.ExprBody != null)
            {
                return Evaluate(method.ExprBody);
            }

            return null;
        }
        finally
        {
            _scopes.Pop();
        }
    }

    private Scope CurrentScope => _scopes.Peek();

    private Dictionary<string, object?> CaptureScope()
    {
        var captured = new Dictionary<string, object?>();
        foreach (var scope in _scopes.Reverse())
        {
            foreach (var (key, value) in scope)
            {
                captured.TryAdd(key, value);
            }
        }
        return captured;
    }

    private object? GetVariable(string name)
    {
        foreach (var scope in _scopes)
        {
            if (scope.TryGetValue(name, out var value))
                return value;
        }
        return null;
    }

    private void SetVariable(string name, object? value)
    {
        foreach (var scope in _scopes)
        {
            if (scope.ContainsKey(name))
            {
                scope[name] = value;
                return;
            }
        }
        CurrentScope[name] = value;
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        double d => d != 0,
        string s => s.Length > 0,
        _ => true
    };

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        double d => (int)d,
        string s when int.TryParse(s, out var i) => i,
        _ => 0
    };

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => s,
        RecordInstance r => r.ToString(),
        EnumInstance e => e.ToString(),
        List<object?> list => $"[{string.Join(", ", list.Select(FormatValue))}]",
        _ => value.ToString() ?? "null"
    };

    // Helper types
    private class Scope : Dictionary<string, object?>
    {
        public Scope() { }
        public Scope(Dictionary<string, object?> captured)
        {
            foreach (var (k, v) in captured)
                this[k] = v;
        }
    }

    private record ReturnValue(object? Value);
    private record BreakSignal;
    private record ContinueSignal;
    private record PropagateError(ResultValue Result);
}

// Runtime value types
public class RecordInstance(string typeName)
{
    public string TypeName { get; } = typeName;
    public Dictionary<string, object?> Fields { get; } = [];

    public override string ToString()
    {
        var fields = string.Join(", ", Fields.Select(f => $"{f.Key}: {FormatValue(f.Value)}"));
        return $"{TypeName} {{ {fields} }}";
    }

    private static string FormatValue(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => $"\"{s}\"",
        _ => v.ToString() ?? "null"
    };
}

public class EnumInstance(string enumName, string variantName)
{
    public string EnumName { get; } = enumName;
    public string VariantName { get; } = variantName;
    public Dictionary<string, object?> Fields { get; } = [];

    public override string ToString()
    {
        if (Fields.Count == 0)
            return $"{EnumName}.{VariantName}";
        var fields = string.Join(", ", Fields.Select(f => $"{f.Key}: {f.Value}"));
        return $"{EnumName}.{VariantName}({fields})";
    }
}

public class LambdaValue(LambdaExpr lambda, Dictionary<string, object?> capturedScope)
{
    public LambdaExpr Lambda { get; } = lambda;
    public Dictionary<string, object?> CapturedScope { get; } = capturedScope;
}

public record ErrorValue(string Kind, string Message)
{
    public override string ToString() => $"Error.{Kind}(\"{Message}\")";
}

public class CustomErrorInstance(string errorType, string variantName)
{
    public string ErrorType { get; } = errorType;
    public string VariantName { get; } = variantName;
    public Dictionary<string, object?> Fields { get; } = [];

    public override string ToString()
    {
        if (Fields.Count == 0)
            return $"{ErrorType}.{VariantName}";
        var fieldStr = string.Join(", ", Fields.Select(f => $"{f.Key}: {f.Value}"));
        return $"{ErrorType}.{VariantName}({fieldStr})";
    }
}

public class ResultValue
{
    public object? Value { get; }
    public object? Error { get; }
    public bool IsOk => Error == null;
    public bool IsError => Error != null;

    public ResultValue(object? value, object? error = null)
    {
        Value = value;
        Error = error;
    }

    public override string ToString() => IsError ? Error!.ToString()! : Value?.ToString() ?? "null";
}

public class ServiceInstance(ServiceDecl serviceDecl)
{
    public ServiceDecl ServiceDecl { get; } = serviceDecl;

    public override string ToString() => $"<{ServiceDecl.Name}>";
}
