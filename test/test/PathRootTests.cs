using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StaticCs.Tests;

public sealed class PathRootTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"{nameof(PathRootTests)}-{Guid.NewGuid():N}"
    );

    public PathRootTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void CreatesAndOpensFilesRelativeToRoot()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        string directoryPath = Path.Combine("a", "b");
        using PathRoot directory = root.CreateSubRoot(directoryPath);

        using (FileStream stream = directory.CreateFile("value.txt"))
        {
            stream.Write("value"u8);
        }

        using FileStream result = root.OpenFile(
            Path.Combine(directoryPath, "value.txt"),
            FileMode.Open,
            FileAccess.Read
        );
        using var reader = new StreamReader(result, Encoding.UTF8);
        Assert.Equal("value", reader.ReadToEnd());
    }

    [Fact]
    public void DotDotCannotEscapeRoot()
    {
        string outsidePath = Path.Combine(
            Path.GetDirectoryName(_testDirectory)!,
            $"{Guid.NewGuid():N}.txt"
        );

        using PathRoot root = PathRoot.Open(_testDirectory);
        root.CreateDirectory("a");
        using (root.CreateFile("a/../inside.txt")) { }

        Assert.True(File.Exists(Path.Combine(_testDirectory, "inside.txt")));
        Assert.Throws<IOException>(() => root.CreateFile("../" + Path.GetFileName(outsidePath)));
        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public void AbsoluteOperationPathsAreRejected()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        string absolutePath = Path.Combine(_testDirectory, "value.txt");

        Assert.Throws<IOException>(() => root.CreateFile(absolutePath));
        Assert.False(File.Exists(absolutePath));
    }

    [Fact]
    public void WindowsCleansDotDotBeforeFilesystemTraversal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PathRoot root = PathRoot.Open(_testDirectory);
        using (root.CreateFile(@"missing\..\value.txt")) { }

        Assert.True(File.Exists(Path.Combine(_testDirectory, "value.txt")));
    }

    [Fact]
    public void WindowsReservedDeviceNamesAreRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PathRoot root = PathRoot.Open(_testDirectory);
        Assert.Throws<ArgumentException>(() => root.CreateFile("NUL.txt"));
    }

    [Fact]
    public void RelativeSymbolicLinksUseDotNetSemantics()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        root.CreateDirectory("dir");
        File.WriteAllText(Path.Combine(_testDirectory, "dir", "target.txt"), "target");

        root.CreateFileSymbolicLink("dir/link.txt", "target.txt");

        Assert.Equal("target.txt", root.GetSymbolicLinkTarget("dir/link.txt"));
        using FileStream result = root.OpenRead("dir/link.txt");
        using var reader = new StreamReader(result, Encoding.UTF8);
        Assert.Equal("target", reader.ReadToEnd());
    }

    [Fact]
    public void IntermediateSymbolicLinksRemainInsideRoot()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        root.CreateDirectory("real");
        root.CreateDirectorySymbolicLink("alias", "real");

        using (root.CreateFile("alias/value.txt")) { }

        Assert.True(File.Exists(Path.Combine(_testDirectory, "real", "value.txt")));
    }

    [Fact]
    public void DotDotInSymbolicLinkTargetIsResolvedFromLinkDirectory()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        root.CreateDirectory("dir");
        File.WriteAllText(Path.Combine(_testDirectory, "target.txt"), "target");
        root.CreateFileSymbolicLink("dir/link.txt", "../target.txt");

        using FileStream result = root.OpenRead("dir/link.txt");
        using var reader = new StreamReader(result, Encoding.UTF8);
        Assert.Equal("target", reader.ReadToEnd());
    }

    [Fact]
    public void SymbolicLinksCannotEscapeRoot()
    {
        string outsideDirectory = CreateOutsideDirectory();
        File.WriteAllText(Path.Combine(outsideDirectory, "secret.txt"), "secret");

        using PathRoot root = PathRoot.Open(_testDirectory);
        root.CreateDirectorySymbolicLink("escape", outsideDirectory);

        Assert.Throws<IOException>(() => root.OpenRead("escape/secret.txt"));
        Assert.Throws<IOException>(() => root.OpenSubRoot("escape/"));
    }

    [Fact]
    public void OpeningRootFollowsTheInitialSymbolicLink()
    {
        string linkPath = _testDirectory + "-link";
        Directory.CreateSymbolicLink(linkPath, _testDirectory);
        try
        {
            using PathRoot root = PathRoot.Open(linkPath);
            using (root.CreateFile("value.txt")) { }

            Assert.True(File.Exists(Path.Combine(_testDirectory, "value.txt")));
        }
        finally
        {
            TryDeleteFile(linkPath);
        }
    }

    [Fact]
    public void MovesAndDeletesFilesRelativeToRoot()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        File.WriteAllText(Path.Combine(_testDirectory, "source.txt"), "source");

        root.MoveFile("source.txt", "moved.txt");
        root.DeleteFile("moved.txt");

        Assert.False(File.Exists(Path.Combine(_testDirectory, "source.txt")));
        Assert.False(File.Exists(Path.Combine(_testDirectory, "moved.txt")));
    }

    [Fact]
    public void MoveFileOnlyOverwritesWhenRequested()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        File.WriteAllText(Path.Combine(_testDirectory, "source.txt"), "source");
        File.WriteAllText(Path.Combine(_testDirectory, "destination.txt"), "destination");

        Assert.ThrowsAny<IOException>(() => root.MoveFile("source.txt", "destination.txt"));
        Assert.Equal("source", File.ReadAllText(Path.Combine(_testDirectory, "source.txt")));
        Assert.Equal(
            "destination",
            File.ReadAllText(Path.Combine(_testDirectory, "destination.txt"))
        );

        root.MoveFile("source.txt", "destination.txt", overwrite: true);

        Assert.False(File.Exists(Path.Combine(_testDirectory, "source.txt")));
        Assert.Equal("source", File.ReadAllText(Path.Combine(_testDirectory, "destination.txt")));
    }

    [Fact]
    public void MoveDirectoryDoesNotOverwrite()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        root.CreateDirectory("source");
        root.CreateDirectory("destination");

        Assert.ThrowsAny<IOException>(() => root.MoveDirectory("source", "destination"));
        root.DeleteDirectory("destination");
        root.MoveDirectory("source", "destination");

        Assert.False(Directory.Exists(Path.Combine(_testDirectory, "source")));
        Assert.True(Directory.Exists(Path.Combine(_testDirectory, "destination")));
    }

    [Fact]
    public void DeleteMethodsDistinguishFilesAndDirectories()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        root.CreateDirectory("directory");
        File.WriteAllText(Path.Combine(_testDirectory, "file.txt"), "file");

        root.DeleteFile("missing.txt");
        Assert.ThrowsAny<IOException>(() => root.DeleteFile("missing/file.txt"));
        Assert.ThrowsAny<IOException>(() => root.DeleteDirectory("missing-directory"));
        Assert.ThrowsAny<IOException>(() => root.DeleteFile("directory"));
        Assert.ThrowsAny<IOException>(() => root.DeleteDirectory("file.txt"));

        root.DeleteFile("file.txt");
        root.DeleteDirectory("directory");
        Assert.False(File.Exists(Path.Combine(_testDirectory, "file.txt")));
        Assert.False(Directory.Exists(Path.Combine(_testDirectory, "directory")));
    }

    [Fact]
    public void DestructiveOperationsRejectTrailingSeparators()
    {
        using PathRoot root = PathRoot.Open(_testDirectory);
        File.WriteAllText(Path.Combine(_testDirectory, "source.txt"), "source");

        Assert.Throws<IOException>(() => root.DeleteFile("source.txt/"));
        Assert.Throws<IOException>(() => root.MoveFile("source.txt/", "destination.txt"));
        Assert.Throws<IOException>(() => root.MoveFile("source.txt", "destination.txt/"));

        Assert.True(File.Exists(Path.Combine(_testDirectory, "source.txt")));
        Assert.False(File.Exists(Path.Combine(_testDirectory, "destination.txt")));
    }

    [Fact]
    public void RecursiveDeleteDoesNotFollowSymbolicLinks()
    {
        string outsideDirectory = CreateOutsideDirectory();
        string outsideFile = Path.Combine(outsideDirectory, "keep.txt");
        File.WriteAllText(outsideFile, "keep");

        using PathRoot root = PathRoot.Open(_testDirectory);
        root.CreateDirectory("tree");
        root.CreateDirectorySymbolicLink("tree/outside", outsideDirectory);
        Assert.Equal(outsideDirectory, root.GetSymbolicLinkTarget("tree/outside"));

        root.DeleteDirectory("tree", recursive: true);

        Assert.True(File.Exists(outsideFile));
        Assert.False(Directory.Exists(Path.Combine(_testDirectory, "tree")));
    }

    [Fact]
    public void OpenRootTracksDirectoryAcrossRename()
    {
        string movedDirectory = _testDirectory + "-moved";
        using PathRoot root = PathRoot.Open(_testDirectory);

        Directory.Move(_testDirectory, movedDirectory);
        try
        {
            using (root.CreateFile("created-after-move.txt")) { }
            Assert.True(File.Exists(Path.Combine(movedDirectory, "created-after-move.txt")));
        }
        finally
        {
            Directory.Move(movedDirectory, _testDirectory);
        }
    }

    [Fact]
    public async Task ConcurrentSymbolicLinkSwapCannotEscapeRoot()
    {
        string outsideDirectory = CreateOutsideDirectory();
        string outsideFile = Path.Combine(outsideDirectory, "probe.txt");
        string slot = Path.Combine(_testDirectory, "slot");
        Directory.CreateDirectory(slot);

        using PathRoot root = PathRoot.Open(_testDirectory);
        using var stop = new CancellationTokenSource();
        Task attacker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                TryDeleteDirectory(slot);
                TryCreateDirectorySymbolicLink(slot, outsideDirectory);
                TryDeleteFile(slot);
                TryCreateDirectory(slot);
            }
        });

        try
        {
            for (int i = 0; i < 2_000; i++)
            {
                try
                {
                    using FileStream stream = root.CreateFile("slot/probe.txt");
                    stream.WriteByte(1);
                    root.DeleteFile("slot/probe.txt");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            stop.Cancel();
            await attacker;
        }

        Assert.False(File.Exists(outsideFile));
    }

    [Fact]
    public void OperationsFailAfterDispose()
    {
        PathRoot root = PathRoot.Open(_testDirectory);
        root.Dispose();

        Assert.Throws<ObjectDisposedException>(() => root.OpenRead("file.txt"));
    }

    public void Dispose()
    {
        TryDeleteFile(_testDirectory);
        TryDeleteDirectory(_testDirectory);

        string parent = Path.GetDirectoryName(_testDirectory)!;
        foreach (
            string path in Directory.EnumerateDirectories(
                parent,
                Path.GetFileName(_testDirectory) + "-outside-*"
            )
        )
        {
            TryDeleteDirectory(path);
        }
    }

    private string CreateOutsideDirectory()
    {
        string path = _testDirectory + $"-outside-{Guid.NewGuid():N}";
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryCreateDirectorySymbolicLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
