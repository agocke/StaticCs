using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CsSig;

/// <summary>
/// Checks that a project's public API surface exactly matches the C# signatures declared in its
/// <c>.cssig</c> additional files. The approach mirrors the Roslyn Public API analyzer, but the
/// source of truth is real (body-less) C# rather than a flat text format: the <c>.cssig</c> files
/// are parsed into a synthetic compilation, symbols are produced from both it and the project, and
/// the two sets are compared for equivalence via structural <see cref="ApiMember"/> signatures.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CsSigAnalyzer : DiagnosticAnalyzer
{
    internal const string Extension = ".cssig";

    private static readonly DiagnosticDescriptor s_missingFromProject = new(
        id: DiagId.MissingFromProject.ToIdString(),
        title: "Signature is missing from the project",
        messageFormat: "The signature '{0}' is declared in a .cssig file but is not part of the project's public API (breaks {1} equivalence)",
        category: "CsSig",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor s_missingFromSignature = new(
        id: DiagId.MissingFromSignature.ToIdString(),
        title: "Public API is missing from the .cssig file",
        messageFormat: "The signature '{0}' is part of the project's public API but is not declared in any .cssig file (breaks {1} equivalence)",
        category: "CsSig",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor s_signatureFileError = new(
        id: DiagId.SignatureFileError.ToIdString(),
        title: "Invalid .cssig file",
        messageFormat: "The .cssig file could not be parsed: {0}",
        category: "CsSig",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor s_signatureMismatch = new(
        id: DiagId.SignatureMismatch.ToIdString(),
        title: "Signature does not match the project",
        messageFormat: "The signature '{0}' is declared in a .cssig file but does not match the project's public API (breaks {1} equivalence)",
        category: "CsSig",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            s_missingFromProject,
            s_missingFromSignature,
            s_signatureFileError,
            s_signatureMismatch,
            CsSigRecognizer.Rule
        );

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;

        var sigFiles = context
            .Options.AdditionalFiles.Where(static f =>
                f.Path.EndsWith(Extension, System.StringComparison.OrdinalIgnoreCase)
            )
            .ToImmutableArray();

        // Nothing to enforce unless the project declares signatures. When one or more .cssig
        // files are present they are all included by default and define the entire public surface.
        if (sigFiles.IsEmpty)
        {
            return;
        }

        var parseOptions =
            compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions
            ?? CSharpParseOptions.Default;

        var sigTrees = new List<SyntaxTree>(sigFiles.Length);
        var fileDiagnostics = new List<Diagnostic>();
        var hadParseError = false;
        foreach (var file in sigFiles)
        {
            var text = file.GetText(context.CancellationToken);
            if (text is null)
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(
                text,
                parseOptions,
                path: file.Path,
                cancellationToken: context.CancellationToken
            );

            foreach (var diagnostic in tree.GetDiagnostics(context.CancellationToken))
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    hadParseError = true;
                    fileDiagnostics.Add(
                        Diagnostic.Create(
                            s_signatureFileError,
                            CsSigLocation.ToExternal(diagnostic.Location, file.Path),
                            diagnostic.GetMessage()
                        )
                    );
                }
            }

            // Recognize the .cssig grammar: reject any construct outside the signature sublanguage
            // (the 'partial' modifier, member bodies, ...). This is purely syntactic and produces
            // no model; the comparison model is derived from symbols below.
            foreach (var diagnostic in CsSigRecognizer.Recognize(tree, context.CancellationToken))
            {
                fileDiagnostics.Add(diagnostic);
            }

            sigTrees.Add(tree);
        }

        // If a .cssig file is malformed we can't reliably diff; surface the parse errors only.
        if (hadParseError || sigTrees.Count == 0)
        {
            context.RegisterCompilationEndAction(endContext =>
            {
                foreach (var diagnostic in fileDiagnostics)
                {
                    endContext.ReportDiagnostic(diagnostic);
                }
            });
            return;
        }

        var sigCompilation = CSharpCompilation.Create(
            "__cssig__",
            sigTrees,
            compilation.References,
            compilation.Options as CSharpCompilationOptions
        );

        var declared = ApiSurface.Collect(sigCompilation.Assembly, isDeclaration: true);
        var mode = ReadEquivalence(context.Options);
        var matched = new ConcurrentDictionary<MemberIdentity, byte>();

        // Per-type analysis: diffing each project type against the declared surface anchors the
        // "missing from signature" / "mismatch" diagnostics to the project's source files, so IDEs
        // report them in open-files scope rather than only in full-solution scope.
        context.RegisterSymbolAction(
            symbolContext =>
            {
                var actual = ApiSurface.CollectType(
                    (INamedTypeSymbol)symbolContext.Symbol,
                    isDeclaration: false
                );

                foreach (var pair in actual)
                {
                    if (!declared.TryGetValue(pair.Key, out var declaredEntry))
                    {
                        // Part of the project's public API but not declared in any .cssig file.
                        symbolContext.ReportDiagnostic(
                            Diagnostic.Create(
                                s_missingFromSignature,
                                pair.Value.Location,
                                pair.Value.Display,
                                Describe(mode)
                            )
                        );
                        continue;
                    }

                    matched[pair.Key] = 0;

                    // Present on both sides: the identities match, so compare the equivalence
                    // projections that are active. A common-aspect change differs in both views,
                    // yielding a single diagnostic labelled with both equivalences.
                    var sourceDiffers =
                        (mode & Equivalence.Source) != 0
                        && !Equals(declaredEntry.Member.Source, pair.Value.Member.Source);
                    var binaryDiffers =
                        (mode & Equivalence.Binary) != 0
                        && !Equals(declaredEntry.Member.Binary, pair.Value.Member.Binary);

                    if (sourceDiffers || binaryDiffers)
                    {
                        symbolContext.ReportDiagnostic(
                            Diagnostic.Create(
                                s_signatureMismatch,
                                pair.Value.Location,
                                declaredEntry.Display,
                                Describe(
                                    (sourceDiffers ? Equivalence.Source : 0)
                                        | (binaryDiffers ? Equivalence.Binary : 0)
                                )
                            )
                        );
                    }
                }
            },
            SymbolKind.NamedType
        );

        // Declared in a .cssig file but never matched by the project: surface the parse/recognizer
        // diagnostics and "missing from project". These anchor in the .cssig files.
        context.RegisterCompilationEndAction(endContext =>
        {
            foreach (var diagnostic in fileDiagnostics)
            {
                endContext.ReportDiagnostic(diagnostic);
            }

            foreach (var pair in declared)
            {
                endContext.CancellationToken.ThrowIfCancellationRequested();
                if (!matched.ContainsKey(pair.Key))
                {
                    // Declared in a .cssig file but missing from the project. An add/remove breaks
                    // whichever equivalence is being enforced.
                    endContext.ReportDiagnostic(
                        Diagnostic.Create(
                            s_missingFromProject,
                            CsSigLocation.ToExternal(pair.Value.Location, sigFiles[0].Path),
                            pair.Value.Display,
                            Describe(mode)
                        )
                    );
                }
            }
        });
    }

    /// <summary>
    /// Reads the <c>CsSigEquivalence</c> MSBuild property (Source / Binary / Both, default Both)
    /// that selects which equivalence relation(s) the analyzer enforces.
    /// </summary>
    private static Equivalence ReadEquivalence(AnalyzerOptions options)
    {
        if (
            options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.CsSigEquivalence",
                out var raw
            ) && !string.IsNullOrWhiteSpace(raw)
        )
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "source":
                    return Equivalence.Source;
                case "binary":
                    return Equivalence.Binary;
                case "both":
                case "strict":
                    return Equivalence.Both;
            }
        }

        return Equivalence.Both;
    }

    private static string Describe(Equivalence equivalence) =>
        equivalence switch
        {
            Equivalence.Source => "source",
            Equivalence.Binary => "binary",
            Equivalence.Both => "source and binary",
            _ => "no",
        };
}

/// <summary>The equivalence relation(s) the analyzer enforces between project and signatures.</summary>
[System.Flags]
internal enum Equivalence
{
    None = 0,

    /// <summary>Members must be usable identically from source (names, optional/params, …).</summary>
    Source = 1,

    /// <summary>Members must be binary-compatible (const values baked into consumers, …).</summary>
    Binary = 2,

    /// <summary>Both source and binary equivalence are enforced.</summary>
    Both = Source | Binary,
}
