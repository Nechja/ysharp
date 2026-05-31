using Superpower;
using YSharp.Compiler.Emit;
using YSharp.Core.Emit;
using YSharp.Core.Lexing;
using YSharp.Core.Parsing;
using YSharp.Core.TypeCheck;

// =====================================================================
// Y# Compiler
// =====================================================================
// Usage: ysc <file.yas> [--bin]
//   --bin  Produce a self-contained native binary

var showHelp = args.Contains("--help") || args.Contains("-h");
var buildBinary = args.Contains("--bin");
var emitCSharp = args.Contains("--emit-cs");

// Subcommands
if (args.Length > 0 && args[0] == "init")
    return Init(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "watch")
    return Watch(args.Skip(1).ToArray());

var inputFile = args.FirstOrDefault(a => !a.StartsWith("-")) ?? "hello.yas";

if (showHelp)
{
    Console.WriteLine("""
        Y# Compiler

        Usage:
          ysc <file.yas> [options]    compile a Y# program
          ysc init [name]             scaffold a new Y# project
          ysc watch <file.yas>        rebuild + restart on save

        Options:
          --bin       build as self-contained AOT native binary
          --emit-cs   print the generated C# instead of building
          --help      show this help

        Examples:
          ysc hello.yas          # Creates hello.dll (run with: dotnet hello.dll)
          ysc hello.yas --bin    # Creates hello binary (run with: ./hello)
          ysc init why-api       # Scaffold a new API project
          ysc watch api.yas      # Live-reload during development
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

// ===== Typecheck =====
var typeChecker = new TypeChecker();
var diagnostics = typeChecker.Check(parseResult.Value);
if (diagnostics.Count > 0)
{
    foreach (var d in diagnostics)
        PrintDiagnostic(d.Kind, inputFile, source, d.Span.Position.Line, d.Span.Position.Column, d.Message);
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

static int Init(string[] args)
{
    var name = args.FirstOrDefault(a => !a.StartsWith("-")) ?? ".";
    var dir = name == "." ? "." : name;

    if (dir != "." && Directory.Exists(dir))
    {
        Console.WriteLine($"Error: directory '{dir}' already exists");
        return 1;
    }

    if (dir != ".")
        Directory.CreateDirectory(dir);

    var apiYas = """
        // A starter Y# API. `ysc api.yas --bin` produces a native binary.

        record Greeting { message: string; }

        fn hello(name: string) -> Greeting ~blocking {
            return Greeting($"hello, {name}!");
        }

        route "/" {
            get "/hello/{name}" => hello;
        }
        """;

    var gitignore = """
        *.dll
        *.exe
        *.pdb
        *.deps.json
        *.runtimeconfig.json
        *.staticwebassets.endpoints.json
        openapi.json
        web.config
        api
        """;

    File.WriteAllText(Path.Combine(dir, "api.yas"), apiYas);
    File.WriteAllText(Path.Combine(dir, ".gitignore"), gitignore);

    Console.WriteLine($"Scaffolded Y# API in {Path.GetFullPath(dir)}");
    Console.WriteLine("Next steps:");
    Console.WriteLine($"  cd {dir}");
    Console.WriteLine("  ysc api.yas --bin");
    Console.WriteLine("  ./api");
    return 0;
}

static int Watch(string[] args)
{
    var file = args.FirstOrDefault(a => !a.StartsWith("-"));
    if (file is null || !File.Exists(file))
    {
        Console.WriteLine("Error: ysc watch requires an existing .yas file");
        return 1;
    }

    var fullPath = Path.GetFullPath(file);
    var dir = Path.GetDirectoryName(fullPath)!;
    var name = Path.GetFileName(fullPath);

    System.Diagnostics.Process? running = null;

    void Rebuild()
    {
        try
        {
            if (running is { HasExited: false })
            {
                try { running.Kill(entireProcessTree: true); } catch { }
                running.WaitForExit(2000);
            }

            Console.WriteLine($"[watch] rebuilding {name}...");
            var yscLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var buildPsi = new System.Diagnostics.ProcessStartInfo
            {
                WorkingDirectory = dir,
                UseShellExecute = false
            };
            if (yscLocation.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                buildPsi.FileName = "dotnet";
                buildPsi.ArgumentList.Add(yscLocation);
                buildPsi.ArgumentList.Add(file);
            }
            else
            {
                buildPsi.FileName = yscLocation;
                buildPsi.ArgumentList.Add(file);
            }
            var build = System.Diagnostics.Process.Start(buildPsi)!;
            build.WaitForExit();
            if (build.ExitCode != 0)
            {
                Console.WriteLine("[watch] build failed; waiting for changes");
                return;
            }

            var dll = Path.ChangeExtension(fullPath, ".dll");
            running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { dll },
                WorkingDirectory = dir,
                UseShellExecute = false
            });
            Console.WriteLine($"[watch] running pid={running?.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[watch] error: {ex.Message}");
        }
    }

    Rebuild();

    var watcher = new FileSystemWatcher(dir, name) { EnableRaisingEvents = true };
    var debounce = DateTime.MinValue;
    watcher.Changed += (_, _) =>
    {
        if ((DateTime.UtcNow - debounce).TotalMilliseconds < 200) return;
        debounce = DateTime.UtcNow;
        Rebuild();
    };

    Console.WriteLine($"[watch] watching {fullPath} (Ctrl-C to exit)");
    System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
    return 0;
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
