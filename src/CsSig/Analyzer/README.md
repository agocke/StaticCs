# CsSig — C# signature files (`.cssig`)

`StaticCS.CsSig` is a Roslyn analyzer, in the spirit of the
[Public API analyzer](https://github.com/dotnet/roslyn/tree/main/src/RoslynAnalyzers/PublicApiAnalyzers),
that lets you pin a project's **public API surface** using ordinary C#.

Instead of a flat text format, the expected surface is described in a `.cssig` file containing
real C# member declarations **with no bodies**. The analyzer verifies that the project's public
API surface *exactly matches* the declared signatures — in both directions.

## Example

`Api.cssig`:

```csharp
namespace MyLibrary;

public class Greeter
{
    public Greeter(string name);
    public string Greet();
    public string Name { get; }
}
```

If the project's public API drifts from this file, you get a build error:

- `CSSIG001` — a signature is declared in a `.cssig` file but is **missing from the project**.
- `CSSIG002` — a public member exists in the project but is **not declared** in any `.cssig` file.
- `CSSIG003` — a `.cssig` file could not be parsed.
- `CSSIG004` — a `.cssig` file uses a construct outside the signature grammar. The grammar is derived from what the comparison observes, so a `.cssig` can only express things that affect a signature. This rejects modifiers that don't change a signature (`new`, `async`, `volatile`, `extern`, `unsafe`, `required`, `partial`, …), member bodies, and non-`const` field initializers. The modifiers that *are* allowed are accessibility, `static`, `abstract`/`sealed` (types), member virtuality (`virtual`/`abstract`/`override`/`sealed`), `const`/`readonly` (fields), `readonly` (struct methods/properties/indexers), and parameter `ref`/`in`/`out`/`params`/`this`. See [`GRAMMAR.md`](GRAMMAR.md) for the full specification.
- `CSSIG005` — a member is present on both sides but its signature is **not equivalent** (e.g. a changed return type, virtuality, parameter name, or `const` value). The message names which equivalence is broken.

## Source vs. binary equivalence

The analyzer enforces one or both of two equivalence relations, chosen with the `CsSigEquivalence`
MSBuild property — `Source`, `Binary`, or `Both` (the default):

```xml
<PropertyGroup>
  <CsSigEquivalence>Both</CsSigEquivalence>
</PropertyGroup>
```

- **Common** aspects break *both*: types, `static`, virtuality, type-level `abstract`/`sealed`,
  field `readonly`/const-ness, struct-member `readonly` (it makes `this` an `in` parameter), and
  return/field/event/parameter types.
- **Source-only** aspects break source equivalence only: parameter *names*, `params`, extension
  `this`, whether a parameter is optional, `in` vs `ref readonly`, and nullable reference type
  annotations (`string?`) — they change source call sites but not the binary calling convention.
- **Binary-only** aspects break binary equivalence only: a `const` *value*, which is baked into
  already-compiled consumers (a source recompile picks up a new value).

Every `CSSIG001`/`CSSIG002`/`CSSIG005` message states which equivalence(s) it breaks.

## How it works

The analyzer parses every `.cssig` additional file into a *synthetic* compilation (using the
project's own references and compilation options), then produces symbols from both that synthetic
compilation and the project. Each externally-visible member is reduced to a structural model
(`ApiMember` — a `MemberIdentity` pairing key plus two projections, `SourceMember` and
`BinaryMember`, whose record equality *is* source/binary equivalence; types are captured as a
recursive structural `TypeRef`). Members are paired by identity, and each pair's active projections
are compared. Bodyless methods (`int M();`) produce a semantic "missing body"
diagnostic in the synthetic compilation, which is intentionally ignored — only the signature
matters.

Before comparison, each `.cssig` file is run through a **recognizer** pass that validates it
against the `.cssig` grammar — a restricted sublanguage of C#. C#'s own parser does the
text-to-tree work; the recognizer parses that tree against the signature grammar and rejects
anything outside it as `CSSIG004`. It is purely syntactic and produces no model: the comparison
model is always derived from symbols, so the project and signature sides go through identical logic.

The grammar is **derived from what the comparison observes** — a `.cssig` may only express things
that affect a signature. The comparison looks at accessibility, `static`, virtuality, type-level
`abstract`/`sealed`, ctor accessibility (extensibility of protected members), field
`readonly`/`const` values, and return/field/event/parameter types and ref kinds; every other
modifier, member bodies, and non-`const` field initializers are invisible to it and are
rejected. This keeps a `.cssig` from silently claiming something the analyzer ignores; if a future
distinction (e.g. nullability) should matter, it is added to *both* the comparison and the grammar
together.

The public-API-surface rules (which members count, how protected members on extensible types are
handled, implicit constructors and record members, etc.) and the canonical signature format are
ported from the Roslyn Public API analyzer.

## Usage

Add a package reference and drop a `.cssig` file next to your code:

```xml
<ItemGroup>
  <PackageReference Include="StaticCS.CsSig" Version="*" PrivateAssets="all" />
</ItemGroup>
```

All `*.cssig` files anywhere in the project are included by default (as `AdditionalFiles`) and
together define the project's entire public API surface. There is no shipped/unshipped split — every
`.cssig` file is treated the same. To opt out of the auto-include entirely, set
`<EnableCsSigAnalyzer>false</EnableCsSigAnalyzer>`.

If a project has no `.cssig` files, there is nothing to enforce and the analyzer does nothing.

## Generating `.cssig` files

You don't have to write a `.cssig` file by hand. A code fix on `CSSIG002` regenerates the
signature file from the project's current public API, mirroring the Public API analyzer's
"Add to public API" fix.

The bootstrap workflow:

1. Create an empty `.cssig` file in the project (e.g. `Api.cssig`).
2. Build. Because the file declares nothing, **every** public member surfaces a `CSSIG002`.
3. In the editor, invoke the code fix on any `CSSIG002` and choose **Fix all** (document, project,
   or solution). The fix rewrites the whole file from the current surface, so a single application
   resolves every outstanding `CSSIG002` at once.

The same fix keeps an existing `.cssig` up to date: when you add public members, apply the fix to
any new `CSSIG002` to regenerate the file with the additions. If a project has more than one
`.cssig` file, the fix targets the first one.

The regenerated text is exactly what the analyzer expects to round-trip with zero diagnostics:
members are emitted as body-less declarations grouped by namespace and nesting, with non-signature
modifiers (`async`, `unsafe`, `volatile`, …) omitted.

## Notes

- Accessibility and type-kind keywords (`public`, `class`, …) are part of the C# you write, but the
  comparison is over the *externally visible surface*: a member written without `public` simply
  isn't part of the declared surface.
- Nullable reference type annotations (`string?`) are part of *source* equivalence only: a
  `string`/`string?` difference is reported when source (or both) equivalence is enforced, and
  ignored under binary equivalence. They are only meaningful when the project compiles with nullable
  reference types enabled; otherwise every reference type is oblivious on both sides and matches.
- Editor support (syntax highlighting) for `.cssig` files is provided by the VS Code extension under
  [`src/CsSig/vscode`](../vscode).
