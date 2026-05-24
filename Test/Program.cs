using System.Text.Json;
using Superpower;
using YSharp.Core.Lexing;
using YSharp.Core.Parsing;

var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tour.json"));
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var tour = JsonSerializer.Deserialize<List<TourStep>>(json, options)!;

var passed = 0;
var failed = 0;

foreach (var step in tour)
{
    Console.Write($"Testing: {step.Title,-25} ");

    var tokenResult = YSharpTokenizer.Instance.TryTokenize(step.Code);
    if (!tokenResult.HasValue)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAIL (tokenize)");
        Console.ResetColor();
        Console.WriteLine($"  Line {tokenResult.ErrorPosition.Line}, Col {tokenResult.ErrorPosition.Column}: {tokenResult.ErrorMessage}");
        failed++;
        continue;
    }

    var parseResult = YSharpParser.Program.TryParse(tokenResult.Value);
    if (!parseResult.HasValue)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAIL (parse)");
        Console.ResetColor();
        Console.WriteLine($"  {parseResult.ErrorPosition}: {parseResult.ErrorMessage}");
        failed++;
        continue;
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("OK");
    Console.ResetColor();
    passed++;
}

Console.WriteLine();
if (failed > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
}
Console.WriteLine($"Results: {passed} passed, {failed} failed");
Console.ResetColor();

return failed > 0 ? 1 : 0;

record TourStep(string Title, string Code);
