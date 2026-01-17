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
    private readonly Stack<Scope> _scopes = new();

    public IReadOnlyList<string> Output => _output;

    public void Run(List<Decl> declarations)
    {
        _output.Clear();
        _functions.Clear();
        _records.Clear();
        _enums.Clear();
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
            }
        }

        // Execute main if it exists
        if (_functions.TryGetValue("main", out var main))
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

        try
        {
            if (fn.Body != null)
            {
                return ExecuteBlock(fn.Body);
            }
            else if (fn.ExprBody != null)
            {
                return Evaluate(fn.ExprBody);
            }
            return null;
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
            if (result is ReturnValue rv)
                return rv.Value;
            if (result is BreakSignal or ContinueSignal)
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
                CurrentScope[varDecl.Name] = Evaluate(varDecl.Value);
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
