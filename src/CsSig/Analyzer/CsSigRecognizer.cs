using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsSig;

/// <summary>
/// Recognizes the <c>.cssig</c> grammar, a restricted sublanguage of C#, reporting a diagnostic
/// (<see cref="Rule"/>, <c>CSSIG004</c>) for every construct outside it.
/// </summary>
/// <remarks>
/// <para>
/// C#'s own lexer/parser does the text-to-tree work (producing a <see cref="SyntaxTree"/>); this
/// pass operates one level up, parsing that tree against the <c>.cssig</c> grammar and rejecting
/// anything outside it. Its sole purpose is recognition: it produces ordinary analyzer diagnostics
/// and no model. The structural model used for comparison is derived from symbols (see
/// <see cref="ApiSurface"/> and <see cref="ApiMember"/>), so both the project and signature sides
/// go through identical logic.
/// </para>
/// <para>
/// The grammar is deliberately derived from what the comparison actually observes: a <c>.cssig</c>
/// file may only express things that affect a signature. The comparison looks at accessibility,
/// <c>static</c>, virtuality (<c>virtual</c>/<c>abstract</c>/<c>override</c>/<c>sealed</c>),
/// type-level <c>abstract</c>/<c>sealed</c>, field <c>readonly</c> and <c>const</c> values, return
/// and parameter types, and parameter ref/params kinds. Every other modifier (<c>new</c>,
/// <c>async</c>, <c>volatile</c>, <c>extern</c>, <c>unsafe</c>, <c>required</c>, <c>partial</c>, …),
/// member bodies, and non-<c>const</c> field initializers are invisible to it and are therefore
/// rejected — declaring them would let a <c>.cssig</c> claim something the analyzer silently ignores.
/// </para>
/// </remarks>
internal static class CsSigRecognizer
{
    /// <summary>Diagnostic reported for any construct outside the <c>.cssig</c> grammar.</summary>
    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagId.DisallowedSignatureSyntax.ToIdString(),
        title: "Disallowed construct in .cssig file",
        messageFormat: "{0}",
        category: "CsSig",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    /// <summary>
    /// Recognizes <paramref name="tree"/> against the <c>.cssig</c> grammar, yielding a diagnostic
    /// for every construct that the grammar does not allow.
    /// </summary>
    public static IEnumerable<Diagnostic> Recognize(
        SyntaxTree tree,
        CancellationToken cancellationToken = default
    )
    {
        var walker = new Walker(tree.FilePath);
        walker.Visit(tree.GetRoot(cancellationToken));
        return walker.Diagnostics;
    }

    private sealed class Walker : CSharpSyntaxWalker
    {
        private readonly string _path;

        public Walker(string path) => _path = path;

        public List<Diagnostic> Diagnostics { get; } = new();

        private void Report(Location location, string message) =>
            Diagnostics.Add(
                Diagnostic.Create(Rule, CsSigLocation.ToExternal(location, _path), message)
            );

        private static bool IsAccessibility(SyntaxKind kind) =>
            kind
                is SyntaxKind.PublicKeyword
                    or SyntaxKind.PrivateKeyword
                    or SyntaxKind.ProtectedKeyword
                    or SyntaxKind.InternalKeyword;

        /// <summary>
        /// Modifiers allowed on a virtualizable member (method, property, indexer, event): the
        /// comparison captures all of these as part of the member's virtuality.
        /// </summary>
        private static readonly SyntaxKind[] s_memberModifiers =
        {
            SyntaxKind.StaticKeyword,
            SyntaxKind.VirtualKeyword,
            SyntaxKind.AbstractKeyword,
            SyntaxKind.OverrideKeyword,
            SyntaxKind.SealedKeyword,
        };

        // Members that can be 'readonly' on a struct (the modifier makes the member readonly,
        // turning `this` into an `in` parameter — observable in both source and binary).
        private static readonly SyntaxKind[] s_readonlyMemberModifiers =
        {
            SyntaxKind.StaticKeyword,
            SyntaxKind.VirtualKeyword,
            SyntaxKind.AbstractKeyword,
            SyntaxKind.OverrideKeyword,
            SyntaxKind.SealedKeyword,
            SyntaxKind.ReadOnlyKeyword,
        };

        /// <summary>
        /// Rejects every modifier that is not accessibility (always allowed, since it determines
        /// surface membership) or one of the <paramref name="allowed"/> modifiers known to affect
        /// the signature for this declaration kind.
        /// </summary>
        private void CheckModifiers(SyntaxTokenList modifiers, params SyntaxKind[] allowed)
        {
            foreach (var modifier in modifiers)
            {
                var kind = modifier.Kind();
                if (IsAccessibility(kind) || System.Array.IndexOf(allowed, kind) >= 0)
                {
                    continue;
                }

                Report(
                    modifier.GetLocation(),
                    $"The '{modifier.ValueText}' modifier does not affect the signature and is not allowed in a .cssig file"
                );
            }
        }

        private void RejectBody(BlockSyntax? body, ArrowExpressionClauseSyntax? expressionBody)
        {
            if (body is not null)
            {
                Report(
                    body.GetLocation(),
                    "Member bodies are not allowed in a .cssig file; signatures declare members without an implementation"
                );
            }

            if (expressionBody is not null)
            {
                Report(
                    expressionBody.GetLocation(),
                    "Expression bodies are not allowed in a .cssig file; signatures declare members without an implementation"
                );
            }
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            // 'static'/'abstract'/'sealed' all affect the type's signature (instantiation,
            // extensibility of protected members, virtual dispatch).
            CheckModifiers(
                node.Modifiers,
                SyntaxKind.StaticKeyword,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.SealedKeyword
            );
            base.VisitClassDeclaration(node);
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            // Structs are always sealed and never static; only accessibility is observable.
            CheckModifiers(node.Modifiers);
            base.VisitStructDeclaration(node);
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            CheckModifiers(node.Modifiers);
            base.VisitInterfaceDeclaration(node);
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            if (node.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
            {
                CheckModifiers(node.Modifiers);
            }
            else
            {
                CheckModifiers(
                    node.Modifiers,
                    SyntaxKind.StaticKeyword,
                    SyntaxKind.AbstractKeyword,
                    SyntaxKind.SealedKeyword
                );
            }

            base.VisitRecordDeclaration(node);
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            CheckModifiers(node.Modifiers);
            base.VisitEnumDeclaration(node);
        }

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            CheckModifiers(node.Modifiers);
            base.VisitDelegateDeclaration(node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // 'readonly' is allowed: on a struct instance method it marks the method readonly,
            // making `this` an `in` parameter (observable in both source and binary).
            CheckModifiers(node.Modifiers, s_readonlyMemberModifiers);
            RejectBody(node.Body, node.ExpressionBody);
            base.VisitMethodDeclaration(node);
        }

        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            CheckModifiers(node.Modifiers, SyntaxKind.StaticKeyword);
            RejectBody(node.Body, node.ExpressionBody);
            base.VisitOperatorDeclaration(node);
        }

        public override void VisitConversionOperatorDeclaration(
            ConversionOperatorDeclarationSyntax node
        )
        {
            CheckModifiers(node.Modifiers, SyntaxKind.StaticKeyword);
            RejectBody(node.Body, node.ExpressionBody);
            base.VisitConversionOperatorDeclaration(node);
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            // Only constructor accessibility is observable (via CanTypeBeExtended).
            CheckModifiers(node.Modifiers);
            RejectBody(node.Body, node.ExpressionBody);
            base.VisitConstructorDeclaration(node);
        }

        public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node)
        {
            RejectBody(node.Body, node.ExpressionBody);
            base.VisitDestructorDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            CheckModifiers(node.Modifiers, s_readonlyMemberModifiers);
            RejectBody(body: null, node.ExpressionBody);
            base.VisitPropertyDeclaration(node);
        }

        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            CheckModifiers(node.Modifiers, s_readonlyMemberModifiers);
            RejectBody(body: null, node.ExpressionBody);
            base.VisitIndexerDeclaration(node);
        }

        public override void VisitEventDeclaration(EventDeclarationSyntax node)
        {
            CheckModifiers(node.Modifiers, s_memberModifiers);
            base.VisitEventDeclaration(node);
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            CheckModifiers(node.Modifiers, s_memberModifiers);
            base.VisitEventFieldDeclaration(node);
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            // 'static' -> ApiMember.IsStatic; 'const' -> the captured constant value;
            // 'readonly' -> the field's read-only-ness (observable to external writers).
            CheckModifiers(
                node.Modifiers,
                SyntaxKind.StaticKeyword,
                SyntaxKind.ConstKeyword,
                SyntaxKind.ReadOnlyKeyword
            );

            // A field's value is only part of the signature when it is 'const'; any other
            // initializer is invisible to the comparison.
            if (!node.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    if (variable.Initializer is { } initializer)
                    {
                        Report(
                            initializer.GetLocation(),
                            "A field initializer does not affect the signature and is not allowed in a .cssig file; only 'const' values are part of the signature"
                        );
                    }
                }
            }

            base.VisitFieldDeclaration(node);
        }

        public override void VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            // Accessor accessibility (e.g. 'private set') determines whether the accessor surfaces.
            CheckModifiers(node.Modifiers);
            RejectBody(node.Body, node.ExpressionBody);
            base.VisitAccessorDeclaration(node);
        }
    }
}
