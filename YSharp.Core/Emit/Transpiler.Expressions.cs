using YSharp.Core.Ast;

namespace YSharp.Core.Emit;

public partial class Transpiler
{
    private void TranspileExpression(Expr expr) => (expr switch
    {
        StringLiteralExpr str => (Action)(() => _sb.Append($"\"{Escape(str.Value)}\"")),
        InterpolatedStringExpr interp => () => TranspileInterpolatedString(interp),
        IntLiteralExpr num => () => _sb.Append(num.Value),
        DoubleLiteralExpr dbl => () => _sb.Append(dbl.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        BoolLiteralExpr b => () => _sb.Append(b.Value ? "true" : "false"),
        NoneExpr => () => _sb.Append("null"),
        SomeExpr some => () => TranspileExpression(some.Value),
        IdentifierExpr id => () => _sb.Append(EscapeIdent(id.Name)),
        BinaryExpr bin => () => TranspileBinary(bin),
        UnaryExpr unary => () => TranspileUnary(unary),
        TryExpr tryExpr => () => TranspileTryExpr(tryExpr),
        LambdaExpr lambda => () => TranspileLambda(lambda),
        CallExpr call => () => TranspileCall(call),
        MemberAccessExpr member => () => TranspileMemberAccess(member),
        ThisExpr => () => _sb.Append("this"),
        WildcardExpr => () => _sb.Append("_"),
        MatchExpr matchExpr => () => TranspileMatch(matchExpr),
        RangeExpr range => () => TranspileRange(range),
        ArrayExpr array => () => TranspileArray(array),
        IndexAccessExpr indexAccess => () => TranspileIndexAccess(indexAccess),
        BlockingExpr blockingExpr => () => TranspileBlockingExpr(blockingExpr),
        ConcurrentExpr concurrentExpr => () => TranspileConcurrent(concurrentExpr),
        WithExpr withExpr => () => TranspileWith(withExpr),
        ScopeExpr scopeExpr => () => TranspileScope(scopeExpr),
        _ => throw new NotSupportedException($"Expression type not supported: {expr.GetType().Name}")
    })();

    private void TranspileBinary(BinaryExpr bin)
    {
        _sb.Append("(");
        TranspileExpression(bin.Left);
        _sb.Append($" {bin.Op} ");
        TranspileExpression(bin.Right);
        _sb.Append(")");
    }

    private void TranspileUnary(UnaryExpr unary)
    {
        _sb.Append($"({unary.Op}");
        TranspileExpression(unary.Operand);
        _sb.Append(")");
    }

    private void TranspileTryExpr(TryExpr tryExpr)
    {
        TranspileExpression(tryExpr.Operand);
        _sb.Append(".Value");
    }

    private void TranspileLambda(LambdaExpr lambda)
    {
        _sb.Append(lambda.Parameters.Count == 1
            ? $"{lambda.Parameters[0]} => "
            : $"({string.Join(", ", lambda.Parameters)}) => ");
        TranspileExpression(lambda.Body);
    }

    private void TranspileMemberAccess(MemberAccessExpr member)
    {
        // `.length` is the polymorphic Y# length accessor; route through a tiny
        // runtime helper so it works on both strings and any collection.
        if (member.Member == "length")
        {
            _usesLength = true;
            _sb.Append("__Y.Length(");
            TranspileExpression(member.Target);
            _sb.Append(")");
            return;
        }

        TranspileExpression(member.Target);
        _sb.Append($".{Capitalize(member.Member)}");
    }

    private void TranspileRange(RangeExpr range)
    {
        _sb.Append("Enumerable.Range(");
        TranspileExpression(range.Start);
        _sb.Append(", ");
        TranspileExpression(range.End);
        _sb.Append(" - ");
        TranspileExpression(range.Start);
        _sb.Append(")");
    }

    private void TranspileArray(ArrayExpr array)
    {
        if (array.Elements.Count == 0)
        {
            // Empty literal — element type can't be inferred. Caller's typed slot
            // (field, assignment) determines T, but we don't have that context here.
            _sb.Append("new List<object>()");
            return;
        }

        _sb.Append("new[] { ");
        _sb.Append(string.Join(", ", array.Elements.Select(e =>
        {
            var start = _sb.Length;
            TranspileExpression(e);
            var result = _sb.ToString(start, _sb.Length - start);
            _sb.Length = start;
            return result;
        })));
        _sb.Append(" }.ToList()");
    }

    private void TranspileIndexAccess(IndexAccessExpr indexAccess)
    {
        TranspileExpression(indexAccess.Target);
        _sb.Append("[");
        TranspileExpression(indexAccess.Index);
        _sb.Append("]");
    }

    private void TranspileBlockingExpr(BlockingExpr blockingExpr)
    {
        _sb.AppendLine("((Action)(() => {");
        _indent++;
        foreach (var stmt in blockingExpr.Statements)
            TranspileStatement(stmt);
        _indent--;
        Append("}))()");
    }

    private void TranspileBlockingStmt(BlockingExpr blocking)
    {
        foreach (var stmt in blocking.Statements)
            TranspileStatement(stmt);
    }

    private void TranspileWith(WithExpr withExpr)
    {
        TranspileExpression(withExpr.Base);
        _sb.Append(" with { ");
        _sb.Append(string.Join(", ", withExpr.Updates.Select(u =>
        {
            var start = _sb.Length;
            _sb.Append($"{Capitalize(u.Name)} = ");
            TranspileExpression(u.Value);
            var result = _sb.ToString(start, _sb.Length - start);
            _sb.Length = start;
            return result;
        })));
        _sb.Append(" }");
    }

    private void TranspileScope(ScopeExpr scopeExpr)
    {
        _sb.AppendLine("((Func<Task>(async () => {");
        _indent++;
        AppendLine("using var _scope = provider.CreateScope();");
        AppendLine("var scopedProvider = _scope.ServiceProvider;");
        foreach (var stmt in scopeExpr.Statements)
            TranspileStatement(stmt);
        _indent--;
        Append("}))()");
    }

    private void TranspileInterpolatedString(InterpolatedStringExpr interp)
    {
        _sb.Append("$\"");
        foreach (var part in interp.Parts)
        {
            (part switch
            {
                InterpolatedText text => (Action)(() => _sb.Append(Escape(text.Text))),
                InterpolatedExpr expr => () =>
                {
                    _sb.Append("{");
                    TranspileExpression(expr.Expression);
                    _sb.Append("}");
                },
                _ => () => { }
            })();
        }
        _sb.Append("\"");
    }

    private void TranspileMatch(MatchExpr match)
    {
        TranspileExpression(match.Value);
        _sb.Append(" switch { ");
        _sb.Append(string.Join(", ", match.Arms.Select(arm =>
        {
            var start = _sb.Length;
            TranspileMatchArm(arm);
            var result = _sb.ToString(start, _sb.Length - start);
            _sb.Length = start;
            return result;
        })));
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
        var typeArgs = call.TypeArgs.Count > 0
            ? $"<{string.Join(", ", call.TypeArgs.Select(TranspileType))}>"
            : "";

        (call.Target switch
        {
            IdentifierExpr id => (Action)(() => TranspileIdentifierCall(id, call, typeArgs)),
            MemberAccessExpr member => () => TranspileMemberCall(member, call),
            _ => () =>
            {
                TranspileExpression(call.Target);
                _sb.Append("(");
                TranspileArgs(call.Args);
                _sb.Append(")");
            }
        })();
    }

    private void TranspileIdentifierCall(IdentifierExpr id, CallExpr call, string typeArgs)
    {
        if (id.Name == "print")
        {
            _sb.Append("Console.WriteLine(");
            TranspileArgs(call.Args);
            _sb.Append(")");
            return;
        }

        if (_typeNames.Contains(id.Name))
        {
            _sb.Append($"new {id.Name}{typeArgs}(");
            TranspileArgs(call.Args);
            _sb.Append(")");
            return;
        }

        if (_asyncFunctions.Contains(id.Name))
            _sb.Append("await ");

        _sb.Append($"{Capitalize(id.Name)}{typeArgs}(");
        TranspileArgs(call.Args);
        _sb.Append(")");
    }

    private void TranspileMemberCall(MemberAccessExpr member, CallExpr call)
    {
        if (member.Target is IdentifierExpr enumName)
        {
            var fullName = $"{enumName.Name}.{member.Member}";
            if (_typeNames.Contains(fullName))
            {
                _sb.Append($"new {enumName.Name}.{member.Member}(");
                TranspileArgs(call.Args);
                _sb.Append(")");
                return;
            }

            if (enumName.Name == "Error")
            {
                _sb.Append($"Error.{member.Member}(");
                TranspileArgs(call.Args);
                _sb.Append(")");
                return;
            }
        }

        if (member.Target is IdentifierExpr targetId && _asyncFunctions.Contains($"{targetId.Name}"))
            _sb.Append("await ");

        TranspileExpression(member.Target);
        _sb.Append($".{Capitalize(member.Member)}(");
        TranspileArgs(call.Args);
        _sb.Append(")");
    }

    private void TranspileConcurrent(ConcurrentExpr concurrent)
    {
        var tasks = new List<(string varName, string taskName)>();
        var syncVars = new List<(string varName, Expr value)>();

        foreach (var stmt in concurrent.Statements)
        {
            if (stmt is VarDeclStmt varDecl)
            {
                if (IsCallAsync(varDecl.Value))
                {
                    var taskName = $"_task{_concurrentTaskCounter++}";
                    tasks.Add((varDecl.Name, taskName));

                    Append($"var {taskName} = ");
                    TranspileExpressionNoAwait(varDecl.Value);
                    _sb.AppendLine(";");
                }
                else
                {
                    syncVars.Add((varDecl.Name, varDecl.Value));
                }
            }
            else
            {
                TranspileStatement(stmt);
            }
        }

        if (tasks.Count > 0)
        {
            AppendLine($"await Task.WhenAll({string.Join(", ", tasks.Select(t => t.taskName))});");

            foreach (var (varName, taskName) in tasks)
                AppendLine($"var {varName} = {taskName}.Result;");
        }

        foreach (var (varName, value) in syncVars)
        {
            Append($"var {varName} = ");
            TranspileExpression(value);
            _sb.AppendLine(";");
        }
    }

    private void TranspileExpressionNoAwait(Expr expr)
    {
        if (expr is CallExpr call)
            TranspileCallNoAwait(call);
        else
            TranspileExpression(expr);
    }

    private void TranspileCallNoAwait(CallExpr call) => (call.Target switch
    {
        IdentifierExpr id => (Action)(() =>
        {
            var typeArgs = call.TypeArgs.Count > 0
                ? $"<{string.Join(", ", call.TypeArgs.Select(TranspileType))}>"
                : "";
            _sb.Append($"{Capitalize(id.Name)}{typeArgs}(");
            TranspileArgs(call.Args);
            _sb.Append(")");
        }),
        MemberAccessExpr member => () =>
        {
            TranspileExpression(member.Target);
            _sb.Append($".{Capitalize(member.Member)}(");
            TranspileArgs(call.Args);
            _sb.Append(")");
        },
        _ => () =>
        {
            TranspileExpression(call.Target);
            _sb.Append("(");
            TranspileArgs(call.Args);
            _sb.Append(")");
        }
    })();
}
