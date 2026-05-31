using Superpower.Model;

namespace YSharp.Core.TypeCheck;

public record Diagnostic(string Kind, string Message, TextSpan Span);
