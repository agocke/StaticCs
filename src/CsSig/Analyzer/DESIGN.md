# CsSig design

Internals of the `StaticCS.CsSig` analyzer. For usage see [`README.md`](README.md); for the
file grammar see [`GRAMMAR.md`](GRAMMAR.md).

## How it works

The analyzer parses every `.cssig` additional file into a *synthetic* compilation (using the
project's own references and compilation options), then produces symbols from both that synthetic
compilation and the project. Each externally-visible member is reduced to a structural model
(`ApiMember` — a `MemberIdentity` pairing key plus two projections, `SourceMember` and
`BinaryMember`, whose record equality *is* source/binary equivalence; types are captured as a
recursive structural `TypeRef`). Members are paired by identity, and each pair's active projections
are compared. Bodyless methods (`int M();`) produce a semantic "missing body" diagnostic in the
synthetic compilation, which is intentionally ignored — only the signature matters.

Before comparison, each `.cssig` file is run through a **recognizer** pass that validates it
against the `.cssig` grammar — a restricted sublanguage of C#. C#'s own parser does the
text-to-tree work; the recognizer parses that tree against the signature grammar and rejects
anything outside it as `CSSIG004`. It is purely syntactic and produces no model: the comparison
model is always derived from symbols, so the project and signature sides go through identical logic.

The grammar is **derived from what the comparison observes** — a `.cssig` may only express things
that affect a signature. The comparison looks at accessibility, `static`, virtuality, type-level
`abstract`/`sealed`, ctor accessibility (extensibility of protected members), field
`readonly`/`const` values, and return/field/event/parameter types and ref kinds; every other
modifier, member bodies, and non-`const` field initializers are invisible to it and are rejected.
This keeps a `.cssig` from silently claiming something the analyzer ignores; if a future distinction
(e.g. nullability) should matter, it is added to *both* the comparison and the grammar together.

The public-API-surface rules (which members count, how protected members on extensible types are
handled, implicit constructors and record members, etc.) and the canonical signature format are
ported from the Roslyn Public API analyzer.

## Notes

- Only the public surface is expressible: a member is part of the surface only when written
  `public` (or `protected`/`protected internal` on an extensible type). The grammar rejects
  `private`, bare `internal`, and `private protected`. An accessible implicit parameterless
  constructor is written explicitly with primary-constructor syntax (`public C()`); inaccessible
  constructors are simply absent.
- Nullable reference type annotations (`string?`) are part of *source* equivalence only, ignored
  under binary equivalence. They are meaningful only when the project compiles with nullable
  reference types enabled; otherwise every reference type is oblivious on both sides and matches.
