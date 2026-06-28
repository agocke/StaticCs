# C# Signature (`.cssig`) — VS Code support

Syntax highlighting and basic editor configuration (brackets, comments, auto-closing pairs) for
`.cssig` files used by the [CsSig analyzer](../../../src/CsSig).

A `.cssig` file is ordinary C# describing a project's public API surface, with member declarations
written without bodies.

## Install (local development)

Copy or symlink this folder into your VS Code extensions directory:

```sh
ln -s "$(pwd)" ~/.vscode/extensions/staticcs.cssig-0.1.0
```

Then reload VS Code. Files ending in `.cssig` will be highlighted using the `source.cssig` grammar.

## Contents

- `package.json` — language + grammar contribution.
- `language-configuration.json` — comments, brackets, auto-closing/surrounding pairs.
- `syntaxes/cssig.tmLanguage.json` — the TextMate grammar (`scopeName: source.cssig`).
