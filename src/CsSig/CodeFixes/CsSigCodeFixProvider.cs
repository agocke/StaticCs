using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CsSig;

/// <summary>
/// Offers a fix for <c>CSSIG002</c> (a public member missing from the <c>.cssig</c> files) that
/// (re)generates a signature file from the project's current public API via <see cref="CsSigWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>.cssig</c> file "owns" a set of top-level types — the ones it declares. Adding a missing
/// member is therefore the same operation regardless of where it lands: regenerate the owning file
/// from the project's current surface (which now includes the member). Because regeneration rewrites
/// the whole file from symbols, it is idempotent and automatically resolves every outstanding
/// <c>CSSIG002</c> for the types that file owns at once.
/// </para>
/// <para>
/// If a file already declares the member's (top-level) type, the member is added there. If no file
/// declares it, the fix offers two destinations: the default <c>PublicAPI.cssig</c>, or a
/// per-type <c>&lt;TypeName&gt;.cssig</c> file.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CsSigCodeFixProvider)), Shared]
public sealed class CsSigCodeFixProvider : CodeFixProvider
{
    internal const string Extension = ".cssig";
    internal const string DefaultFileName = "PublicAPI.cssig";

    // Equivalence keys double as the FixAll destination strategy for *undeclared* types.
    public const string ToPublicApiKey = "CsSig.AddToPublicApi";
    public const string ToTypeFileKey = "CsSig.AddToTypeFile";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(DiagId.MissingFromSignature.ToIdString());

    public override FixAllProvider GetFixAllProvider() => CsSigFixAllProvider.Instance;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var document = context.Document;
        var project = document.Project;

        var compilation = await project
            .GetCompilationAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var model = await document
            .GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        var root = await document
            .GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (compilation is null || model is null || root is null)
        {
            return;
        }

        var diagnostic = context.Diagnostics[0];
        var symbol = ResolveMember(
            model,
            root,
            diagnostic.Location.SourceSpan,
            context.CancellationToken
        );
        if (symbol is null)
        {
            return;
        }

        var topLevel = TopLevelType(symbol);
        if (topLevel is null)
        {
            return;
        }

        var key = CsSigWriter.TopLevelKey(topLevel);
        var index = await SignatureIndex
            .BuildAsync(project, context.CancellationToken)
            .ConfigureAwait(false);

        if (index.OwnerOf(key) is { } owner)
        {
            // The type is already declared in a .cssig file: regenerate that file.
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Add missing API to '{owner.Name}'",
                    ct => RegenerateOwningFileAsync(project, owner, index, key, ct),
                    equivalenceKey: ToPublicApiKey
                ),
                diagnostic
            );
            return;
        }

        // The type is not declared anywhere: offer PublicAPI.cssig (default) or <TypeName>.cssig.
        var perTypeName = topLevel.Name + Extension;
        context.RegisterCodeFix(
            CodeAction.Create(
                $"Add API to '{DefaultFileName}'",
                ct => AddTypeToNamedFileAsync(project, DefaultFileName, index, key, ct),
                equivalenceKey: ToPublicApiKey
            ),
            diagnostic
        );
        context.RegisterCodeFix(
            CodeAction.Create(
                $"Add API to '{perTypeName}'",
                ct => AddTypeToNamedFileAsync(project, perTypeName, index, key, ct),
                equivalenceKey: ToTypeFileKey
            ),
            diagnostic
        );
    }

    /// <summary>Regenerates the file that already owns <paramref name="key"/> from the project's
    /// current surface.</summary>
    private static async Task<Solution> RegenerateOwningFileAsync(
        Project project,
        TextDocument owner,
        SignatureIndex index,
        string key,
        CancellationToken ct
    )
    {
        var assembly = (await project.GetCompilationAsync(ct).ConfigureAwait(false))?.Assembly;
        if (assembly is null)
        {
            return project.Solution;
        }

        var keys = new HashSet<string>(index.KeysOwnedBy(owner.Id)) { key };
        var text = CsSigWriter.Write(assembly, keys);
        return project.Solution.WithAdditionalDocumentText(owner.Id, SourceText.From(text));
    }

    /// <summary>Adds <paramref name="key"/>'s type to the file named <paramref name="fileName"/>,
    /// creating it if necessary, otherwise regenerating it with the type included.</summary>
    private static async Task<Solution> AddTypeToNamedFileAsync(
        Project project,
        string fileName,
        SignatureIndex index,
        string key,
        CancellationToken ct
    )
    {
        var assembly = (await project.GetCompilationAsync(ct).ConfigureAwait(false))?.Assembly;
        if (assembly is null)
        {
            return project.Solution;
        }

        var existing = index.DocumentNamed(fileName);
        if (existing is not null)
        {
            var keys = new HashSet<string>(index.KeysOwnedBy(existing.Id)) { key };
            var updated = CsSigWriter.Write(assembly, keys);
            return project.Solution.WithAdditionalDocumentText(
                existing.Id,
                SourceText.From(updated)
            );
        }

        var text = CsSigWriter.Write(assembly, new HashSet<string> { key });
        return project
            .AddAdditionalDocument(
                fileName,
                SourceText.From(text),
                filePath: FilePathFor(project, fileName)
            )
            .Project.Solution;
    }

    private static string FilePathFor(Project project, string fileName)
    {
        var dir = project.FilePath is { } p ? Path.GetDirectoryName(p) : null;
        if (
            dir is null
            && project
                .AdditionalDocuments.Select(d => d.FilePath)
                .FirstOrDefault(d => d is not null)
                is { } existing
        )
        {
            dir = Path.GetDirectoryName(existing);
        }

        return dir is null ? fileName : Path.Combine(dir, fileName);
    }

    /// <summary>The outermost containing type of <paramref name="symbol"/> — the top-level type that
    /// a signature file declares. Property/event accessors are normalized to their owning member.</summary>
    private static INamedTypeSymbol? TopLevelType(ISymbol symbol)
    {
        var member =
            symbol is IMethodSymbol { AssociatedSymbol: { } associated }
            && associated is IPropertySymbol or IEventSymbol
                ? associated
                : symbol;

        var type = member as INamedTypeSymbol ?? member.ContainingType;
        if (type is null)
        {
            return null;
        }

        while (type.ContainingType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    /// <summary>Resolves the declared symbol the CSSIG002 diagnostic refers to, walking up from the
    /// node at <paramref name="span"/> until a member or type symbol is found.</summary>
    private static ISymbol? ResolveMember(
        SemanticModel model,
        SyntaxNode root,
        TextSpan span,
        CancellationToken ct
    )
    {
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        for (var current = node; current is not null; current = current.Parent)
        {
            var declared = model.GetDeclaredSymbol(current, ct);
            if (
                declared
                is INamedTypeSymbol
                    or IMethodSymbol
                    or IPropertySymbol
                    or IEventSymbol
                    or IFieldSymbol
            )
            {
                return declared;
            }

            if (current is BaseTypeDeclarationSyntax)
            {
                break;
            }
        }

        return null;
    }

    private static bool IsSignatureFile(TextDocument doc) =>
        (doc.FilePath ?? doc.Name).EndsWith(Extension, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>The keys (see <see cref="CsSigWriter.TopLevelKey"/>) of the top-level types declared
    /// in a <c>.cssig</c> document.</summary>
    private static async Task<HashSet<string>> TopLevelKeysAsync(
        TextDocument doc,
        CancellationToken ct
    )
    {
        var keys = new HashSet<string>();
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        var root = CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot(ct);

        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            // Top-level = not nested inside another type declaration.
            if (type.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any())
            {
                continue;
            }

            keys.Add(SyntaxTopLevelKey(type));
        }

        return keys;
    }

    private static string SyntaxTopLevelKey(BaseTypeDeclarationSyntax type)
    {
        var ns = NamespaceName(type);
        var arity = type is TypeDeclarationSyntax { TypeParameterList: { } list }
            ? list.Parameters.Count
            : 0;
        var name = arity > 0 ? type.Identifier.ValueText + "`" + arity : type.Identifier.ValueText;
        return ns.Length > 0 ? ns + "." + name : name;
    }

    private static string NamespaceName(SyntaxNode node)
    {
        var names = new List<string>();
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is BaseNamespaceDeclarationSyntax ns)
            {
                names.Add(ns.Name.ToString());
            }
        }

        names.Reverse();
        return string.Join(".", names);
    }

    /// <summary>An index of the project's <c>.cssig</c> additional documents and the top-level types
    /// each one declares.</summary>
    internal sealed class SignatureIndex
    {
        private readonly Dictionary<string, TextDocument> _ownerByKey;
        private readonly Dictionary<DocumentId, HashSet<string>> _keysByDocument;
        private readonly List<TextDocument> _documents;

        private SignatureIndex(
            Dictionary<string, TextDocument> ownerByKey,
            Dictionary<DocumentId, HashSet<string>> keysByDocument,
            List<TextDocument> documents
        )
        {
            _ownerByKey = ownerByKey;
            _keysByDocument = keysByDocument;
            _documents = documents;
        }

        public static async Task<SignatureIndex> BuildAsync(Project project, CancellationToken ct)
        {
            var ownerByKey = new Dictionary<string, TextDocument>();
            var keysByDocument = new Dictionary<DocumentId, HashSet<string>>();
            var documents = new List<TextDocument>();

            foreach (var doc in project.AdditionalDocuments)
            {
                if (!IsSignatureFile(doc))
                {
                    continue;
                }

                documents.Add(doc);
                var keys = await TopLevelKeysAsync(doc, ct).ConfigureAwait(false);
                keysByDocument[doc.Id] = keys;
                foreach (var key in keys)
                {
                    ownerByKey[key] = doc;
                }
            }

            return new SignatureIndex(ownerByKey, keysByDocument, documents);
        }

        public TextDocument? OwnerOf(string key) =>
            _ownerByKey.TryGetValue(key, out var doc) ? doc : null;

        public IEnumerable<string> KeysOwnedBy(DocumentId id) =>
            _keysByDocument.TryGetValue(id, out var keys) ? keys : Enumerable.Empty<string>();

        public TextDocument? DocumentNamed(string name) =>
            _documents.FirstOrDefault(d =>
                string.Equals(d.Name, name, System.StringComparison.OrdinalIgnoreCase)
            );
    }

    private sealed class CsSigFixAllProvider : FixAllProvider
    {
        public static readonly CsSigFixAllProvider Instance = new();

        public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            var diagnostics = await GatherAsync(fixAllContext).ConfigureAwait(false);
            if (diagnostics.IsEmpty)
            {
                return null;
            }

            var strategy = fixAllContext.CodeActionEquivalenceKey ?? ToPublicApiKey;
            return CodeAction.Create(
                "Add missing API to .cssig files",
                ct =>
                    ApplyAsync(
                        fixAllContext.Solution,
                        fixAllContext.Project,
                        diagnostics,
                        strategy,
                        ct
                    ),
                equivalenceKey: strategy
            );
        }

        private static async Task<ImmutableArray<Diagnostic>> GatherAsync(FixAllContext context)
        {
            switch (context.Scope)
            {
                case FixAllScope.Document when context.Document is { } document:
                    return await context
                        .GetDocumentDiagnosticsAsync(document)
                        .ConfigureAwait(false);
                case FixAllScope.Project:
                    return await context
                        .GetAllDiagnosticsAsync(context.Project)
                        .ConfigureAwait(false);
                case FixAllScope.Solution:
                    var all = ImmutableArray.CreateBuilder<Diagnostic>();
                    foreach (var project in context.Solution.Projects)
                    {
                        all.AddRange(
                            await context.GetAllDiagnosticsAsync(project).ConfigureAwait(false)
                        );
                    }

                    return all.ToImmutable();
                default:
                    return ImmutableArray<Diagnostic>.Empty;
            }
        }

        /// <summary>
        /// Regenerates every signature file touched by the batch. Each missing member is routed to the
        /// file that owns its top-level type (or, for an undeclared type, to PublicAPI.cssig or a
        /// per-type file per <paramref name="strategy"/>); each touched file is then regenerated once
        /// from the project surface with the full set of types it should own.
        /// </summary>
        private static async Task<Solution> ApplyAsync(
            Solution solution,
            Project project,
            ImmutableArray<Diagnostic> diagnostics,
            string strategy,
            CancellationToken ct
        )
        {
            var assembly = (await project.GetCompilationAsync(ct).ConfigureAwait(false))?.Assembly;
            if (assembly is null)
            {
                return solution;
            }

            var index = await SignatureIndex.BuildAsync(project, ct).ConfigureAwait(false);

            // fileName -> the set of top-level keys that file should own after the fix.
            var plan = new Dictionary<string, HashSet<string>>(
                System.StringComparer.OrdinalIgnoreCase
            );
            var existingByName = new Dictionary<string, TextDocument>(
                System.StringComparer.OrdinalIgnoreCase
            );

            foreach (var diagnostic in diagnostics)
            {
                ct.ThrowIfCancellationRequested();

                var key = await ResolveKeyAsync(solution, diagnostic, ct).ConfigureAwait(false);
                if (key is null)
                {
                    continue;
                }

                var target = index.OwnerOf(key);
                string fileName;
                if (target is not null)
                {
                    fileName = target.Name;
                }
                else
                {
                    fileName =
                        strategy == ToTypeFileKey
                            ? TypeNameFromKey(key) + Extension
                            : DefaultFileName;
                    target = index.DocumentNamed(fileName);
                }

                if (!plan.TryGetValue(fileName, out var keys))
                {
                    keys = target is not null
                        ? new HashSet<string>(index.KeysOwnedBy(target.Id))
                        : new HashSet<string>();
                    plan[fileName] = keys;
                    if (target is not null)
                    {
                        existingByName[fileName] = target;
                    }
                }

                keys.Add(key);
            }

            foreach (var entry in plan)
            {
                var text = SourceText.From(CsSigWriter.Write(assembly, entry.Value));
                if (existingByName.TryGetValue(entry.Key, out var doc))
                {
                    solution = solution.WithAdditionalDocumentText(doc.Id, text);
                }
                else
                {
                    var id = DocumentId.CreateNewId(project.Id);
                    solution = solution.AddAdditionalDocument(
                        id,
                        entry.Key,
                        text,
                        filePath: FilePathFor(project, entry.Key)
                    );
                }
            }

            return solution;
        }

        private static async Task<string?> ResolveKeyAsync(
            Solution solution,
            Diagnostic diagnostic,
            CancellationToken ct
        )
        {
            if (
                diagnostic.Location.SourceTree is not { } tree
                || solution.GetDocument(tree) is not { } document
            )
            {
                return null;
            }

            var model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (model is null || root is null)
            {
                return null;
            }

            var symbol = ResolveMember(model, root, diagnostic.Location.SourceSpan, ct);
            var topLevel = symbol is null ? null : TopLevelType(symbol);
            return topLevel is null ? null : CsSigWriter.TopLevelKey(topLevel);
        }

        private static string TypeNameFromKey(string key)
        {
            var lastDot = key.LastIndexOf('.');
            var name = lastDot >= 0 ? key.Substring(lastDot + 1) : key;
            var backtick = name.IndexOf('`');
            return backtick >= 0 ? name.Substring(0, backtick) : name;
        }
    }
}
