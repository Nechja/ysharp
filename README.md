# Y#

A small language that transpiles to C# and AOT-compiles to native binaries.
Built to explore what a "small fast API language" looks like when records, DI,
and HTTP routes are first-class.

## Status

Personal project, experimental. I'm the only intended user right now. Syntax
and codegen change whenever I learn something. No release schedule, no SemVer
guarantees, no support.

PRs aren't being accepted. Issues are fine if you want to flag something
interesting.

## What it looks like

```
record Why { id: int; topic: string; reason: string; }

interface IWhyStore {
    fn all() -> List<Why> ~blocking;
    fn findByTopic(topic: string) -> Result<Why> ~blocking;
}

service singleton WhyStore : IWhyStore {
    fn all() -> List<Why> ~blocking => [Why(1, "null", "someone forgot to check it")];
    fn findByTopic(topic: string) -> Result<Why> ~blocking {
        for w in all() {
            if w.topic == topic { return w; }
        }
        return Error.NotFound($"no why for topic '{topic}'");
    }
}

module Reasons { bind IWhyStore => WhyStore; }

fn get_why(store: IWhyStore, topic: string) -> Result<Why> ~blocking
    => store.findByTopic(topic);

route "/why" { get "/{topic}" => get_why; }

app Reasons { fn main() -> void { log.info("running"); } }
```

```
ysc api.yas --bin
./api
```

## What works

* `ysc api.yas --bin` produces a ~10MB native AOT binary, no .NET runtime needed
* Records, interfaces, services, modules, app DI block
* Async-first functions, `~blocking` opt-out
* `Result<T>` auto-maps to HTTP status codes
* `~created`, `~accepted`, `~noContent`, `~log`, `~secret` modifiers
* Match destructuring on enum/error variants
* Built-in `print`, `env`, `log.info`, `http.get`, `http.post`
* OpenAPI spec emitted alongside the binary
* `ysc init` scaffolds a project, `ysc watch` rebuilds on save
* `ysharp-lsp` provides editor diagnostics and document symbols
* Source-aware compile errors with line snippets
* Type checker catches undefined idents, arity mismatches, return type misuse

## What doesn't

* No member-access type checking, no generic type checking
* No validation modifiers, no wire-name overrides
* No persistence story, no config file loading
* No response header writing
* No formatter, no VS Code extension yet
* No language spec or grammar doc
* Interpolated string parser still has rough edges with complex expressions
