using YSharp.Compiler.Ast;

namespace YSharp.Compiler.Emit;

public partial class Transpiler
{
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
                TranspileExpression(some.Value);
                break;
            case IdentifierExpr id:
                _sb.Append(id.Name);
                break;
            case BinaryExpr bin:
                TranspileBinary(bin);
                break;
            case UnaryExpr unary:
                TranspileUnary(unary);
                break;
            case TryExpr tryExpr:
                TranspileTryExpr(tryExpr);
                break;
            case LambdaExpr lambda:
                TranspileLambda(lambda);
                break;
            case CallExpr call:
                TranspileCall(call);
                break;
            case MemberAccessExpr member:
                TranspileMemberAccess(member);
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
                TranspileRange(range);
                break;
            case ArrayExpr array:
                TranspileArray(array);
                break;
            case IndexAccessExpr indexAccess:
                TranspileIndexAccess(indexAccess);
                break;
            case BlockingExpr blockingExpr:
                TranspileBlockingExpr(blockingExpr);
                break;
            case ConcurrentExpr concurrentExpr:
                TranspileConcurrent(concurrentExpr);
                break;
            case WithExpr withExpr:
                TranspileWith(withExpr);
                break;
            case ScopeExpr scopeExpr:
                TranspileScope(scopeExpr);
                break;
            default:
                throw new NotSupportedException($"Expression type not supported: {expr.GetType().Name}");
        }
    }

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
        if (lambda.Parameters.Count == 1)
            _sb.Append($"{lambda.Parameters[0]} => ");
        else
            _sb.Append($"({string.Join(", ", lambda.Parameters)}) => ");
        TranspileExpression(lambda.Body);
    }

    private void TranspileMemberAccess(MemberAccessExpr member)
    {
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
        _sb.Append("new[] { ");
        for (int i = 0; i < array.Elements.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            TranspileExpression(array.Elements[i]);
        }
        _sb.Append(" }");
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
        for (int i = 0; i < withExpr.Updates.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            var (name, value) = withExpr.Updates[i];
            _sb.Append($"{Capitalize(name)} = ");
            TranspileExpression(value);
        }
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
        var typeArgs = call.TypeArgs.Count > 0
            ? $"<{string.Join(", ", call.TypeArgs.Select(TranspileType))}>"
            : "";

        if (call.Target is IdentifierExpr id)
        {
            TranspileIdentifierCall(id, call, typeArgs);
            return;
        }

        if (call.Target is MemberAccessExpr member)
        {
            TranspileMemberCall(member, call);
            return;
        }

        TranspileExpression(call.Target);
        _sb.Append("(");
        TranspileArgs(call.Args);
        _sb.Append(")");
    }

    private void TranspileIdentifierCall(IdentifierExpr id, CallExpr call, string typeArgs)
    {
        switch (id.Name)
        {
            case "print":
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
                    var varName = varDecl.Name;
                    var taskName = $"_task{_concurrentTaskCounter++}";
                    tasks.Add((varName, taskName));

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

    private void TranspileCallNoAwait(CallExpr call)
    {
        switch (call.Target)
        {
            case IdentifierExpr id:
                var typeArgs = call.TypeArgs.Count > 0
                    ? $"<{string.Join(", ", call.TypeArgs.Select(TranspileType))}>"
                    : "";
                _sb.Append($"{Capitalize(id.Name)}{typeArgs}(");
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
}
