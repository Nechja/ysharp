using System.Runtime.CompilerServices;
using System.Text.Json;
using Superpower;
using YSharp.Core.Lexing;
using YSharp.Core.Parsing;
using YSharp.Core.Runtime;
using YSharp.Core.TypeCheck;

var bless = args.Contains("--bless");

// In normal mode, read fixtures from the copied output dir (works for CI / shipped binary).
// In --bless mode, read and write source fixtures directly so blessed .out files land in the repo.
var baseDir = bless ? SourceDir() : AppContext.BaseDirectory;
var fixturesDir = Path.Combine(baseDir, "fixtures");
var errorsDir = Path.Combine(fixturesDir, "errors");
var tourPath = Path.Combine(baseDir, "tour.json");

static string SourceDir([CallerFilePath] string path = "")
    => Path.GetDirectoryName(path)!;

var passed = 0;
var failed = 0;
var blessed = 0;

// ===== Positive fixtures: interpret and diff stdout against .out =====
foreach (var yasPath in Directory.EnumerateFiles(fixturesDir, "*.yas").OrderBy(p => p))
{
    var name = Path.GetFileNameWithoutExtension(yasPath);
    var outPath = Path.ChangeExtension(yasPath, ".out");

    var (ok, actual, stage, message) = RunFixture(yasPath);

    if (!ok)
    {
        Fail(name, $"unexpected {stage} failure", message);
        continue;
    }

    if (bless)
    {
        File.WriteAllText(outPath, actual + "\n");
        Console.WriteLine($"BLESS  {name}");
        blessed++;
        continue;
    }

    if (!File.Exists(outPath))
    {
        Fail(name, "missing .out file", "run with --bless to generate");
        continue;
    }

    var expected = File.ReadAllText(outPath).TrimEnd('\n');
    if (actual != expected)
    {
        Fail(name, "output mismatch",
            $"--- expected ---\n{expected}\n--- actual ---\n{actual}");
        continue;
    }

    Pass(name);
}

// ===== Negative fixtures: expect failure at the stage listed in .err =====
if (Directory.Exists(errorsDir))
{
    foreach (var yasPath in Directory.EnumerateFiles(errorsDir, "*.yas").OrderBy(p => p))
    {
        var name = "errors/" + Path.GetFileNameWithoutExtension(yasPath);
        var errPath = Path.ChangeExtension(yasPath, ".err");

        if (!File.Exists(errPath))
        {
            Fail(name, "missing .err file", "expected stage: tokenize | parse | runtime");
            continue;
        }

        var expectedStage = File.ReadAllText(errPath).Trim();
        var (ok, _, actualStage, message) = RunFixture(yasPath);

        if (ok)
        {
            Fail(name, $"expected {expectedStage} failure", "but it succeeded");
            continue;
        }

        if (actualStage != expectedStage)
        {
            Fail(name, "wrong failure stage",
                $"expected {expectedStage}, got {actualStage}: {message}");
            continue;
        }

        Pass(name);
    }
}

// ===== Tour: parse smoke test for every teaching example =====
if (File.Exists(tourPath))
{
    var tourJson = File.ReadAllText(tourPath);
    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var tour = JsonSerializer.Deserialize<List<TourStep>>(tourJson, opts)!;

    foreach (var step in tour)
    {
        var name = "tour/" + step.Title;
        var tokens = YSharpTokenizer.Instance.TryTokenize(step.Code);
        if (!tokens.HasValue) { Fail(name, "tokenize", tokens.ErrorMessage ?? ""); continue; }

        var ast = YSharpParser.Program.TryParse(tokens.Value);
        if (!ast.HasValue) { Fail(name, "parse", ast.ErrorMessage ?? ""); continue; }

        Pass(name);
    }
}

Console.WriteLine();
if (bless)
{
    Console.WriteLine($"Blessed {blessed} fixtures.");
}
Console.ForegroundColor = failed > 0 ? ConsoleColor.Red : ConsoleColor.Green;
Console.WriteLine($"Results: {passed} passed, {failed} failed");
Console.ResetColor();

return failed > 0 ? 1 : 0;

(bool ok, string output, string stage, string message) RunFixture(string yasPath)
{
    var source = File.ReadAllText(yasPath);

    var tokens = YSharpTokenizer.Instance.TryTokenize(source);
    if (!tokens.HasValue)
        return (false, "", "tokenize", tokens.ErrorMessage ?? "");

    var ast = YSharpParser.Program.TryParse(tokens.Value);
    if (!ast.HasValue)
        return (false, "", "parse", ast.ErrorMessage ?? "");

    var diags = new TypeChecker().Check(ast.Value);
    if (diags.Count > 0)
        return (false, "", "typecheck", diags[0].Message);

    var interp = new Interpreter();
    Exception? runtimeEx = null;
    var thread = new Thread(() =>
    {
        try { interp.Run(ast.Value); }
        catch (Exception ex) { runtimeEx = ex; }
    }) { IsBackground = true };

    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(10)))
        return (false, "", "runtime", "timeout (>10s)");

    if (runtimeEx is not null)
        return (false, "", "runtime", runtimeEx.Message);

    var output = string.Join("\n", interp.Output).TrimEnd('\n');
    return (true, output, "", "");
}

void Pass(string name)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("PASS   ");
    Console.ResetColor();
    Console.WriteLine(name);
    passed++;
}

void Fail(string name, string what, string details)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Write("FAIL   ");
    Console.ResetColor();
    Console.WriteLine($"{name}: {what}");
    if (!string.IsNullOrEmpty(details))
    {
        foreach (var line in details.Split('\n'))
            Console.WriteLine($"       {line}");
    }
    failed++;
}

record TourStep(string Title, string Code);
