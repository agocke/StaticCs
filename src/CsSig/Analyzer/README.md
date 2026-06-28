# CsSig — C# signature files (`.cssig`)

`StaticCS.CsSig` is a Roslyn analyzer, in the spirit of the
[Public API analyzer](https://github.com/dotnet/roslyn/tree/main/src/RoslynAnalyzers/PublicApiAnalyzers),
that pins a project's **public API surface** using ordinary C#. The expected surface lives in a
`.cssig` file of real C# member declarations **with no bodies**, and the analyzer verifies the
project matches it exactly, in both directions.

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

## Usage

Add the package and drop a `.cssig` file next to your code:

```
dotnet add package StaticCS.CsSig
```

All `*.cssig` files in the project are included automatically and together define the entire public
API surface. Set `<EnableCsSigAnalyzer>false</EnableCsSigAnalyzer>` to opt out. A project with no
`.cssig` files enforces nothing.

You don't write the file by hand: create an empty `PublicApi.cssig`, build (every public member reports
`CSSIG002`), then invoke the code fix → **Fix all** to generate it from the current surface. Apply
the same fix to keep it current as the API grows.

## Diagnostics

- `CSSIG001` — declared in a `.cssig` file but **missing from the project**.
- `CSSIG002` — public in the project but **not declared** in any `.cssig` file.
- `CSSIG003` — a `.cssig` file could not be parsed.
- `CSSIG004` — uses a construct outside the signature grammar (see [`GRAMMAR.md`](GRAMMAR.md)).
- `CSSIG005` — present on both sides but the signatures are **not equivalent**.

Every `001`/`002`/`005` message states which equivalence(s) it breaks.

## Equivalence

Choose what to enforce with the `CsSigEquivalence` MSBuild property — `Source`, `Binary`, or
`Both` (default):

```xml
<PropertyGroup>
  <CsSigEquivalence>Both</CsSigEquivalence>
</PropertyGroup>
```

Types, virtuality, `static`, `readonly`, and member types break both. Parameter names, `params`,
optionality, `in` vs `ref readonly`, and nullable annotations break source only. A `const` *value*
breaks binary only.

---

Syntax highlighting is provided by the VS Code extension in [`src/CsSig/vscode`](../vscode).
Internals are documented in [`DESIGN.md`](DESIGN.md).
