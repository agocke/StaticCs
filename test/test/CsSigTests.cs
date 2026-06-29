using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CsSig;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace StaticCs.Tests;

public class CsSigTests
{
    [Fact]
    public async Task ExactMatchProducesNoDiagnostics()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M(string s) => s.Length;
                public string Name { get; set; } = "";
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M(string s);
                public string Name { get; set; }
            }
            """;
        var diagnostics = await RunAsync(source, sig);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task SignatureMissingFromProjectReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M();
                public int Extra();
            }
            """;
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG001", diagnostic.Id);
        Assert.Contains("Extra", diagnostic.GetMessage());
    }

    [Fact]
    public async Task PublicMemberMissingFromSignatureReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
                public int Extra() => 1;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M();
            }
            """;
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG002", diagnostic.Id);
        Assert.Contains("Extra", diagnostic.GetMessage());
    }

    [Fact]
    public async Task InternalAndPrivateMembersAreIgnored()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
                internal int Hidden() => 1;
                private int Secret() => 2;
            }
            internal class NotPublic
            {
                public int Whatever() => 0;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M();
            }
            """;
        Assert.Empty(await RunAsync(source, sig));
    }

    [Fact]
    public async Task NonPublicVisibilityInSignatureRejected()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M();
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                private int A();
                internal int B();
                private protected int D();
                public int M();
            }
            """;
        // private, a bare internal, and private protected name non-public members, which the .cssig
        // grammar cannot express.
        var diagnostics = await RunAsync(source, sig);
        Assert.Equal(3, diagnostics.Count(d => d.Id == "CSSIG004"));
    }

    [Fact]
    public async Task ProtectedVisibilitiesInSignatureAllowed()
    {
        var source = """
            namespace N;
            public class C()
            {
                protected int A();
                protected internal int B();
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                protected int A();
                protected internal int B();
            }
            """;
        // protected and protected internal are part of the extensible surface and are allowed.
        Assert.DoesNotContain(await RunAsync(source, sig), d => d.Id == "CSSIG004");
    }

    [Fact]
    public async Task ChangedReturnTypeReportsMismatch()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public string M();
            }
            """;
        // The return type is a common aspect, not part of the member's identity, so the members
        // pair up and the difference is one mismatch (breaking both equivalences), not add/remove.
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
        Assert.Contains("source and binary", diagnostic.GetMessage());
    }

    [Fact]
    public async Task MissingTypeIsReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M();
            }
            public class Missing
            {
            }
            """;
        var diagnostics = await RunAsync(source, sig);
        // The missing type and its implicit constructor are both absent from the project.
        Assert.All(diagnostics, d => Assert.Equal("CSSIG001", d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("N.Missing"));
    }

    [Fact]
    public async Task NoSignatureFilesMeansNoEnforcement()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
                public int Extra() => 1;
            }
            """;
        Assert.Empty(await RunAsync(source));
    }

    [Fact]
    public async Task EnumExactMatch()
    {
        var source = """
            namespace N;
            public enum Color { Red, Green, Blue }
            """;
        var sig = """
            namespace N;
            public enum Color { Red, Green, Blue }
            """;
        Assert.Empty(await RunAsync(source, sig));
    }

    [Fact]
    public async Task MissingEnumMemberReported()
    {
        var source = """
            namespace N;
            public enum Color { Red, Green, Blue }
            """;
        var sig = """
            namespace N;
            public enum Color { Red, Green }
            """;
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG002", diagnostic.Id);
        Assert.Contains("Blue", diagnostic.GetMessage());
    }

    [Fact]
    public async Task SyntaxErrorInSignatureFileReported()
    {
        var source = """
            namespace N;
            public class C()
            {
            }
            """;
        var sig = """
            namespace N
            public class C() {
            """;
        var diagnostics = await RunAsync(source, sig);
        Assert.Contains(diagnostics, d => d.Id == "CSSIG003");
    }

    [Fact]
    public async Task PartialInSignatureFileReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            """;
        var sig = """
            namespace N;
            public partial class C
            {
                public partial int M();
            }
            """;
        var diagnostics = await RunAsync(source, sig);
        Assert.Equal(2, diagnostics.Count(d => d.Id == "CSSIG004"));
    }

    [Fact]
    public async Task MemberBodyInSignatureFileReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
                public int P { get; }
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M() => 0;
                public int P { get { return 0; } }
            }
            """;
        var diagnostics = await RunAsync(source, sig);
        // The expression body on M and the block body on P's getter are both rejected.
        Assert.Equal(2, diagnostics.Count(d => d.Id == "CSSIG004"));
    }

    [Fact]
    public async Task ModifiersThatDoNotAffectTheSignatureAreReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
                public int F;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public async int M();
                public volatile int F;
            }
            """;
        var diagnostics = await RunAsync(source, sig);
        // 'async' (method) and 'volatile' (field) are invisible to the comparison: both rejected,
        // and the signatures still match so there are no missing/mismatch diagnostics.
        Assert.Equal(2, diagnostics.Count(d => d.Id == "CSSIG004"));
        Assert.DoesNotContain(diagnostics, d => d.Id is "CSSIG001" or "CSSIG002" or "CSSIG005");
    }

    [Fact]
    public async Task ModifiersThatAffectVirtualityAreAllowedAndCompared()
    {
        var source = """
            namespace N;
            public abstract class C()
            {
                public virtual int M() => 0;
                public abstract int N();
            }
            """;
        var sig = """
            namespace N;
            public abstract class C()
            {
                public virtual int M();
                public abstract int N();
            }
            """;
        // 'abstract' (type) and 'virtual'/'abstract' (members) affect both equivalences, so they
        // are allowed and, matching the project, produce no diagnostics.
        Assert.Empty(await RunAsync(source, sig));
    }

    [Fact]
    public async Task VirtualityMismatchReportedInBothEquivalences()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public virtual int M();
            }
            """;
        // Virtuality is a common aspect: declaring 'virtual' when the project's member is not
        // breaks both equivalences.
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
        Assert.Contains("source and binary", diagnostic.GetMessage());
    }

    [Fact]
    public async Task ParameterNameChangeBreaksOnlySourceEquivalence()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M(string s) => s.Length;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M(string name);
            }
            """;
        // A renamed parameter changes named-argument call sites (source) but not the binary
        // calling convention.
        var underBoth = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG005", underBoth.Id);
        Assert.Contains("breaks source equivalence", underBoth.GetMessage());

        Assert.Empty(await RunWithEquivalenceAsync(source, "Binary", sig));
    }

    [Fact]
    public async Task InVsRefReadonlyBreaksOnlySourceEquivalence()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M(ref readonly int x) => x;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M(in int x);
            }
            """;
        // `in` and `ref readonly` share a binary calling convention (both a modreq(InAttribute)
        // byref), so they differ for source equivalence (call-site rules) but are binary-equivalent
        // -- a single source-only modification, not an add/remove.
        var underBoth = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG005", underBoth.Id);
        Assert.Contains("breaks source equivalence", underBoth.GetMessage());

        Assert.Empty(await RunWithEquivalenceAsync(source, "Binary", sig));
    }

    [Fact]
    public async Task ConstValueChangeBreaksOnlyBinaryEquivalence()
    {
        var source = """
            namespace N;
            public class C()
            {
                public const int X = 1;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public const int X = 2;
            }
            """;
        // A changed const value is baked into already-compiled consumers (binary) but a source
        // recompile picks up the new value.
        var underBinary = Assert.Single(await RunWithEquivalenceAsync(source, "Binary", sig));
        Assert.Equal("CSSIG005", underBinary.Id);
        Assert.Contains("breaks binary equivalence", underBinary.GetMessage());

        Assert.Empty(await RunWithEquivalenceAsync(source, "Source", sig));
    }

    [Fact]
    public async Task FunctionPointerTypesAreComparedStructurally()
    {
        // The project source needs 'unsafe' (real C#); the .cssig declares the same member
        // without it, since 'unsafe' has no signature impact and is rejected in .cssig.
        var source = """
            namespace N;
            public unsafe class C()
            {
                public delegate*<int, void> F;
            }
            """;
        var match = """
            namespace N;
            public class C()
            {
                public delegate*<int, void> F;
            }
            """;
        // Identical function-pointer signatures are equivalent.
        Assert.Empty(await RunAsync(source, match));

        // A differing function-pointer parameter type (int vs long) must be detected
        // structurally, not by a string blob.
        var sig = """
            namespace N;
            public class C()
            {
                public delegate*<long, void> F;
            }
            """;
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
        Assert.Contains("source and binary", diagnostic.GetMessage());
    }

    [Fact]
    public async Task UnsafeModifierInSignatureFileReported()
    {
        var source = """
            namespace N;
            public unsafe class C()
            {
                public delegate*<int, void> F;
            }
            """;
        // 'unsafe' has no signature impact, so it is rejected even though it is required to write
        // the equivalent declaration in real C#.
        var sig = """
            namespace N;
            public unsafe class C()
            {
                public delegate*<int, void> F;
            }
            """;
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG004", diagnostic.Id);
    }

    [Fact]
    public async Task NonConstFieldInitializerReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public static int X;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public static int X = 5;
            }
            """;
        var diagnostics = await RunAsync(source, sig);
        // The initializer is invisible to the comparison (the field is not const), so it is
        // rejected, but the fields themselves still match.
        Assert.Equal(1, diagnostics.Count(d => d.Id == "CSSIG004"));
        Assert.DoesNotContain(diagnostics, d => d.Id is "CSSIG001" or "CSSIG002");
    }

    [Fact]
    public async Task RoundTripClassMembers()
    {
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public abstract class C()<T>
                {
                    public const int K = 5;
                    public static readonly string S;
                    protected C() { }
                    public C(int x) { }
                    public virtual int M(T value, in int by, ref string s, out bool b) { b = true; return 0; }
                    public string Name { get; set; }
                    public int ReadOnly { get; }
                    public int this[int i] => i;
                    public event System.Action E;
                }
            }
            """
        );
    }

    [Fact]
    public async Task RoundTripStructInterfaceEnumDelegate()
    {
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public interface IThing
                {
                    int Compute(string s);
                    int Value { get; set; }
                }

                public struct Point
                {
                    public int X;
                    public int Y;
                    public readonly int Sum() => X + Y;
                }

                public enum Color : byte
                {
                    Red = 1,
                    Green = 2,
                    Blue = 4,
                }

                public delegate int Transform<T>(T input, ref int state);
            }
            """
        );
    }

    [Fact]
    public async Task RoundTripNestedTypesAndStaticClass()
    {
        await AssertRoundTripsAsync(
            """
            namespace N.Inner
            {
                public static class Helpers
                {
                    public static int Add(int a, int b) => a + b;

                    public sealed class Nested
                    {
                        public int Value;
                    }
                }
            }
            """
        );
    }

    [Fact]
    public async Task RoundTripExtensionMembers()
    {
        // C# 14 extension blocks: each `extension(Receiver) { ... }` is modelled by Roslyn as a
        // nested type with an unspeakable name. The writer must emit it as an `extension(...)` block
        // (not a nameless `class`), and the round-trip must produce no diagnostics.
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public static class Ext
                {
                    extension(int[])
                    {
                        public static string Describe => "ints";
                    }
                    extension<T>(T[] source)
                    {
                        public int Count2 => source.Length;
                        public bool TryFirst(out T value) { value = default!; return false; }
                        public static T[] Empty => System.Array.Empty<T>();
                    }
                }
            }
            """,
            nullable: true,
            languageVersion: LanguageVersion.Preview
        );
    }

    [Fact]
    public async Task ExtensionMemberMissingFromProjectReported()
    {
        var source = """
            namespace N;
            public static class Ext
            {
                extension<T>(T[] source)
                {
                    public int Count2 => source.Length;
                }
            }
            """;
        var sig = """
            namespace N
            {
                public static class Ext
                {
                    extension<T>(T[] source)
                    {
                        public int Count2 { get; }
                        public static int Bogus { get; }
                    }
                }
            }
            """;
        // `Bogus` is declared in the signature but absent from the project: exactly one report.
        var diagnostic = Assert.Single(await RunPreviewAsync(source, sig));
        Assert.Equal("CSSIG001", diagnostic.Id);
    }

    [Fact]
    public async Task ExtensionMemberMissingFromSignatureReported()
    {
        var source = """
            namespace N;
            public static class Ext
            {
                extension<T>(T[] source)
                {
                    public int Count2 => source.Length;
                    public static T[] Empty => System.Array.Empty<T>();
                }
            }
            """;
        var sig = """
            namespace N
            {
                public static class Ext
                {
                    extension<T>(T[] source)
                    {
                        public int Count2 { get; }
                    }
                }
            }
            """;
        // `Empty` exists in the project but is not declared: exactly one report (no double-count
        // from the implicit implementation method the compiler synthesises on the static class).
        var diagnostic = Assert.Single(await RunPreviewAsync(source, sig));
        Assert.Equal("CSSIG002", diagnostic.Id);
    }

    [Fact]
    public async Task RoundTripFunctionPointerAndVolatile()
    {
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public unsafe class C()
                {
                    public static volatile int Flag;
                    public delegate*<int, void> Callback;
                }
            }
            """
        );
    }

    [Fact]
    public async Task RoundTripDefaultInterfaceMethods()
    {
        // A default interface method (one with a body) is `virtual`, whereas the body-less form
        // written to a .cssig is `abstract`. The two must compare equal: a signature file cannot
        // carry a body, so it cannot distinguish the two.
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public interface IThing
                {
                    int Required(string s);
                    int Defaulted() => 0;
                    string Name { get; }
                    string DefaultedName => "x";
                    static abstract int StaticRequired();
                    static virtual int StaticDefaulted() => 1;
                }
            }
            """,
            languageVersion: LanguageVersion.Preview
        );
    }

    [Fact]
    public async Task RoundTripPrivateConstructorClass()
    {
        // A class whose only constructor is private exposes no public constructor. The writer emits
        // no constructor for it, and the analyzer ignores the parameterless constructor the compiler
        // synthesizes when the body-less `.cssig` is parsed, so the two sides still agree -- without
        // any private members appearing in the signature.
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public sealed class Singleton
                {
                    public static Singleton Instance { get; }
                    private Singleton() { }
                }

                public sealed class OnlyParam
                {
                    public OnlyParam(int x) { }
                }

                public class WithProtected
                {
                    protected WithProtected() { }
                }

                public class PublicImplicit
                {
                    public int Value;
                }
            }
            """
        );
    }

    [Fact]
    public async Task RoundTripPositionalRecords()
    {
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public abstract record Base
                {
                    public sealed record Num(double Value) : Base;
                    public sealed record Pair(int A, string B) : Base;
                    public sealed record Empty : Base;
                }

                public record struct PointR(int X, int Y);
            }
            """
        );
    }

    [Fact]
    public async Task RoundTripGenericConstraints()
    {
        // Constraints change member semantics: `where T : struct` makes `T?` a Nullable<T> rather
        // than an annotated reference, and two overloads differing only by constraint are distinct.
        // The self-referential `Box<T, TProvider>` field exercises oblivious-vs-not-annotated type
        // parameter nullability that the writer must reconcile.
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public interface IProvider<T> { }

                public sealed class Box<T, TProvider>
                    where T : struct
                    where TProvider : IProvider<T>
                {
                    public class Inner
                    {
                        public static readonly Box<T, TProvider>.Inner Instance = null!;
                    }

                    public T? Wrap(T value) => value;
                }

                public static class Ext
                {
                    public static void M<T, TProvider>(T? value)
                        where T : struct
                        where TProvider : IProvider<T> { }

                    public static void M<T, TProvider>(T? value)
                        where T : class
                        where TProvider : IProvider<T> { }
                }
            }
            """,
            nullable: true
        );
    }

    [Fact]
    public async Task WritesPositionalRecordParameterListAndBase()
    {
        var generated = await WriteAsync(
            """
            namespace N
            {
                public abstract record Base
                {
                    public sealed record Num(double Value) : Base;
                    public sealed record Empty : Base;
                }
            }
            """
        );

        // The primary constructor parameter list and the record base clause are emitted in the
        // header.
        Assert.Contains("public sealed record Num(double Value) : N.Base", generated);
        Assert.Contains("public sealed record Empty() : N.Base", generated);

        // The positional property and the primary constructor are carried by the header, so they
        // must not be written as separate members.
        Assert.DoesNotContain("public double Value", generated);
        Assert.DoesNotContain("public Num(", generated);
    }

    [Fact]
    public async Task WritesGenericConstraintClauses()
    {
        var generated = await WriteAsync(
            """
            namespace N
            {
                public interface IFace { }

                public class Constraints<TStruct, TClass, TClassQ, TNotNull, TUnmanaged, TNew, TBase, TMulti>
                    where TStruct : struct
                    where TClass : class
                    where TClassQ : class?
                    where TNotNull : notnull
                    where TUnmanaged : unmanaged
                    where TNew : new()
                    where TBase : System.Exception
                    where TMulti : class, System.IComparable, new()
                {
                }

                public static class Methods
                {
                    public static void M<T>(T x) where T : struct { }
                }
            }
            """,
            nullable: true
        );

        Assert.Contains("where TStruct : struct", generated);
        Assert.Contains("where TClass : class", generated);
        Assert.Contains("where TClassQ : class?", generated);
        Assert.Contains("where TNotNull : notnull", generated);
        Assert.Contains("where TUnmanaged : unmanaged", generated);
        Assert.Contains("where TNew : new()", generated);
        Assert.Contains("where TBase : System.Exception", generated);
        Assert.Contains("where TMulti : class, System.IComparable, new()", generated);
        // Method-level constraints are carried too.
        Assert.Contains("where T : struct", generated);
    }

    [Fact]
    public async Task WritesAccessibleParameterlessConstructorExplicitly()
    {
        var generated = await WriteAsync(
            """
            namespace N
            {
                public class PublicImplicit
                {
                    public int Value;
                }

                public abstract class WithProtected
                {
                    public int Value;
                }

                public sealed class Singleton
                {
                    public static Singleton Instance { get; }
                    private Singleton() { }
                }
            }
            """
        );

        // An accessible implicit parameterless constructor is real API the project could remove or
        // make private, so it is declared explicitly via primary-constructor syntax (a `()` after
        // the type name) rather than a separate member line.
        Assert.Contains("public class PublicImplicit()", generated);
        Assert.Contains("public abstract class WithProtected()", generated);

        // A class whose only constructor is inaccessible declares no `()`, so no constructor -- and
        // in particular no private member -- is written.
        Assert.Contains("public sealed class Singleton\n", generated.Replace("\r\n", "\n"));
        Assert.DoesNotContain("private", generated);
    }

    [Fact]
    public async Task WritesStaticModifierOnInterfaceMembers()
    {
        var generated = await WriteAsync(
            """
            namespace N
            {
                public interface IThing
                {
                    static int StaticOnly() => 2;
                    int Instance();
                }
            }
            """,
            languageVersion: LanguageVersion.Preview
        );

        // SymbolDisplay strips `static` on interface members; the writer puts it back.
        Assert.Contains("static int StaticOnly();", generated);
        // Instance members are not given a spurious `static`.
        Assert.Contains("int Instance();", generated);
        Assert.DoesNotContain("static int Instance();", generated);
    }

    [Fact]
    public async Task RoundTripConstraintKinds()
    {
        // Exercises every constraint clause the writer can emit, on both types and methods.
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public interface IFace { }

                public class TStruct<T> where T : struct { }
                public class TClass<T> where T : class { }
                public class TClassQ<T> where T : class? { }
                public class TNotNull<T> where T : notnull { }
                public class TUnmanaged<T> where T : unmanaged { }
                public class TNew<T> where T : new() { }
                public class TBase<T> where T : System.Exception { }
                public class TMulti<T> where T : class, System.IComparable, IFace, new() { }
                public class TInterdependent<T, U> where U : T { }

                public static class Methods
                {
                    public static void A<T>(T x) where T : struct { }
                    public static void B<T>(T x) where T : class, new() { }
                }
            }
            """,
            nullable: true
        );
    }

    [Fact]
    public async Task RoundTripGenericPositionalRecords()
    {
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public abstract record Base<T>
                {
                    public sealed record Leaf(T Value) : Base<T>;
                }

                public record Box<T>(T Value);
                public record Pair<K, V>(K Key, V Value) where K : notnull;
            }
            """,
            nullable: true
        );
    }

    [Fact]
    public async Task RoundTripRecordWithExplicitAndExtraMembers()
    {
        // A positional record that also declares an explicitly-overriding positional property, an
        // additional (non-primary) constructor, and ordinary members. The explicit property and the
        // extra constructor must still be written; the primary constructor and the implicit
        // positional property must not be double-counted.
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public sealed record Person(string Name, int Age)
                {
                    public string Name { get; init; } = Name;

                    public Person(string name) : this(name, 0) { }

                    public string Greeting() => "hi";
                    public static Person Anonymous { get; } = new("?");
                }
            }
            """
        );
    }

    [Fact]
    public async Task RoundTripAbstractRecordWithPrivateConstructor()
    {
        // The serde JsonValue shape: an abstract record with an explicit private parameterless
        // constructor and derived records. The compiler synthesizes a (protected) parameterless
        // constructor from the body-less form, but the analyzer ignores that synthesized constructor
        // on the declaration side, so the two sides agree with no private member in the signature.
        await AssertRoundTripsAsync(
            """
            namespace N
            {
                public abstract record Value
                {
                    private Value() { }

                    public sealed record Num(double Number) : Value;
                    public sealed record Text(string Content) : Value;
                }
            }
            """
        );
    }

    [Fact]
    public async Task DeclaredPublicConstructorRemovedFromProjectReported()
    {
        // The accessible parameterless constructor is part of the tracked contract: if the .cssig
        // declares it (via primary-constructor syntax) but the project has made it private, the
        // divergence is reported.
        var source = """
            namespace N;
            public class C
            {
                private C() { }
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
            }
            """;
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG001", diagnostic.Id);
    }

    [Fact]
    public async Task InterfaceMemberStaticnessStillReported()
    {
        // FlagsFrom normalizes virtuality away for interface members but keeps static-ness, so a
        // static-vs-instance mismatch must still be reported.
        var source = """
            namespace N;
            public interface IThing
            {
                static abstract int M();
            }
            """;
        var sig = """
            namespace N;
            public interface IThing
            {
                int M();
            }
            """;
        var diagnostic = Assert.Single(await RunPreviewAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
    }

    [Fact]
    public async Task NullableTypeParameterDifferenceStillReported()
    {
        // The oblivious-to-not-annotated collapse must not mask a genuine `T?` vs `T` difference on
        // a class-constrained type parameter (annotated stays annotated).
        var source = """
            #nullable enable
            namespace N;
            public class C()
            {
                public T M<T>(T? value) where T : class => value!;
            }
            """;
        var sig = """
            #nullable enable
            namespace N;
            public class C()
            {
                public T M<T>(T value) where T : class;
            }
            """;
        var diagnostic = Assert.Single(await RunNullableAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
        Assert.Contains("breaks source equivalence", diagnostic.GetMessage());
    }

    /// <summary>Generates a <c>.cssig</c> from <paramref name="source"/> and asserts that feeding it
    /// back through the analyzer reports no diagnostics.</summary>
    private static async Task AssertRoundTripsAsync(
        string source,
        bool nullable = false,
        LanguageVersion languageVersion = LanguageVersion.Default
    )
    {
        var references = await ReferenceAssemblies.Net.Net60.ResolveAsync(
            LanguageNames.CSharp,
            CancellationToken.None
        );
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true
        );
        if (nullable)
        {
            compilationOptions = compilationOptions.WithNullableContextOptions(
                NullableContextOptions.Enable
            );
        }

        var compilation = CSharpCompilation.Create(
            "TestProject",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(languageVersion),
                    path: "Test.cs"
                ),
            },
            references,
            compilationOptions
        );

        var generated = CsSigWriter.Write(compilation);
        var diagnostics = await RunCoreAsync(
            source,
            equivalence: null,
            nullable,
            languageVersion,
            generated
        );
        Assert.Empty(diagnostics);
    }

    /// <summary>Compiles <paramref name="source"/> and returns the generated <c>.cssig</c> text, for
    /// tests that assert on the exact emitted syntax.</summary>
    private static async Task<string> WriteAsync(
        string source,
        bool nullable = false,
        LanguageVersion languageVersion = LanguageVersion.Default
    )
    {
        var references = await ReferenceAssemblies.Net.Net60.ResolveAsync(
            LanguageNames.CSharp,
            CancellationToken.None
        );
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true
        );
        if (nullable)
        {
            compilationOptions = compilationOptions.WithNullableContextOptions(
                NullableContextOptions.Enable
            );
        }

        var compilation = CSharpCompilation.Create(
            "TestProject",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(languageVersion),
                    path: "Test.cs"
                ),
            },
            references,
            compilationOptions
        );

        return CsSigWriter.Write(compilation);
    }

    [Fact]
    public async Task ReadonlyStructMethodMismatchReported()
    {
        var source = """
            namespace N;
            public struct S
            {
                public int X;
                public readonly int Get() => X;
            }
            """;
        var sig = """
            namespace N;
            public struct S
            {
                public int X;
                public int Get();
            }
            """;
        // The project method is `readonly`; the signature's is not. That difference affects the
        // calling convention of `this`, so it must be reported under both equivalences.
        var diagnostic = Assert.Single(await RunAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
    }

    [Fact]
    public async Task NullableReturnAnnotationMismatchReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public string? M() => null;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public string M();
            }
            """;

        // The project returns `string?` but the signature declares `string`: a nullable-annotation
        // difference. It is observable only in source, so it reports as a source-equivalence change.
        var diagnostic = Assert.Single(await RunNullableAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
    }

    [Fact]
    public async Task NullableParameterAnnotationMismatchReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public void M(string? s) { }
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public void M(string s);
            }
            """;

        var diagnostic = Assert.Single(await RunNullableAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
    }

    [Fact]
    public async Task NullableFieldAnnotationMismatchReported()
    {
        var source = """
            namespace N;
            public class C()
            {
                public string? F;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public string F;
            }
            """;

        var diagnostic = Assert.Single(await RunNullableAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
    }

    [Fact]
    public async Task NullableNestedTypeArgumentAnnotationMismatchReported()
    {
        var source = """
            using System.Collections.Generic;
            namespace N;
            public class C()
            {
                public List<string?> M() => new();
            }
            """;
        var sig = """
            using System.Collections.Generic;
            namespace N;
            public class C()
            {
                public List<string> M();
            }
            """;

        // The outer type matches; only the nullability of the type argument differs.
        var diagnostic = Assert.Single(await RunNullableAsync(source, sig));
        Assert.Equal("CSSIG005", diagnostic.Id);
    }

    [Fact]
    public async Task NullableAnnotationIgnoredUnderBinaryEquivalence()
    {
        var source = """
            namespace N;
            public class C()
            {
                public string? M() => null;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public string M();
            }
            """;

        // Nullable annotations have no binary impact, so a `string?`/`string` difference is invisible
        // when only binary equivalence is enforced.
        Assert.Empty(await RunNullableWithEquivalenceAsync(source, "binary", sig));
    }

    [Fact]
    public async Task MatchingNullableAnnotationAccepted()
    {
        var source = """
            namespace N;
            public class C()
            {
                public string? M(string? s) => s;
                public string N(string s) => s;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public string? M(string? s);
                public string N(string s);
            }
            """;

        Assert.Empty(await RunNullableAsync(source, sig));
    }

    [Fact]
    public async Task NullableMembersRoundTrip()
    {
        await AssertRoundTripsAsync(
            """
            using System.Collections.Generic;
            namespace N;
            public class C()
            {
                public string? Field;
                public string? Property { get; set; }
                public string? M(string? a, List<string?> b) => a;
            }
            """,
            nullable: true
        );
    }

    [Fact]
    public async Task CodeFixRegeneratesFileThatAlreadyDeclaresTheType()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
                public int Extra() => 1;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M();
            }
            """;

        // 'MyApi.cssig' already declares N.C, so the only offered fix regenerates that file.
        using var harness = await CodeFixHarness.CreateAsync(source, ("MyApi.cssig", sig));
        var actions = await harness.RegisterFirstAsync();

        var single = Assert.Single(actions);
        Assert.Equal("Update 'MyApi.cssig' to match project API", single.Title);

        var files = await CodeFixHarness.ApplyAsync(single, harness.ProjectId);
        Assert.Contains("public int Extra();", files["MyApi.cssig"]);
        Assert.Empty(await RunAsync(source, files["MyApi.cssig"]));
    }

    [Fact]
    public async Task CodeFixRegeneratesFileForSignatureMismatch()
    {
        var source = """
            namespace N;
            public class C()
            {
                public long M() => 0;
            }
            """;
        var sig = """
            namespace N;
            public class C()
            {
                public int M();
            }
            """;

        // The declared signature returns int but the project returns long: CSSIG005. The fix
        // regenerates the owning file so the declared member matches the project surface.
        using var harness = await CodeFixHarness.CreateAsync(source, ("MyApi.cssig", sig));
        var actions = await harness.RegisterFirstAsync("CSSIG005");

        var single = Assert.Single(actions);
        Assert.Equal("Update 'MyApi.cssig' to match project API", single.Title);

        var files = await CodeFixHarness.ApplyAsync(single, harness.ProjectId);
        Assert.Contains("public long M();", files["MyApi.cssig"]);
        Assert.Empty(await RunAsync(source, files["MyApi.cssig"]));
    }

    [Fact]
    public async Task CodeFixDefaultPopulatesPublicApiFileForUndeclaredType()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M(string s) => s.Length;
                public string Name { get; set; } = "";
            }
            """;

        // Bootstrap workflow: an empty PublicAPI.cssig exists but declares nothing, so N.C is
        // undeclared. Two fixes are offered; the default (first) targets PublicAPI.cssig.
        using var harness = await CodeFixHarness.CreateAsync(source, ("PublicAPI.cssig", ""));
        var actions = await harness.RegisterFirstAsync();

        Assert.Equal(2, actions.Length);
        Assert.Equal("Add API to 'PublicAPI.cssig'", actions[0].Title);
        Assert.Equal("Add API to 'C.cssig'", actions[1].Title);

        var files = await CodeFixHarness.ApplyAsync(actions[0], harness.ProjectId);
        var generated = files["PublicAPI.cssig"];
        Assert.Contains("public int M(string s);", generated);
        Assert.Contains("public string Name { get; set; }", generated);
        Assert.Empty(await RunAsync(source, generated));
    }

    [Fact]
    public async Task CodeFixPerTypeOptionCreatesTypeNamedFile()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            """;

        using var harness = await CodeFixHarness.CreateAsync(source, ("PublicAPI.cssig", ""));
        var actions = await harness.RegisterFirstAsync();

        // The second option writes into <TypeName>.cssig, leaving PublicAPI.cssig untouched.
        var files = await CodeFixHarness.ApplyAsync(actions[1], harness.ProjectId);
        Assert.Contains("public int M();", files["C.cssig"]);
        Assert.Equal("", files["PublicAPI.cssig"]);
        Assert.Empty(await RunAsync(source, files["C.cssig"]));
    }

    [Fact]
    public async Task CodeFixRoutesToDeclaringFileNotPublicApi()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            """;
        var cFile = """
            namespace N;
            public class C()
            {
            }
            """;

        // N.C is declared in C.cssig, so even with PublicAPI.cssig present the fix targets C.cssig.
        using var harness = await CodeFixHarness.CreateAsync(
            source,
            ("PublicAPI.cssig", ""),
            ("C.cssig", cFile)
        );
        var actions = await harness.RegisterFirstAsync();

        var single = Assert.Single(actions);
        Assert.Equal("Update 'C.cssig' to match project API", single.Title);

        var files = await CodeFixHarness.ApplyAsync(single, harness.ProjectId);
        Assert.Contains("public int M();", files["C.cssig"]);
        Assert.Equal("", files["PublicAPI.cssig"]);
    }

    [Fact]
    public async Task CodeFixFixAllPopulatesPublicApiWithEveryType()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            public class D
            {
                public int N() => 0;
            }
            """;

        using var harness = await CodeFixHarness.CreateAsync(source, ("PublicAPI.cssig", ""));
        var files = await harness.ApplyFixAllAsync(CsSigCodeFixProvider.ToPublicApiKey);

        var generated = files["PublicAPI.cssig"];
        Assert.Contains("class C", generated);
        Assert.Contains("class D", generated);
        Assert.Empty(await RunAsync(source, generated));
    }

    [Fact]
    public async Task CodeFixFixAllPerTypeCreatesOneFileEach()
    {
        var source = """
            namespace N;
            public class C()
            {
                public int M() => 0;
            }
            public class D
            {
                public int N() => 0;
            }
            """;

        using var harness = await CodeFixHarness.CreateAsync(source, ("PublicAPI.cssig", ""));
        var files = await harness.ApplyFixAllAsync(CsSigCodeFixProvider.ToTypeFileKey);

        Assert.Contains("public int M();", files["C.cssig"]);
        Assert.Contains("public int N();", files["D.cssig"]);
        Assert.Equal("", files["PublicAPI.cssig"]);
        Assert.Empty(await RunAsync(source, files["C.cssig"], files["D.cssig"]));
    }

    /// <summary>An in-memory workspace harness for exercising <see cref="CsSigCodeFixProvider"/>:
    /// builds a project with a source document plus named <c>.cssig</c> additional documents, runs
    /// the analyzer, and exposes helpers to register and apply the resulting fixes.</summary>
    private sealed class CodeFixHarness : System.IDisposable
    {
        private readonly Microsoft.CodeAnalysis.AdhocWorkspace _workspace;

        public ProjectId ProjectId { get; }
        public DocumentId SourceId { get; }

        private CodeFixHarness(
            Microsoft.CodeAnalysis.AdhocWorkspace workspace,
            ProjectId projectId,
            DocumentId sourceId
        )
        {
            _workspace = workspace;
            ProjectId = projectId;
            SourceId = sourceId;
        }

        public void Dispose() => _workspace.Dispose();

        public static async Task<CodeFixHarness> CreateAsync(
            string source,
            params (string name, string content)[] sigFiles
        )
        {
            var references = await ReferenceAssemblies.Net.Net60.ResolveAsync(
                LanguageNames.CSharp,
                CancellationToken.None
            );

            var projectId = ProjectId.CreateNewId();
            var sourceId = DocumentId.CreateNewId(projectId);

            var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
            var solution = workspace
                .CurrentSolution.AddProject(
                    projectId,
                    "TestProject",
                    "TestProject",
                    LanguageNames.CSharp
                )
                .AddMetadataReferences(projectId, references)
                .WithProjectCompilationOptions(
                    projectId,
                    new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        allowUnsafe: true
                    )
                )
                .AddDocument(sourceId, "Test.cs", source);

            foreach (var (name, content) in sigFiles)
            {
                var sigId = DocumentId.CreateNewId(projectId);
                solution = solution.AddAdditionalDocument(
                    sigId,
                    name,
                    SourceText.From(content),
                    filePath: name
                );
            }

            Assert.True(workspace.TryApplyChanges(solution));
            return new CodeFixHarness(workspace, projectId, sourceId);
        }

        private Project Project => _workspace.CurrentSolution.GetProject(ProjectId)!;

        private async Task<ImmutableArray<Diagnostic>> MissingDiagnosticsAsync(
            string id = "CSSIG002"
        )
        {
            var project = Project;
            var compilation = (await project.GetCompilationAsync(CancellationToken.None))!;
            var withAnalyzers = compilation.WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new CsSigAnalyzer()),
                project.AnalyzerOptions
            );
            var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(
                CancellationToken.None
            );
            return diagnostics.Where(d => d.Id == id).ToImmutableArray();
        }

        /// <summary>Registers fixes for the first <paramref name="id"/> diagnostic and returns the
        /// offered actions in registration order.</summary>
        public async Task<ImmutableArray<CodeAction>> RegisterFirstAsync(string id = "CSSIG002")
        {
            var diagnostics = await MissingDiagnosticsAsync(id);
            var trigger = diagnostics.First();

            var actions = ImmutableArray.CreateBuilder<CodeAction>();
            var context = new CodeFixContext(
                Project.GetDocument(SourceId)!,
                trigger,
                (a, _) => actions.Add(a),
                CancellationToken.None
            );
            await new CsSigCodeFixProvider().RegisterCodeFixesAsync(context);
            return actions.ToImmutable();
        }

        /// <summary>Invokes the provider's FixAll with the given equivalence key across the whole
        /// project.</summary>
        public async Task<Dictionary<string, string>> ApplyFixAllAsync(string equivalenceKey)
        {
            var diagnostics = await MissingDiagnosticsAsync();
            var provider = new CsSigCodeFixProvider();
            var fixAllContext = new FixAllContext(
                Project.GetDocument(SourceId)!,
                provider,
                FixAllScope.Project,
                equivalenceKey,
                provider.FixableDiagnosticIds,
                new CollectedDiagnosticProvider(diagnostics),
                CancellationToken.None
            );

            var action = await provider.GetFixAllProvider()!.GetFixAsync(fixAllContext);
            return await ApplyAsync(action!, ProjectId);
        }

        /// <summary>Applies a code action and returns the resulting <c>.cssig</c> documents as a
        /// name -> text map.</summary>
        public static async Task<Dictionary<string, string>> ApplyAsync(
            CodeAction action,
            ProjectId projectId
        )
        {
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var changed = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;

            var result = new Dictionary<string, string>();
            foreach (var doc in changed.GetProject(projectId)!.AdditionalDocuments)
            {
                result[doc.Name] = (await doc.GetTextAsync(CancellationToken.None)).ToString();
            }

            return result;
        }

        private sealed class CollectedDiagnosticProvider : FixAllContext.DiagnosticProvider
        {
            private readonly ImmutableArray<Diagnostic> _diagnostics;

            public CollectedDiagnosticProvider(ImmutableArray<Diagnostic> diagnostics) =>
                _diagnostics = diagnostics;

            public override Task<System.Collections.Generic.IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(
                Project project,
                CancellationToken cancellationToken
            ) => Task.FromResult<System.Collections.Generic.IEnumerable<Diagnostic>>(_diagnostics);

            public override Task<System.Collections.Generic.IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(
                Document document,
                CancellationToken cancellationToken
            ) => Task.FromResult<System.Collections.Generic.IEnumerable<Diagnostic>>(_diagnostics);

            public override Task<System.Collections.Generic.IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(
                Project project,
                CancellationToken cancellationToken
            ) => Task.FromResult<System.Collections.Generic.IEnumerable<Diagnostic>>(_diagnostics);
        }
    }

    private static Task<ImmutableArray<Diagnostic>> RunAsync(
        string source,
        params string[] signatureFiles
    ) =>
        RunCoreAsync(
            source,
            equivalence: null,
            nullable: false,
            LanguageVersion.Default,
            signatureFiles
        );

    private static Task<ImmutableArray<Diagnostic>> RunPreviewAsync(
        string source,
        params string[] signatureFiles
    ) =>
        RunCoreAsync(
            source,
            equivalence: null,
            nullable: true,
            LanguageVersion.Preview,
            signatureFiles
        );

    private static Task<ImmutableArray<Diagnostic>> RunNullableAsync(
        string source,
        params string[] signatureFiles
    ) =>
        RunCoreAsync(
            source,
            equivalence: null,
            nullable: true,
            LanguageVersion.Default,
            signatureFiles
        );

    private static Task<ImmutableArray<Diagnostic>> RunWithEquivalenceAsync(
        string source,
        string? equivalence,
        params string[] signatureFiles
    ) =>
        RunCoreAsync(source, equivalence, nullable: false, LanguageVersion.Default, signatureFiles);

    private static Task<ImmutableArray<Diagnostic>> RunNullableWithEquivalenceAsync(
        string source,
        string? equivalence,
        params string[] signatureFiles
    ) => RunCoreAsync(source, equivalence, nullable: true, LanguageVersion.Default, signatureFiles);

    private static async Task<ImmutableArray<Diagnostic>> RunCoreAsync(
        string source,
        string? equivalence,
        bool nullable,
        LanguageVersion languageVersion,
        params string[] signatureFiles
    )
    {
        var references = await ReferenceAssemblies.Net.Net60.ResolveAsync(
            LanguageNames.CSharp,
            CancellationToken.None
        );

        var parseOptions = new CSharpParseOptions(languageVersion);
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true
        );
        if (nullable)
        {
            compilationOptions = compilationOptions.WithNullableContextOptions(
                NullableContextOptions.Enable
            );
        }

        var compilation = CSharpCompilation.Create(
            "TestProject",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions, path: "Test.cs") },
            references,
            compilationOptions
        );

        var additionalFiles = ImmutableArray.CreateRange(
            signatureFiles.Select(
                (content, i) => (AdditionalText)new InMemoryAdditionalText($"Api{i}.cssig", content)
            )
        );

        var options = equivalence is null
            ? new AnalyzerOptions(additionalFiles)
            : new AnalyzerOptions(
                additionalFiles,
                new TestConfigOptionsProvider(
                    ImmutableDictionary<string, string>.Empty.Add(
                        "build_property.CsSigEquivalence",
                        equivalence
                    )
                )
            );

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new CsSigAnalyzer()),
            options
        );

        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
        return diagnostics.OrderBy(d => d.Id).ToImmutableArray();
    }

    private sealed class TestConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _global;

        public TestConfigOptionsProvider(ImmutableDictionary<string, string> globals) =>
            _global = new TestConfigOptions(globals);

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
            TestConfigOptions.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            TestConfigOptions.Empty;
    }

    private sealed class TestConfigOptions : AnalyzerConfigOptions
    {
        public static readonly TestConfigOptions Empty = new(
            ImmutableDictionary<string, string>.Empty
        );

        private readonly ImmutableDictionary<string, string> _values;

        public TestConfigOptions(ImmutableDictionary<string, string> values) => _values = values;

        public override bool TryGetValue(
            string key,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value
        )
        {
            if (_values.TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }

            value = null;
            return false;
        }
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}
