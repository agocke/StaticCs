using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace StaticCs;

/// <summary>
/// Provides filesystem operations confined to an open directory.
/// </summary>
/// <remarks>
/// Paths passed to instance methods are relative to this root. Operations reject
/// absolute paths and paths that escape the root through parent-directory
/// components or symbolic links, including links replaced concurrently.
/// </remarks>
public abstract class PathRoot : IDisposable
{
    private const int MaxPathSteps = 1024;
    private const int MaxSymbolicLinks = 40;
    private readonly SafeFileHandle _rootHandle;

    private protected PathRoot(SafeFileHandle rootHandle, string name)
    {
        _rootHandle = rootHandle;
        DisplayName = name;
    }

    /// <summary>
    /// Opens a directory as a filesystem root.
    /// </summary>
    /// <param name="path">
    /// The absolute or current-directory-relative path of the directory to open.
    /// </param>
    /// <returns>A filesystem root attached to the opened directory.</returns>
    /// <remarks>
    /// Symbolic links in <paramref name="path"/> are followed while establishing
    /// the root. The returned root remains attached to the opened directory if it
    /// is subsequently renamed.
    /// </remarks>
    public static PathRoot Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (OperatingSystem.IsWindows())
        {
            return WindowsPathRoot.OpenRoot(path);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return UnixPathRoot.OpenRoot(path);
        }

        throw new PlatformNotSupportedException("PathRoot supports Windows, Linux, and macOS.");
    }

    private protected string DisplayName { get; }

    private protected abstract bool IsPathSeparator(char value);

    private protected abstract bool IsAbsolutePath(string path);

    private protected abstract SafeFileHandle OpenDirectoryAt(nint parent, string name);

    private protected abstract SafeFileHandle OpenFileAt(
        nint parent,
        string name,
        FileMode mode,
        FileAccess access,
        FileShare share
    );

    private protected abstract void CreateDirectoryAt(nint parent, string name);

    private protected abstract void CreateSymbolicLinkAt(
        nint parent,
        string path,
        string pathToTarget,
        bool targetIsDirectory
    );

    private protected abstract string GetSymbolicLinkTargetAt(nint parent, string name);

    private protected abstract void MoveAt(
        nint sourceParent,
        string sourceName,
        nint destinationParent,
        string destinationName,
        bool sourceIsDirectory,
        bool overwrite
    );

    private protected abstract void DeleteFileAt(nint parent, string name);

    private protected abstract void DeleteDirectoryAt(nint parent, string name);

    private protected abstract void DeleteSymbolicLinkAt(nint parent, string name);

    private protected abstract IEnumerable<string> EnumerateDirectory(nint directory);

    private protected abstract PathRoot CreatePlatformRoot(SafeFileHandle rootHandle, string name);

    private protected abstract bool IsAlreadyExists(Exception exception);

    private protected abstract bool IsDirectoryNotEmpty(Exception exception);

    private protected abstract bool IsNotDirectory(Exception exception);

    private protected abstract bool IsNotFound(Exception exception);

    private protected virtual void ValidateComponent(string component) { }

    private protected virtual bool ResolveDotDotLexically => false;

    /// <summary>
    /// Opens a file relative to this root.
    /// </summary>
    /// <param name="path">The path of the file, relative to this root.</param>
    /// <param name="mode">The mode used to open the file.</param>
    /// <returns>The opened file stream.</returns>
    /// <remarks>
    /// The file is opened with <see cref="FileShare.None"/>. Access defaults to
    /// <see cref="FileAccess.Write"/> for <see cref="FileMode.Append"/> and
    /// <see cref="FileAccess.ReadWrite"/> for other modes.
    /// </remarks>
    public FileStream OpenFile(string path, FileMode mode) =>
        OpenFile(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite);

    /// <summary>
    /// Opens a file relative to this root.
    /// </summary>
    /// <param name="path">The path of the file, relative to this root.</param>
    /// <param name="mode">The mode used to open the file.</param>
    /// <param name="access">The access requested for the file.</param>
    /// <returns>The opened file stream.</returns>
    /// <remarks>The file is opened with <see cref="FileShare.None"/>.</remarks>
    public FileStream OpenFile(string path, FileMode mode, FileAccess access) =>
        OpenFile(path, mode, access, FileShare.None);

    /// <summary>
    /// Opens a file relative to this root.
    /// </summary>
    /// <param name="path">The path of the file, relative to this root.</param>
    /// <param name="mode">The mode used to open the file.</param>
    /// <param name="access">The access requested for the file.</param>
    /// <param name="share">
    /// The access that other file handles may request while the file is open.
    /// </param>
    /// <returns>The opened file stream.</returns>
    public FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        ValidateOpenArguments(mode, access, share);
        SafeFileHandle handle = Resolve(
            path,
            createParents: false,
            (Root: this, Path: path, Mode: mode, Access: access, Share: share),
            static (state, parent, name, endsInSeparator) =>
            {
                if (endsInSeparator)
                {
                    throw new IOException(
                        $"The file path '{state.Path}' ends in a directory separator."
                    );
                }

                return state.Root.OpenFileAt(parent, name, state.Mode, state.Access, state.Share);
            }
        );

        try
        {
            return new FileStream(handle, access);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing file for reading relative to this root.
    /// </summary>
    /// <param name="path">The path of the file, relative to this root.</param>
    /// <returns>A read-only stream opened with <see cref="FileShare.Read"/>.</returns>
    public FileStream OpenRead(string path) =>
        OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>
    /// Creates or overwrites a file relative to this root.
    /// </summary>
    /// <param name="path">The path of the file, relative to this root.</param>
    /// <returns>
    /// A read/write stream opened with <see cref="FileShare.None"/>.
    /// </returns>
    public FileStream CreateFile(string path) =>
        OpenFile(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

    /// <summary>
    /// Opens an existing directory as a root confined beneath this root.
    /// </summary>
    /// <param name="path">The path of the directory, relative to this root.</param>
    /// <returns>An independently disposable root for the opened directory.</returns>
    public PathRoot OpenSubRoot(string path)
    {
        SafeFileHandle handle = Resolve(
            path,
            createParents: false,
            this,
            static (root, parent, name, _) => root.OpenDirectoryAt(parent, name)
        );
        return WrapRootHandle(handle, path);
    }

    /// <summary>
    /// Creates a directory and any missing parent directories relative to this root.
    /// </summary>
    /// <param name="path">The directory path, relative to this root.</param>
    /// <remarks>This method succeeds if the directory already exists.</remarks>
    public void CreateDirectory(string path)
    {
        using SafeFileHandle directory = CreateDirectoryHandle(path);
    }

    /// <summary>
    /// Creates a directory and opens it as a root confined beneath this root.
    /// </summary>
    /// <param name="path">The directory path, relative to this root.</param>
    /// <returns>An independently disposable root for the created directory.</returns>
    /// <remarks>
    /// Missing parent directories are created. This method succeeds if the
    /// directory already exists.
    /// </remarks>
    public PathRoot CreateSubRoot(string path)
    {
        SafeFileHandle handle = CreateDirectoryHandle(path);
        return WrapRootHandle(handle, path);
    }

    private SafeFileHandle CreateDirectoryHandle(string path) =>
        Resolve(
            path,
            createParents: true,
            this,
            static (root, parent, name, _) =>
            {
                if (name != ".")
                {
                    try
                    {
                        root.CreateDirectoryAt(parent, name);
                    }
                    catch (Exception exception) when (root.IsAlreadyExists(exception)) { }
                }

                return root.OpenDirectoryAt(parent, name);
            }
        );

    private PathRoot WrapRootHandle(SafeFileHandle handle, string path)
    {
        try
        {
            return CreatePlatformRoot(handle, JoinDisplayPath(DisplayName, path));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a symbolic link to a file relative to this root.
    /// </summary>
    /// <param name="path">The path of the new link, relative to this root.</param>
    /// <param name="pathToTarget">
    /// The target stored in the link. A relative target is relative to the
    /// directory containing the link.
    /// </param>
    /// <remarks>
    /// The target is stored without confinement validation. A link whose target
    /// escapes this root can be created, but cannot be traversed through this
    /// <see cref="PathRoot"/>.
    /// </remarks>
    public void CreateFileSymbolicLink(string path, string pathToTarget) =>
        CreateSymbolicLink(path, pathToTarget, targetIsDirectory: false);

    /// <summary>
    /// Creates a symbolic link to a directory relative to this root.
    /// </summary>
    /// <param name="path">The path of the new link, relative to this root.</param>
    /// <param name="pathToTarget">
    /// The target stored in the link. A relative target is relative to the
    /// directory containing the link.
    /// </param>
    /// <remarks>
    /// The target is stored without confinement validation. A link whose target
    /// escapes this root can be created, but cannot be traversed through this
    /// <see cref="PathRoot"/>.
    /// </remarks>
    public void CreateDirectorySymbolicLink(string path, string pathToTarget) =>
        CreateSymbolicLink(path, pathToTarget, targetIsDirectory: true);

    private void CreateSymbolicLink(string path, string pathToTarget, bool targetIsDirectory)
    {
        ValidatePathArgument(path, nameof(path));
        ArgumentException.ThrowIfNullOrEmpty(pathToTarget);
        if (pathToTarget.IndexOf('\0') >= 0)
        {
            throw new ArgumentException(
                "A path cannot contain a null character.",
                nameof(pathToTarget)
            );
        }

        Resolve(
            path,
            createParents: false,
            (
                Root: this,
                Path: path,
                PathToTarget: pathToTarget,
                TargetIsDirectory: targetIsDirectory
            ),
            static (state, parent, name, endsInSeparator) =>
            {
                if (endsInSeparator)
                {
                    throw new IOException(
                        $"The symbolic-link path '{state.Path}' ends in a directory separator."
                    );
                }

                state.Root.CreateSymbolicLinkAt(
                    parent,
                    name,
                    state.PathToTarget,
                    state.TargetIsDirectory
                );
                return 0;
            }
        );
    }

    /// <summary>
    /// Gets the target stored in a symbolic link relative to this root.
    /// </summary>
    /// <param name="path">The path of the symbolic link, relative to this root.</param>
    /// <returns>
    /// The target stored in the link, without resolving it to a final path.
    /// </returns>
    public string GetSymbolicLinkTarget(string path) =>
        Resolve(
            path,
            createParents: false,
            (Root: this, Path: path),
            static (state, parent, name, endsInSeparator) =>
            {
                if (endsInSeparator)
                {
                    throw new IOException(
                        $"The symbolic-link path '{state.Path}' ends in a directory separator."
                    );
                }

                return state.Root.GetSymbolicLinkTargetAt(parent, name);
            }
        );

    /// <summary>
    /// Moves a file without replacing an existing destination.
    /// </summary>
    /// <param name="sourceFileName">
    /// The path of the file to move, relative to this root.
    /// </param>
    /// <param name="destFileName">
    /// The destination path, relative to this root.
    /// </param>
    public void MoveFile(string sourceFileName, string destFileName) =>
        MoveFile(sourceFileName, destFileName, overwrite: false);

    /// <summary>
    /// Moves a file, optionally replacing an existing destination file.
    /// </summary>
    /// <param name="sourceFileName">
    /// The path of the file to move, relative to this root.
    /// </param>
    /// <param name="destFileName">
    /// The destination path, relative to this root.
    /// </param>
    /// <param name="overwrite">
    /// <see langword="true"/> to replace an existing destination file;
    /// otherwise, <see langword="false"/>.
    /// </param>
    public void MoveFile(string sourceFileName, string destFileName, bool overwrite) =>
        Move(sourceFileName, destFileName, sourceIsDirectory: false, overwrite);

    /// <summary>
    /// Moves a directory without replacing an existing destination.
    /// </summary>
    /// <param name="sourceDirName">
    /// The path of the directory to move, relative to this root.
    /// </param>
    /// <param name="destDirName">
    /// The destination path, relative to this root.
    /// </param>
    public void MoveDirectory(string sourceDirName, string destDirName) =>
        Move(sourceDirName, destDirName, sourceIsDirectory: true, overwrite: false);

    private void Move(
        string sourcePath,
        string destinationPath,
        bool sourceIsDirectory,
        bool overwrite
    )
    {
        ValidatePathArgument(sourcePath, nameof(sourcePath));
        ValidatePathArgument(destinationPath, nameof(destinationPath));

        Resolve(
            sourcePath,
            createParents: false,
            (
                Root: this,
                SourcePath: sourcePath,
                DestinationPath: destinationPath,
                SourceIsDirectory: sourceIsDirectory,
                Overwrite: overwrite
            ),
            static (state, sourceParent, sourceName, sourceEndsInSeparator) =>
            {
                if (sourceEndsInSeparator)
                {
                    throw new IOException(
                        $"The source path '{state.SourcePath}' ends in a directory separator."
                    );
                }

                return state.Root.Resolve(
                    state.DestinationPath,
                    createParents: false,
                    (
                        Root: state.Root,
                        SourceParent: sourceParent,
                        SourceName: sourceName,
                        DestinationPath: state.DestinationPath,
                        SourceIsDirectory: state.SourceIsDirectory,
                        Overwrite: state.Overwrite
                    ),
                    static (
                        destinationState,
                        destinationParent,
                        destinationName,
                        destinationEndsInSeparator
                    ) =>
                    {
                        if (destinationEndsInSeparator)
                        {
                            throw new IOException(
                                $"The destination path '{destinationState.DestinationPath}' ends in a directory separator."
                            );
                        }

                        destinationState.Root.MoveAt(
                            destinationState.SourceParent,
                            destinationState.SourceName,
                            destinationParent,
                            destinationName,
                            destinationState.SourceIsDirectory,
                            destinationState.Overwrite
                        );
                        return 0;
                    }
                );
            }
        );
    }

    /// <summary>
    /// Deletes a file relative to this root.
    /// </summary>
    /// <param name="path">The path of the file, relative to this root.</param>
    /// <remarks>
    /// This method does nothing if the final file does not exist. It throws if
    /// an intermediate directory does not exist.
    /// </remarks>
    public void DeleteFile(string path) => Delete(path, recursive: false, isDirectory: false);

    /// <summary>
    /// Deletes an empty directory relative to this root.
    /// </summary>
    /// <param name="path">The path of the directory, relative to this root.</param>
    public void DeleteDirectory(string path) => DeleteDirectory(path, recursive: false);

    /// <summary>
    /// Deletes a directory relative to this root.
    /// </summary>
    /// <param name="path">The path of the directory, relative to this root.</param>
    /// <param name="recursive">
    /// <see langword="true"/> to delete contained entries recursively;
    /// otherwise, <see langword="false"/> to require an empty directory.
    /// </param>
    /// <remarks>
    /// Recursive deletion removes symbolic links themselves and does not recurse
    /// through their targets.
    /// </remarks>
    public void DeleteDirectory(string path, bool recursive) =>
        Delete(path, recursive, isDirectory: true);

    private void Delete(string path, bool recursive, bool isDirectory)
    {
        ValidatePathArgument(path, nameof(path));
        Resolve(
            path,
            createParents: false,
            (Root: this, Path: path, Recursive: recursive, IsDirectory: isDirectory),
            static (state, parent, name, endsInSeparator) =>
            {
                if (endsInSeparator)
                {
                    throw new IOException(
                        $"The path '{state.Path}' ends in a directory separator."
                    );
                }

                if (name == ".")
                {
                    throw new IOException("The root directory cannot be deleted.");
                }

                if (!state.IsDirectory)
                {
                    state.Root.DeleteFileAt(parent, name);
                }
                else if (state.Recursive)
                {
                    state.Root.DeleteRecursiveAt(parent, name);
                }
                else
                {
                    state.Root.DeleteDirectoryAt(parent, name);
                }

                return 0;
            }
        );
    }

    /// <summary>
    /// Releases the operating-system handle for this root.
    /// </summary>
    public void Dispose() => _rootHandle.Dispose();

    // Resolve one component at a time from retained directory handles rather
    // than validating a complete path and opening it later. If ".." or a
    // symbolic link rewrites the remaining path, restart from the root handle;
    // this keeps traversal resistant to concurrent symbolic-link replacement.
    private TResult Resolve<TState, TResult>(
        string path,
        bool createParents,
        TState state,
        Func<TState, nint, string, bool, TResult> operation
    )
    {
        ValidatePathArgument(path, nameof(path));
        List<string> parts = SplitPath(path, initialPath: true, out bool endsInSeparator);
        if (ResolveDotDotLexically)
        {
            NormalizeDotDot(parts, path);
        }
        bool addedRef = false;
        SafeFileHandle? directory = null;

        try
        {
            _rootHandle.DangerousAddRef(ref addedRef);
            nint root = _rootHandle.DangerousGetHandle();
            nint parent = root;
            int index = 0;
            int steps = 0;
            int symbolicLinks = 0;

            while (true)
            {
                if (++steps > MaxPathSteps)
                {
                    throw new IOException($"The path '{path}' requires too many resolution steps.");
                }

                string part = parts[index];
                if (part == "..")
                {
                    if (index == 0)
                    {
                        throw PathEscapesRoot(path);
                    }

                    parts.RemoveRange(index - 1, 2);
                    if (parts.Count == 0)
                    {
                        parts.Add(".");
                    }

                    directory?.Dispose();
                    directory = null;
                    parent = root;
                    index = 0;
                    continue;
                }

                try
                {
                    if (index == parts.Count - 1)
                    {
                        return operation(state, parent, part, endsInSeparator);
                    }

                    SafeFileHandle next;
                    try
                    {
                        next = OpenDirectoryAt(parent, part);
                    }
                    catch (Exception exception) when (createParents && IsNotFound(exception))
                    {
                        try
                        {
                            CreateDirectoryAt(parent, part);
                        }
                        catch (Exception createException) when (IsAlreadyExists(createException))
                        { }

                        next = OpenDirectoryAt(parent, part);
                    }

                    directory?.Dispose();
                    directory = next;
                    parent = directory.DangerousGetHandle();
                    index++;
                }
                catch (SymbolicLinkEncounteredException exception)
                {
                    if (++symbolicLinks > MaxSymbolicLinks)
                    {
                        throw new IOException(
                            $"The path '{path}' contains too many symbolic links."
                        );
                    }

                    bool linkWasLastPart = index == parts.Count - 1;
                    List<string> targetParts = SplitPath(
                        exception.Target,
                        initialPath: false,
                        out bool targetEndsInSeparator
                    );
                    parts.RemoveAt(index);
                    parts.InsertRange(index, targetParts);
                    if (parts.Count == 0)
                    {
                        parts.Add(".");
                    }
                    else if (ResolveDotDotLexically)
                    {
                        NormalizeDotDot(parts, path);
                    }

                    if (linkWasLastPart && targetEndsInSeparator)
                    {
                        endsInSeparator = true;
                    }

                    directory?.Dispose();
                    directory = null;
                    parent = root;
                    index = 0;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(PathRoot));
        }
        finally
        {
            directory?.Dispose();
            if (addedRef)
            {
                _rootHandle.DangerousRelease();
            }
        }
    }

    private void DeleteRecursiveAt(nint parent, string name)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            SafeFileHandle? directory;
            try
            {
                directory = OpenDirectoryAt(parent, name);
            }
            catch (SymbolicLinkEncounteredException)
            {
                DeleteSymbolicLinkAt(parent, name);
                return;
            }
            catch (Exception exception) when (IsNotDirectory(exception))
            {
                DeleteFileAt(parent, name);
                return;
            }
            catch (Exception exception) when (IsNotFound(exception))
            {
                return;
            }

            using (directory)
            {
                nint directoryHandle = directory.DangerousGetHandle();
                foreach (string child in EnumerateDirectory(directoryHandle))
                {
                    DeleteRecursiveAt(directoryHandle, child);
                }
            }

            try
            {
                DeleteDirectoryAt(parent, name);
                return;
            }
            catch (Exception exception) when (IsNotFound(exception))
            {
                return;
            }
            catch (Exception exception) when (IsDirectoryNotEmpty(exception)) { }
        }

        DeleteDirectoryAt(parent, name);
    }

    private List<string> SplitPath(string path, bool initialPath, out bool endsInSeparator)
    {
        if (IsAbsolutePath(path))
        {
            throw PathEscapesRoot(path);
        }

        endsInSeparator = path.Length > 0 && IsPathSeparator(path[^1]);
        var parts = new List<string>();
        int start = 0;

        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && !IsPathSeparator(path[i]))
            {
                continue;
            }

            if (i > start)
            {
                string part = path[start..i];
                if (part != ".")
                {
                    ValidateComponent(part);
                    parts.Add(part);
                }
            }

            start = i + 1;
        }

        if (initialPath && parts.Count == 0)
        {
            parts.Add(".");
        }

        return parts;
    }

    private string JoinDisplayPath(string root, string path)
    {
        if (root.Length == 0 || IsPathSeparator(root[^1]))
        {
            return root + path;
        }

        char separator = OperatingSystem.IsWindows() ? '\\' : '/';
        return root + separator + path;
    }

    private static void NormalizeDotDot(List<string> parts, string path)
    {
        for (int index = 0; index < parts.Count; )
        {
            if (parts[index] != "..")
            {
                index++;
                continue;
            }

            if (index == 0)
            {
                throw PathEscapesRoot(path);
            }

            parts.RemoveRange(index - 1, 2);
            index--;
        }

        if (parts.Count == 0)
        {
            parts.Add(".");
        }
    }

    private static void ValidateOpenArguments(FileMode mode, FileAccess access, FileShare share)
    {
        if (mode is < FileMode.CreateNew or > FileMode.Append)
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (access is < FileAccess.Read or > FileAccess.ReadWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        if ((share & ~(FileShare.ReadWrite | FileShare.Delete | FileShare.Inheritable)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(share));
        }

        if (mode == FileMode.Append && access != FileAccess.Write)
        {
            throw new ArgumentException("Append mode requires write-only access.", nameof(access));
        }

        if (
            access == FileAccess.Read
            && mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate
        )
        {
            throw new ArgumentException(
                "The requested file mode requires write access.",
                nameof(access)
            );
        }
    }

    private void ValidatePathArgument(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, parameterName);
        if (path.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("A path cannot contain a null character.", parameterName);
        }
    }

    private static IOException PathEscapesRoot(string path) =>
        new($"The path '{path}' escapes the PathRoot.");
}

internal sealed class SymbolicLinkEncounteredException(string target) : Exception
{
    internal string Target { get; } = target;
}

internal sealed class NativePathException(string operation, string path, int errorCode)
    : IOException($"{operation} '{path}' failed with native error {errorCode}.")
{
    internal int ErrorCode { get; } = errorCode;
}
