using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Superpower;
using YSharp.Core.Ast;
using YSharp.Core.Lexing;
using YSharp.Core.Parsing;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace YSharp.LanguageServer;

public class DocumentSymbolHandler : DocumentSymbolHandlerBase
{
    public override Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request,
        CancellationToken _)
    {
        var path = request.TextDocument.Uri.GetFileSystemPath();
        if (!File.Exists(path))
            return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);

        var source = File.ReadAllText(path);
        var tokens = YSharpTokenizer.Instance.TryTokenize(source);
        if (!tokens.HasValue) return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);

        var parsed = YSharpParser.Program.TryParse(tokens.Value);
        if (!parsed.HasValue) return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(null);

        var symbols = new List<SymbolInformationOrDocumentSymbol>();
        foreach (var decl in parsed.Value)
            AddSymbol(decl, symbols);

        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(
            SymbolInformationOrDocumentSymbolContainer.From(symbols));
    }

    private static void AddSymbol(Decl decl, List<SymbolInformationOrDocumentSymbol> output)
    {
        (string name, SymbolKind kind)? info = decl switch
        {
            FnDecl fn          => (fn.Name,    SymbolKind.Function),
            RecordDecl r       => (r.Name,     SymbolKind.Struct),
            ClassDecl c        => (c.Name,     SymbolKind.Class),
            InterfaceDecl i    => (i.Name,     SymbolKind.Interface),
            ServiceDecl s      => (s.Name,     SymbolKind.Class),
            ModuleDecl m       => (m.Name,     SymbolKind.Module),
            EnumDecl e         => (e.Name,     SymbolKind.Enum),
            ErrorDecl er       => (er.Name,    SymbolKind.Enum),
            RouteDecl rt       => (rt.BasePath, SymbolKind.Namespace),
            _ => null
        };
        if (info is null) return;

        var span = decl.Span;
        var line = Math.Max(0, span.Position.Line - 1);
        var col  = Math.Max(0, span.Position.Column - 1);
        var range = new LspRange(new Position(line, col), new Position(line, col + info.Value.name.Length));

        output.Add(new SymbolInformationOrDocumentSymbol(new DocumentSymbol
        {
            Name = info.Value.name,
            Kind = info.Value.kind,
            Range = range,
            SelectionRange = range
        }));
    }

    protected override DocumentSymbolRegistrationOptions CreateRegistrationOptions(
        DocumentSymbolCapability capability,
        ClientCapabilities clientCapabilities) => new()
        {
            DocumentSelector = new TextDocumentSelector(new TextDocumentFilter
            {
                Language = "ysharp",
                Pattern = "**/*.yas"
            })
        };
}
