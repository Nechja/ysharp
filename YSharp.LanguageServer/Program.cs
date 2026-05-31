using OmniSharp.Extensions.LanguageServer.Server;

namespace YSharp.LanguageServer;

public static class Program
{
    public static async Task Main()
    {
        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(opts => opts
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .WithHandler<TextDocumentSyncHandler>()
            .WithHandler<DocumentSymbolHandler>());

        await server.WaitForExit;
    }
}
