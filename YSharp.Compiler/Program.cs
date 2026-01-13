using Superpower;
using YSharp.Compiler.Emit;
using YSharp.Compiler.Lexing;
using YSharp.Compiler.Parsing;

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
    Console.WriteLine($"Tokenization failed at {tokenResult.ErrorPosition}");
    return 1;
}

// ===== Parse =====
var parseResult = YSharpParser.Program.TryParse(tokenResult.Value);
if (!parseResult.HasValue)
{
    Console.WriteLine($"Parse failed: {parseResult.ErrorMessage}");
    Console.WriteLine($"At: {parseResult.ErrorPosition}");
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

    if (buildBinary)
    {
        Console.WriteLine("Building native binary...");
        Builder.BuildBinary(csharpCode, assemblyName, useDi, useWeb);
        Console.WriteLine();
        Console.WriteLine($"Output: ./{assemblyName}");
        Console.WriteLine($"Run:    ./{assemblyName}");
    }
    else
    {
        Console.WriteLine("Building...");
        var outputPath = Path.ChangeExtension(inputFile, ".dll");
        Builder.BuildDll(csharpCode, outputPath, useDi, useWeb);
        Console.WriteLine();
        Console.WriteLine($"Output: {outputPath}");
        Console.WriteLine($"Run:    dotnet {outputPath}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Build failed: {ex.Message}");
    return 1;
}

return 0;
