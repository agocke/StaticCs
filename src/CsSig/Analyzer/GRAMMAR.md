# The `.cssig` grammar

A `.cssig` file describes a project's externally visible API surface using ordinary C# member
declarations **with no bodies**. This document specifies exactly which C# constructs are legal in a
`.cssig` file.

The grammar is a **restricted sublanguage of C#**. A `.cssig` file is first parsed by the C#
parser (so its lexical and syntactic structure is exactly C#'s), and is then validated by the
[recognizer](CsSigRecognizer.cs) against the additional rules below. Anything the C# parser rejects
is reported as **`CSSIG003`**; anything the recognizer rejects is reported as **`CSSIG004`**.

## Guiding principle

> A `.cssig` file may only express things that affect a signature.

The set of legal constructs is **derived from what the comparison observes** (see
[`SignatureModel.cs`](SignatureModel.cs) and [`ApiSurface.cs`](ApiSurface.cs)). If a construct is
invisible to the comparison, declaring it would let a `.cssig` silently claim something the analyzer
ignores, so the recognizer rejects it. When a new distinction should matter, it is added to *both*
the comparison and this grammar together.

## Overall shape

A `.cssig` file is a C# compilation unit:

```ebnf
compilation-unit   = { using-directive } ( file-namespace | { namespace | type-declaration } ) ;
file-namespace     = "namespace" qualified-name ";" { using-directive } { type-declaration } ;
namespace          = "namespace" qualified-name "{" { using-directive } { type-declaration } "}" ;
```

`using` directives and namespace declarations are unrestricted — they exist to bring names into
scope and to place declarations, neither of which is a signature claim.

## Type declarations

Legal type declarations are `class`, `struct`, `interface`, `record` (class or struct), `enum`, and
`delegate`. Each may carry only the modifiers that affect the surface:

| Declaration            | Allowed modifiers                          | Rationale                                                                 |
| ---------------------- | ------------------------------------------ | ------------------------------------------------------------------------- |
| `class`, `record class`| accessibility, `static`, `abstract`, `sealed` | packed into `CommonTypeAspects.Flags`; `abstract`/`sealed` affect instantiability, virtual dispatch, and whether protected members surface (`CanTypeBeExtended`) |
| `struct`, `record struct`, `interface`, `enum`, `delegate` | accessibility            | structs are always sealed/never static; the rest have no observable type modifier |

"accessibility" means any visibility that names part of the public surface: `public`, `protected`,
or `protected internal`. The non-public visibilities `private`, a bare `internal`, and `private
protected` are rejected (`CSSIG004`) — they cannot appear on any member, accessor, or type.

## Members

| Member                                     | Allowed modifiers                | Body?            | Notes                                                            |
| ------------------------------------------ | -------------------------------- | ---------------- | --------------------------------------------------------------- |
| method                                     | accessibility, `static`, `virtual`, `abstract`, `override`, `sealed`, `readonly` | none | declared as `T M(params);`; virtuality is part of the signature; `readonly` allowed on struct methods |
| operator, conversion operator              | accessibility, `static`          | none             |                                                                 |
| constructor                                | accessibility                    | none             | constructor accessibility feeds `CanTypeBeExtended`             |
| destructor                                 | (none)                           | none             |                                                                 |
| property, indexer                          | accessibility, `static`, `virtual`, `abstract`, `override`, `sealed`, `readonly` | no expression body | accessors are written as `{ get; set; }` (see below); `readonly` allowed on struct members |
| accessor (`get` / `set` / `init`)          | accessibility                    | none             | a non-public accessor (`internal set`) is rejected; omit the accessor instead   |
| event (field-form or with accessors)       | accessibility, `static`, `virtual`, `abstract`, `override`, `sealed` | n/a | typically `event T E;`                                          |
| field                                      | accessibility, `static`, `const`, `readonly` | n/a  | initializer allowed **only** with `const` (see below)           |
| enum member                                | (none)                           | n/a              | an explicit `= value` is allowed and is part of the signature   |

`virtual` / `abstract` / `override` / `sealed` on a member are captured as its *virtuality* and
affect both equivalences (see below). Interface members are implicitly `abstract`, so the modifier
need not be written there.

### No bodies

Member bodies are not part of a signature, so they are prohibited everywhere:

- block bodies (`{ … }`) on methods, operators, constructors, destructors, and accessors;
- expression bodies (`=> …`) on methods, operators, properties, indexers, and accessors.

A body-less method such as `int M();` is a *semantic* error in plain C# (CS0501); the analyzer
parses it successfully and intentionally ignores that semantic error — only the signature matters.

### Field initializers

A field's value is part of the signature **only** when the field is `const` (the comparison
captures the constant value). A `const` field therefore requires its initializer:

```csharp
public const int Limit = 100;   // legal: the value is part of the signature
```

An initializer on any non-`const` field is invisible to the comparison and is rejected:

```csharp
public static int Count = 0;    // CSSIG004: the initializer is ignored — drop it
public static int Count;        // legal
```

### Unsafe types

The `unsafe` modifier has no signature impact, so it is rejected like any other non-signature
modifier. Pointer (`int*`) and function-pointer (`delegate*<…>`) types, however, are part of the
signature and are compared structurally. Write such members **without** `unsafe`:

```csharp
public unsafe delegate*<int, void> Callback;   // CSSIG004: drop 'unsafe'
public delegate*<int, void> Callback;          // legal
```

Omitting `unsafe` makes the declaration a *semantic* error in plain C# (CS0214), exactly as a
body-less `int M();` is (CS0501); the analyzer parses it successfully and intentionally ignores
that semantic error — only the signature matters.

## Currently accepted but not part of the comparison

The following constructs are **syntactically accepted** today (the recognizer does not reject them),
but the comparison does **not** currently observe them, so they have no effect. They are candidates
for either rejection or — more likely — being folded into the comparison later:

- generic constraints (`where T : …`);
- base types and implemented interfaces (`class C : IFoo`);
- generic type-parameter variance (`in` / `out`);
- `ref` / `ref readonly` returns;
- the `scoped` parameter modifier;
- attributes.

Until that is decided, do not rely on any of these to change what the analyzer enforces.

## Source vs. binary equivalence

The comparison enforces one or both of two equivalence relations, selected by the
`CsSigEquivalence` MSBuild property (`Source`, `Binary`, or `Both` — the default):

- **Common aspects** (compared in *both*): kind, name, arity, parameter types and ref kinds, return
  / field / event type, `static`, virtuality, type-level `abstract` / `sealed`, field `readonly`,
  struct-member `readonly`, and const-ness. A difference here breaks both equivalences.
- **Source-only**: parameter *names*, `params`, extension `this`, whether a parameter is optional,
  `in` vs `ref readonly`, and nullable reference type annotations (`string?`). These change source
  call sites but not the binary calling convention.
- **Binary-only**: the `const` *value*, which is baked into already-compiled consumers (a source
  recompile picks up a new value).

Because the grammar only admits constructs the comparison observes, every legal modifier above feeds
one of these buckets.

## Diagnostics summary

| Id        | Meaning                                                                 |
| --------- | ----------------------------------------------------------------------- |
| `CSSIG003`| the file is not valid C# (a parse error)                                |
| `CSSIG004`| the file uses a construct outside this grammar                          |

(`CSSIG001` / `CSSIG002` / `CSSIG005` are reported by the *comparison*, not the grammar: a declared
signature missing from the project, a public member missing from the signature files, or a member
present on both sides whose signature is not equivalent. Each names the equivalence it breaks.)
