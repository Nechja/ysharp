using System.Text.Json;
using System.Text.RegularExpressions;
using YSharp.Core.Ast;

namespace YSharp.Core.Emit;

public partial class Transpiler
{
    // Set after Transpile() when routes are present. Caller writes alongside the binary.
    public string? OpenApiSpec { get; private set; }

    private void BuildOpenApiSpec(
        string title,
        List<RouteDecl> routes,
        List<RecordDecl> records,
        List<EnumDecl> enums,
        List<ErrorDecl> errors)
    {
        var paths = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var route in routes)
        {
            foreach (var endpoint in route.Endpoints)
            {
                var fullPath = (route.BasePath.TrimEnd('/') + "/" + endpoint.Path.TrimStart('/')).TrimEnd('/');
                if (string.IsNullOrEmpty(fullPath)) fullPath = "/";
                fullPath = fullPath.Replace("//", "/");

                var method = endpoint.Method.ToString().ToLowerInvariant();
                if (!paths.TryGetValue(fullPath, out var pathItem))
                    paths[fullPath] = pathItem = new Dictionary<string, object?>();

                pathItem[method] = BuildOperation(endpoint, fullPath);
            }
        }

        var schemas = new Dictionary<string, object?>();
        foreach (var rec in records)
            schemas[rec.Name] = BuildRecordSchema(rec);
        foreach (var enm in enums)
            schemas[enm.Name] = BuildTaggedUnionSchema(enm.Name, enm.Variants);
        foreach (var err in errors)
            schemas[err.Name] = BuildTaggedUnionSchema(err.Name, err.Variants);

        var doc = new Dictionary<string, object?>
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["version"] = "0.0.1"
            },
            ["paths"] = paths,
            ["components"] = new Dictionary<string, object?>
            {
                ["schemas"] = schemas
            }
        };

        OpenApiSpec = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    private Dictionary<string, object?> BuildOperation(RouteEndpoint endpoint, string fullPath)
    {
        var op = new Dictionary<string, object?>
        {
            ["operationId"] = ToCamel(endpoint.Handler)
        };

        // Path parameters parsed from `{name}` segments.
        var pathParamNames = Regex.Matches(fullPath, "\\{([^}]+)\\}")
            .Select(m => m.Groups[1].Value)
            .ToList();

        _fnsByName.TryGetValue(endpoint.Handler, out var fn);

        var parameters = new List<object?>();
        foreach (var name in pathParamNames)
        {
            var paramType = fn?.Params.FirstOrDefault(p => p.Name == name)?.Type;
            parameters.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = TypeSchema(paramType)
            });
        }
        if (parameters.Count > 0)
            op["parameters"] = parameters;

        // Request body: any non-path, non-service handler param of a record type.
        if (fn is not null)
        {
            var bodyParam = fn.Params.FirstOrDefault(p =>
                !pathParamNames.Contains(p.Name) &&
                IsRecordType(p.Type));
            if (bodyParam is not null)
            {
                op["requestBody"] = new Dictionary<string, object?>
                {
                    ["required"] = true,
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["application/json"] = new Dictionary<string, object?>
                        {
                            ["schema"] = TypeSchema(bodyParam.Type)
                        }
                    }
                };
            }
        }

        // Responses derived from handler return type.
        op["responses"] = BuildResponses(fn?.ReturnType);

        return op;
    }

    private Dictionary<string, object?> BuildResponses(TypeRef? returnType)
    {
        var responses = new Dictionary<string, object?>();

        if (returnType is GenericTypeRef { Name: "Result" } resultType)
        {
            var inner = resultType.TypeArgs.Count > 0 ? resultType.TypeArgs[0] : null;
            responses["200"] = OkResponse(inner);
            responses["400"] = ErrorResponse("Validation error");
            responses["404"] = ErrorResponse("Not found");
            responses["500"] = ErrorResponse("Internal error");
        }
        else
        {
            responses["200"] = OkResponse(returnType);
        }

        return responses;
    }

    private Dictionary<string, object?> OkResponse(TypeRef? type) => new()
    {
        ["description"] = "OK",
        ["content"] = new Dictionary<string, object?>
        {
            ["application/json"] = new Dictionary<string, object?>
            {
                ["schema"] = TypeSchema(type)
            }
        }
    };

    private static Dictionary<string, object?> ErrorResponse(string description) => new()
    {
        ["description"] = description,
        ["content"] = new Dictionary<string, object?>
        {
            ["application/json"] = new Dictionary<string, object?>
            {
                ["schema"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["error"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["message"] = new Dictionary<string, object?> { ["type"] = "string" }
                    }
                }
            }
        }
    };

    private Dictionary<string, object?> BuildRecordSchema(RecordDecl rec)
    {
        var properties = new Dictionary<string, object?>();
        var required = new List<string>();
        foreach (var f in rec.Fields)
        {
            if (f.Modifiers.Any(m => m.Name == "secret"))
                continue; // ~secret fields are JSON-invisible
            properties[ToCamel(f.Name)] = TypeSchema(f.Type);
            if (f.DefaultValue is null)
                required.Add(ToCamel(f.Name));
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private Dictionary<string, object?> BuildTaggedUnionSchema(string name, List<EnumVariant> variants)
    {
        var oneOf = new List<object?>();
        foreach (var variant in variants)
        {
            var props = new Dictionary<string, object?>
            {
                ["kind"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { variant.Name }
                }
            };
            var required = new List<string> { "kind" };
            foreach (var f in variant.Fields)
            {
                props[ToCamel(f.Name)] = TypeSchema(f.Type);
                if (f.DefaultValue is null)
                    required.Add(ToCamel(f.Name));
            }
            oneOf.Add(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = required
            });
        }

        return new Dictionary<string, object?>
        {
            ["oneOf"] = oneOf,
            ["discriminator"] = new Dictionary<string, object?> { ["propertyName"] = "kind" }
        };
    }

    private Dictionary<string, object?> TypeSchema(TypeRef? type)
    {
        if (type is null)
            return new() { ["type"] = "object" };

        switch (type)
        {
            case NamedTypeRef named:
                return named.Name switch
                {
                    "int" => new() { ["type"] = "integer", ["format"] = "int32" },
                    "long" => new() { ["type"] = "integer", ["format"] = "int64" },
                    "float" or "double" => new() { ["type"] = "number" },
                    "bool" => new() { ["type"] = "boolean" },
                    "string" => new() { ["type"] = "string" },
                    "void" => new() { ["type"] = "null" },
                    _ when IsKnownType(named.Name) => new() { ["$ref"] = $"#/components/schemas/{named.Name}" },
                    _ => new() { ["type"] = "object" }
                };

            case GenericTypeRef generic when generic.Name == "List" && generic.TypeArgs.Count == 1:
                return new() { ["type"] = "array", ["items"] = TypeSchema(generic.TypeArgs[0]) };

            case GenericTypeRef generic when generic.Name == "Result":
                // The response wrapper unwraps Result; if it leaks into a schema slot,
                // describe it as the success type.
                return generic.TypeArgs.Count > 0 ? TypeSchema(generic.TypeArgs[0]) : new() { ["type"] = "object" };

            case OptionalTypeRef opt:
                var inner = TypeSchema(opt.Inner);
                inner["nullable"] = true;
                return inner;

            default:
                return new() { ["type"] = "object" };
        }
    }

    private bool IsKnownType(string name) => _typeNames.Contains(name);

    private bool IsRecordType(TypeRef t) => t is NamedTypeRef named && IsKnownType(named.Name);

    private static string ToCamel(string snake)
    {
        if (string.IsNullOrEmpty(snake)) return snake;
        if (!snake.Contains('_')) return char.ToLowerInvariant(snake[0]) + snake[1..];

        var parts = snake.Split('_');
        return parts[0].ToLowerInvariant() + string.Concat(parts.Skip(1).Where(s => s.Length > 0).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
    }
}
