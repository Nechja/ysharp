using YSharp.Compiler.Ast;

namespace YSharp.Compiler.Emit;

public partial class Transpiler
{
    private void TranspileStatement(Stmt stmt) => (stmt switch
    {
        ExprStmt expr => (Action)(() => TranspileExprStmt(expr)),
        ReturnStmt ret => () => TranspileReturn(ret),
        BreakStmt => () => AppendLine("break;"),
        ContinueStmt => () => AppendLine("continue;"),
        IfStmt ifStmt => () => TranspileIf(ifStmt),
        VarDeclStmt varDecl => () => TranspileVarDecl(varDecl),
        ForStmt forStmt => () => TranspileFor(forStmt),
        AssignStmt assign => () => TranspileAssign(assign),
        CompoundAssignStmt compound => () => TranspileCompoundAssign(compound),
        BlockStmt block => () => TranspileBlock(block),
        _ => throw new NotSupportedException($"Statement type not supported: {stmt.GetType().Name}")
    })();

    private void TranspileBlock(BlockStmt block)
    {
        AppendLine("{");
        _indent++;
        foreach (var s in block.Statements)
            TranspileStatement(s);
        _indent--;
        AppendLine("}");
    }

    private void TranspileExprStmt(ExprStmt expr) => (expr.Expression switch
    {
        TryExpr tryExpr => (Action)(() => TranspileTryStmt(tryExpr)),
        ConcurrentExpr concurrentExpr => () => TranspileConcurrent(concurrentExpr),
        BlockingExpr blockingExpr => () => TranspileBlockingStmt(blockingExpr),
        _ => () =>
        {
            Append("");
            TranspileExpression(expr.Expression);
            _sb.AppendLine(";");
        }
    })();

    private void TranspileReturn(ReturnStmt ret)
    {
        Append("return");
        if (ret.Value is not null)
        {
            _sb.Append(" ");
            TranspileExpression(ret.Value);
        }
        _sb.AppendLine(";");
    }

    private void TranspileIf(IfStmt ifStmt)
    {
        Append("if (");
        TranspileExpression(ifStmt.Condition);
        _sb.AppendLine(")");
        AppendLine("{");
        _indent++;
        foreach (var s in ifStmt.ThenBlock.Statements)
            TranspileStatement(s);
        _indent--;
        AppendLine("}");

        if (ifStmt.ElseBlock is not null)
        {
            AppendLine("else");
            AppendLine("{");
            _indent++;
            foreach (var s in ifStmt.ElseBlock.Statements)
                TranspileStatement(s);
            _indent--;
            AppendLine("}");
        }
    }

    private void TranspileVarDecl(VarDeclStmt varDecl)
    {
        WarnIfVoidInValueContext(varDecl.Value, $"assigning to '{varDecl.Name}'");

        if (varDecl.Value is TryExpr varTryExpr)
        {
            TranspileTryVarDecl(varDecl, varTryExpr);
            return;
        }

        Append("");
        _sb.Append(varDecl.Type is not null ? $"{TranspileType(varDecl.Type)} " : "var ");
        _sb.Append($"{varDecl.Name} = ");
        TranspileExpression(varDecl.Value);
        _sb.AppendLine(";");
    }

    private void TranspileFor(ForStmt forStmt)
    {
        ((forStmt.Variable, forStmt.Iterable) switch
        {
            (null, _) => (Action)(() =>
            {
                Append("while (");
                TranspileExpression(forStmt.Iterable);
                _sb.AppendLine(")");
            }),
            (_, RangeExpr range) => () =>
            {
                Append($"for (var {forStmt.Variable} = ");
                TranspileExpression(range.Start);
                _sb.Append($"; {forStmt.Variable} < ");
                TranspileExpression(range.End);
                _sb.AppendLine($"; {forStmt.Variable}++)");
            },
            _ => () =>
            {
                Append($"foreach (var {forStmt.Variable} in ");
                TranspileExpression(forStmt.Iterable);
                _sb.AppendLine(")");
            }
        })();

        AppendLine("{");
        _indent++;
        foreach (var s in forStmt.Body.Statements)
            TranspileStatement(s);
        _indent--;
        AppendLine("}");
    }

    private void TranspileAssign(AssignStmt assign)
    {
        Append($"{assign.Target} = ");
        TranspileExpression(assign.Value);
        _sb.AppendLine(";");
    }

    private void TranspileCompoundAssign(CompoundAssignStmt compound)
    {
        Append($"{compound.Target} {compound.Op}= ");
        TranspileExpression(compound.Value);
        _sb.AppendLine(";");
    }

    private void TranspileTryVarDecl(VarDeclStmt varDecl, TryExpr tryExpr)
    {
        var tempVar = $"_try{_tryCounter++}";

        Append($"var {tempVar} = ");
        TranspileExpression(tryExpr.Operand);
        _sb.AppendLine(";");

        AppendLine($"if ({tempVar}.IsError) return {tempVar}.Error.Value;");

        var typePrefix = varDecl.Type is not null ? $"{TranspileType(varDecl.Type)} " : "var ";
        AppendLine($"{typePrefix}{varDecl.Name} = {tempVar}.Value;");
    }

    private void TranspileTryStmt(TryExpr tryExpr)
    {
        var tempVar = $"_try{_tryCounter++}";

        Append($"var {tempVar} = ");
        TranspileExpression(tryExpr.Operand);
        _sb.AppendLine(";");

        AppendLine($"if ({tempVar}.IsError) return {tempVar}.Error.Value;");
    }
}
