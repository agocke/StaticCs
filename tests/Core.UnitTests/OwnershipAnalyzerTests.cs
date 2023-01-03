

namespace StaticCs.Ownership.UnitTests;

using Verify = Microsoft.CodeAnalysis.CSharp.Testing.XUnit.AnalyzerVerifier<OwnershipChecker>;

public class OwnershipAnalyzerTests
{
    [Fact]
    public async Task LinearNotDisposed()
    {
        await Verify.VerifyAnalyzerAsync("""
using System;
class D : IDisposable { public void Dispose() {} }
class C
{
    void M()
    {
        var d = new D();
    }
}
""");
    }
}