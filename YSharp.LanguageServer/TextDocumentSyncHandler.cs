using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Superpower;
using YSharp.Core.Lexing;
using YSharp.Core.Parsing;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using YsTypeCheck = YSharp.Core.TypeCheck.TypeChecker;

namespace YSharp.LanguageServer;

public class TextDocumentSyncHandler(ILanguageServerFacade router) : TextDocumentSyncHandlerBase
{
    private const string Language = "ysharp";

    private readonly Dictionary<DocumentUri, string> _buffers = new();

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) =>
        new(uri, Language);

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken _)
    {
        _buffers[request.TextDocument.Uri] = request.TextDocument.Text;
        Publish(request.TextDocument.Uri, request.TextDocument.Text);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken _)
    {
        // We register Full sync below — only one change with the entire buffer.
        var text = request.ContentChanges.FirstOrDefault()?.Text ?? "";
        _buffers[request.TextDocument.Uri] = text;
        Publish(request.TextDocument.Uri, text);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken _) => Unit.Task;

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken _)
    {
        _buffers.Remove(request.TextDocument.Uri);
        router.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = request.TextDocument.Uri,
            Diagnostics = []
        });
        return Unit.Task;
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities) => new()
        {
            DocumentSelector = new TextDocumentSelector(new TextDocumentFilter
            {
                Language = Language,
                Pattern = "**/*.yas"
            }),
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false }
        };

    private void Publish(DocumentUri uri, string source)
    {
        var diagnostics = Analyze(source);
        router.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = diagnostics
        });
    }

    private static List<LspDiagnostic> Analyze(string source)
    {
        var result = new List<LspDiagnostic>();

        var tokens = YSharpTokenizer.Instance.TryTokenize(source);
        if (!tokens.HasValue)
        {
            result.Add(MakeDiagnostic(
                "tokenize",
                tokens.ErrorMessage ?? "tokenization failed",
                tokens.ErrorPosition.Line, tokens.ErrorPosition.Column));
            return result;
        }

        var parsed = YSharpParser.Program.TryParse(tokens.Value);
        if (!parsed.HasValue)
        {
            result.Add(MakeDiagnostic(
                "parse",
                parsed.ErrorMessage ?? "parse failed",
                parsed.ErrorPosition.Line, parsed.ErrorPosition.Column));
            return result;
        }

        var diags = new YsTypeCheck().Check(parsed.Value);
        foreach (var d in diags)
            result.Add(MakeDiagnostic(d.Kind, d.Message, d.Span.Position.Line, d.Span.Position.Column));

        return result;
    }

    private static LspDiagnostic MakeDiagnostic(string kind, string message, int line, int column)
    {
        // LSP is 0-indexed; our spans are 1-indexed.
        var l = Math.Max(0, line - 1);
        var c = Math.Max(0, column - 1);
        return new LspDiagnostic
        {
            Severity = DiagnosticSeverity.Error,
            Source = $"ysharp ({kind})",
            Message = message,
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                new Position(l, c), new Position(l, c + 1))
        };
    }
}
