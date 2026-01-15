using YSharp.Compiler.Ast;

namespace YSharp.Compiler.Emit;

public partial class Transpiler
{
    private void TranspileStatement(Stmt stmt)
    {
        switch (stmt)
        {
            case ExprStmt expr:
                TranspileExprStmt(expr);
                break;
            case ReturnStmt ret:
                TranspileReturn(ret);
                break;
            case BreakStmt:
                AppendLine("break;");
                break;
            case ContinueStmt:
                AppendLine("continue;");
                break;
            case IfStmt ifStmt:
                TranspileIf(ifStmt);
                break;
            case VarDeclStmt varDecl:
                TranspileVarDecl(varDecl);
                break;
            case ForStmt forStmt:
                TranspileFor(forStmt);
                break;
            case AssignStmt assign:
                TranspileAssign(assign);
                break;
            case CompoundAssignStmt compound:
                TranspileCompoundAssign(compound);
                break;
            case BlockStmt block:
                TranspileBlock(block);
                break;
            default:
                throw new NotSupportedException($"Statement type not supported: {stmt.GetType().Name}");
        }
    }

    private void TranspileBlock(BlockStmt block)
    {
        AppendLine("{");
        _indent++;
        foreach (var s in block.Statements)
            TranspileStatement(s);
        _indent--;
        AppendLine("}");
    }

    private void TranspileExprStmt(ExprStmt expr)
    {
        switch (expr.Expression)
        {
            case TryExpr tryExpr:
                TranspileTryStmt(tryExpr);
                break;
            case ConcurrentExpr concurrentExpr:
                TranspileConcurrent(concurrentExpr);
                break;
            case BlockingExpr blockingExpr:
                TranspileBlockingStmt(blockingExpr);
                break;
            default:
                Append("");
                TranspileExpression(expr.Expression);
                _sb.AppendLine(";");
                break;
        }
    }

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
        if (forStmt.Variable is null)
        {
            Append("while (");
            TranspileExpression(forStmt.Iterable);
            _sb.AppendLine(")");
        }
        else if (forStmt.Iterable is RangeExpr range)
        {
            Append($"for (var {forStmt.Variable} = ");
            TranspileExpression(range.Start);
            _sb.Append($"; {forStmt.Variable} < ");
            TranspileExpression(range.End);
            _sb.AppendLine($"; {forStmt.Variable}++)");
        }
        else
        {
            Append($"foreach (var {forStmt.Variable} in ");
            TranspileExpression(forStmt.Iterable);
            _sb.AppendLine(")");
        }

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
