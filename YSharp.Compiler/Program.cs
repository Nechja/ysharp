using Superpower;
using YSharp.Compiler.Emit;
using YSharp.Core.Emit;
using YSharp.Core.Lexing;
using YSharp.Core.Parsing;

// =====================================================================
// Y# Compiler
// =====================================================================
// Usage: ysc <file.yas> [--bin]
//   --bin  Produce a self-contained native binary

var showHelp = args.Contains("--help") || args.Contains("-h");
var buildBinary = args.Contains("--bin");
var emitCSharp = args.Contains("--emit-cs");
var inputFile = args.FirstOrDefault(a => !a.StartsWith("-")) ?? "hello.yas";

if (showHelp)
{
    Console.WriteLine("""
        Y# Compiler

        Usage: ysc <file.yas> [options]

        Options:
          --bin    Build as self-contained native binary
          --help   Show this help

        Examples:
          ysc hello.yas          # Creates hello.dll (run with: dotnet hello.dll)
          ysc hello.yas --bin    # Creates hello binary (run with: ./hello)
        """);
    return 0;
}

Console.WriteLine("=== Y# Compiler ===");
Console.WriteLine();

// Check file exists
if (!File.Exists(inputFile))
{
    Console.WriteLine($"Error: File not found: {inputFile}");
    return 1;
}

var source = File.ReadAllText(inputFile);
Console.WriteLine($"Compiling: {inputFile}");

// ===== Tokenize =====
var tokenResult = YSharpTokenizer.Instance.TryTokenize(source);
if (!tokenResult.HasValue)
{
    PrintDiagnostic("tokenize", inputFile, source, tokenResult.ErrorPosition.Line, tokenResult.ErrorPosition.Column, tokenResult.ErrorMessage);
    return 1;
}

// ===== Parse =====
var parseResult = YSharpParser.Program.TryParse(tokenResult.Value);
if (!parseResult.HasValue)
{
    var msg = parseResult.ErrorMessage;
    if (string.IsNullOrWhiteSpace(msg) && parseResult.Expectations is { } exp && exp.Any())
        msg = SummarizeExpectations(exp.Distinct().ToArray());
    PrintDiagnostic("parse", inputFile, source, parseResult.ErrorPosition.Line, parseResult.ErrorPosition.Column, msg);
    return 1;
}

// ===== Transpile to C# =====
var assemblyName = Path.GetFileNameWithoutExtension(inputFile);
var transpiler = new Transpiler(assemblyName);
var csharpCode = transpiler.Transpile(parseResult.Value);

if (emitCSharp)
{
    Console.WriteLine("=== Generated C# ===");
    Console.WriteLine(csharpCode);
    return 0;
}

// ===== Build =====
try
{
    var useDi = transpiler.UsesDependencyInjection;
    var useWeb = transpiler.UsesHttpRoutes;

    string outputDir;
    if (buildBinary)
    {
        Console.WriteLine("Building native binary...");
        Builder.BuildBinary(csharpCode, assemblyName, useDi, useWeb);
        Console.WriteLine();
        Console.WriteLine($"Output: ./{assemblyName}");
        Console.WriteLine($"Run:    ./{assemblyName}");
        outputDir = ".";
    }
    else
    {
        Console.WriteLine("Building...");
        var outputPath = Path.ChangeExtension(inputFile, ".dll");
        Builder.BuildDll(csharpCode, outputPath, useDi, useWeb);
        Console.WriteLine();
        Console.WriteLine($"Output: {outputPath}");
        Console.WriteLine($"Run:    dotnet {outputPath}");
        outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".";
    }

    if (transpiler.OpenApiSpec is { } spec)
    {
        var openApiPath = Path.Combine(outputDir, "openapi.json");
        File.WriteAllText(openApiPath, spec);
        Console.WriteLine($"OpenAPI: {openApiPath}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Build failed: {ex.Message}");
    return 1;
}

return 0;

// Token-name hints from the parser's failure-set are individually meaningless and
// collectively enormous. Detect the common "start of expression" set and just say so.
static string SummarizeExpectations(string[] exp)
{
    var set = exp.Select(e => e.ToLowerInvariant()).ToHashSet();
    var expressionStarters = new[] { "integer", "decimal", "string", "identifier", "true", "false", "none", "lbracket" };
    if (expressionStarters.Count(set.Contains) >= 3)
        return "expected expression";

    if (exp.Length <= 4)
        return "expected " + string.Join(" or ", exp);

    return "expected " + string.Join(", ", exp.Take(3)) + $", or {exp.Length - 3} other tokens";
}

static void PrintDiagnostic(string kind, string file, string source, int line, int column, string? message)
{
    var lines = source.Split('\n');
    var lineIdx = line - 1;
    var bad = lineIdx >= 0 && lineIdx < lines.Length ? lines[lineIdx].TrimEnd('\r') : "";

    // Tabs throw off the caret; replace with two spaces and track the resulting column.
    var displayLine = bad.Replace("\t", "  ");
    var displayCol = 0;
    for (int i = 0; i < column - 1 && i < bad.Length; i++)
        displayCol += bad[i] == '\t' ? 2 : 1;

    var gutter = line.ToString();
    var pad = new string(' ', gutter.Length);

    Console.ForegroundColor = ConsoleColor.Red;
    Console.Write($"error[{kind}]: ");
    Console.ResetColor();
    Console.WriteLine(string.IsNullOrWhiteSpace(message) ? "compilation failed" : message);
    Console.WriteLine($"  {pad}--> {file}:{line}:{column}");
    Console.WriteLine($"  {pad} |");
    Console.WriteLine($"  {gutter} | {displayLine}");
    Console.Write($"  {pad} | {new string(' ', displayCol)}");
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("^");
    Console.ResetColor();
}
