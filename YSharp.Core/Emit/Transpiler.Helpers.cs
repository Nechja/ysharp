using System.Text;
using YSharp.Core.Ast;

namespace YSharp.Core.Emit;

public partial class Transpiler
{
    private void Append(string text)
    {
        _sb.Append(new string(' ', _indent * 4));
        _sb.Append(text);
    }

    private void AppendLine(string text)
    {
        _sb.Append(new string(' ', _indent * 4));
        _sb.AppendLine(text);
    }

    private static string TranspileType(TypeRef typeRef) => typeRef switch
    {
        NamedTypeRef named => named.Name switch
        {
            "void" => "void",
            "int" => "int",
            "long" => "long",
            "float" => "float",
            "double" => "double",
            "bool" => "bool",
            "string" => "string",
            // Built-in HTTP types — handler params typed as these get the
            // matching ASP.NET object injected by minimal API.
            "HttpRequest" => "Microsoft.AspNetCore.Http.HttpRequest",
            "HttpResponse" => "Microsoft.AspNetCore.Http.HttpResponse",
            _ => named.Name
        },
        GenericTypeRef generic => $"{generic.Name}<{string.Join(", ", generic.TypeArgs.Select(TranspileType))}>",
        OptionalTypeRef optional => $"{TranspileType(optional.Inner)}?",
        _ => "object"
    };

    private void TranspileArgs(List<Expr> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            TranspileExpression(args[i]);
        }
    }

    private static bool IsBlocking(FnDecl fn) =>
        fn.Modifiers.Any(m => m.Name == "blocking");

    private string GetReturnType(TypeRef returnType)
    {
        var baseType = TranspileType(returnType);
        return baseType == "void" ? "Task" : $"Task<{baseType}>";
    }

    private bool IsCallAsync(Expr expr)
    {
        if (expr is CallExpr call)
        {
            if (call.Target is IdentifierExpr id)
                return _asyncFunctions.Contains(id.Name);

            if (call.Target is MemberAccessExpr member && member.Target is IdentifierExpr targetId)
                return _asyncFunctions.Contains($"{targetId.Name}.{member.Member}");
        }
        return false;
    }

    private static string Capitalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        if (name.Contains('_'))
        {
            return string.Concat(name.Split('_')
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => char.ToUpper(s[0]) + s[1..]));
        }

        return char.ToUpper(name[0]) + name[1..];
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\n", "\\n")
         .Replace("\r", "\\r");

    // C# reserved words that a Y# user might legitimately use as an identifier
    // (Y# keywords like `class`/`if`/`fn` are excluded — they can never reach here).
    private static readonly HashSet<string> CSharpReservedWords =
    [
        "abstract", "as", "base", "byte", "case", "catch", "checked", "const",
        "decimal", "default", "delegate", "do", "else", "event", "explicit",
        "extern", "false", "finally", "fixed", "foreach", "goto", "implicit",
        "in", "internal", "is", "lock", "namespace", "new", "null", "object",
        "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "volatile", "while"
    ];

    private static string EscapeIdent(string name) =>
        CSharpReservedWords.Contains(name) ? "@" + name : name;

    private string TranspileExpressionToString(Expr expr)
    {
        var startPos = _sb.Length;
        TranspileExpression(expr);
        var result = _sb.ToString(startPos, _sb.Length - startPos);
        _sb.Length = startPos;
        return result;
    }

    private void WarnIfVoidInValueContext(Expr expr, string context)
    {
        string? funcName = null;

        if (expr is CallExpr call)
        {
            if (call.Target is IdentifierExpr id)
                funcName = id.Name;
            else if (call.Target is MemberAccessExpr member && member.Target is IdentifierExpr targetId)
                funcName = $"{targetId.Name}.{member.Member}";
        }

        if (funcName is not null && _voidFunctions.Contains(funcName))
            Console.Error.WriteLine($"Warning: void function '{funcName}' used in value context ({context})");
    }
}
