using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StaticCs;

internal sealed class UnixPathRoot : PathRoot
{
    private static readonly UTF8Encoding Utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private UnixPathRoot(SafeFileHandle rootHandle, string name)
        : base(rootHandle, name) { }

    internal static UnixPathRoot OpenRoot(string path)
    {
        int descriptor;
        do
        {
            descriptor = NativeMethods.OpenAt(
                NativeFlags.CurrentWorkingDirectory,
                path,
                NativeFlags.RootDirectoryOpen,
                0
            );
        } while (descriptor < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (descriptor < 0)
        {
            throw NativeError("open", path);
        }

        return new UnixPathRoot(new SafeFileHandle(descriptor, ownsHandle: true), path);
    }

    private protected override bool IsPathSeparator(char value) => value == '/';

    private protected override bool IsAbsolutePath(string path) => path.StartsWith('/');

    private protected override SafeFileHandle OpenDirectoryAt(nint parent, string name)
    {
        int descriptor;
        do
        {
            descriptor = NativeMethods.OpenAt(
                parent.ToInt32(),
                name,
                NativeFlags.DirectoryOpen,
                0
            );
        } while (descriptor < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (descriptor >= 0)
        {
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }

        int error = Marshal.GetLastPInvokeError();
        ThrowSymbolicLinkOrNative(parent, name, "openat", error);
        throw new InvalidOperationException();
    }

    private protected override SafeFileHandle OpenFileAt(
        nint parent,
        string name,
        FileMode mode,
        FileAccess access,
        FileShare share
    )
    {
        int flags = NativeFlags.ForOpen(
            mode,
            access,
            inheritable: (share & FileShare.Inheritable) != 0
        );
        int descriptor;
        do
        {
            descriptor = NativeMethods.OpenAt(parent.ToInt32(), name, flags, 0x1B6);
        } while (descriptor < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (descriptor >= 0)
        {
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }

        int error = Marshal.GetLastPInvokeError();
        if (mode != FileMode.CreateNew)
        {
            ThrowSymbolicLinkOrNative(parent, name, "openat", error);
        }

        throw NativeError("openat", name, error);
    }

    private protected override void CreateDirectoryAt(nint parent, string name)
    {
        int result;
        do
        {
            result = NativeMethods.MkdirAt(parent.ToInt32(), name, 0x1FF);
        } while (result < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (result != 0)
        {
            throw NativeError("mkdirat", name);
        }
    }

    private protected override void CreateSymbolicLinkAt(
        nint parent,
        string path,
        string pathToTarget,
        bool targetIsDirectory
    )
    {
        int result;
        do
        {
            result = NativeMethods.SymlinkAt(pathToTarget, parent.ToInt32(), path);
        } while (result < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (result != 0)
        {
            throw NativeError("symlinkat", path);
        }
    }

    private protected override void CreateHardLinkAt(
        nint parent,
        string path,
        nint targetParent,
        string pathToTarget
    )
    {
        int result;
        do
        {
            result = NativeMethods.LinkAt(
                targetParent.ToInt32(),
                pathToTarget,
                parent.ToInt32(),
                path,
                0
            );
        } while (result < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (result != 0)
        {
            throw NativeError("linkat", path);
        }
    }

    private protected override string GetSymbolicLinkTargetAt(nint parent, string name)
    {
        if (TryGetLinkTarget(parent, name, out string? target, out int error))
        {
            return target;
        }

        throw NativeError("readlinkat", name, error);
    }

    private protected override void MoveAt(
        nint sourceParent,
        string sourceName,
        nint destinationParent,
        string destinationName,
        bool sourceIsDirectory,
        bool overwrite
    )
    {
        ValidateMoveSourceType(sourceParent, sourceName, sourceIsDirectory);
        int result;
        do
        {
            result = overwrite
                ? NativeMethods.RenameAt(
                    sourceParent.ToInt32(),
                    sourceName,
                    destinationParent.ToInt32(),
                    destinationName
                )
                : NativeMethods.RenameAtNoReplace(
                    sourceParent.ToInt32(),
                    sourceName,
                    destinationParent.ToInt32(),
                    destinationName
                );
        } while (result < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (!overwrite && error == NativeErrors.FunctionNotImplemented)
            {
                throw new PlatformNotSupportedException(
                    "Atomic moves without replacement are not supported by this operating system."
                );
            }

            throw NativeError("renameat", sourceName, error);
        }
    }

    private protected override void DeleteFileAt(nint parent, string name)
    {
        int result;
        do
        {
            result = NativeMethods.UnlinkAt(parent.ToInt32(), name, 0);
        } while (result < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == NativeErrors.NotFound)
            {
                return;
            }

            throw NativeError("unlinkat", name, error);
        }
    }

    private protected override void DeleteDirectoryAt(nint parent, string name)
    {
        int result;
        do
        {
            result = NativeMethods.UnlinkAt(
                parent.ToInt32(),
                name,
                NativeFlags.RemoveDirectory
            );
        } while (result < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (result == 0)
        {
            return;
        }

        int error = Marshal.GetLastPInvokeError();
        if (
            error == NativeErrors.NotDirectory
            && TryGetLinkTarget(parent, name, out _, out _)
        )
        {
            DeleteSymbolicLinkAt(parent, name);
            return;
        }

        throw NativeError("unlinkat", name, error);
    }

    private protected override void DeleteSymbolicLinkAt(nint parent, string name) =>
        DeleteFileAt(parent, name);

    private protected override IEnumerable<string> EnumerateDirectory(nint directory)
    {
        int duplicate;
        do
        {
            duplicate = NativeMethods.Duplicate(directory.ToInt32());
        } while (duplicate < 0 && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted);

        if (duplicate < 0)
        {
            throw NativeError("dup", DisplayName);
        }

        nint stream;
        unsafe
        {
            stream = NativeMethods.OpenDirectoryStream(duplicate);
        }
        if (stream == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            NativeMethods.Close(duplicate);
            throw NativeError("fdopendir", DisplayName, error);
        }

        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                nint entry;
                unsafe
                {
                    entry = NativeMethods.ReadDirectory(stream);
                }
                if (entry == 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                    {
                        throw NativeError("readdir", DisplayName, error);
                    }

                    yield break;
                }

                string entryName = ReadDirectoryEntryName(entry);
                if (entryName is not "." and not "..")
                {
                    yield return entryName;
                }
            }
        }
        finally
        {
            unsafe
            {
                NativeMethods.CloseDirectoryStream(stream);
            }
        }
    }

    private protected override PathRoot CreatePlatformRoot(
        SafeFileHandle rootHandle,
        string name
    ) =>
        new UnixPathRoot(rootHandle, name);

    private protected override bool IsAlreadyExists(Exception exception) =>
        GetErrorCode(exception) == NativeErrors.AlreadyExists;

    private protected override bool IsDirectoryNotEmpty(Exception exception) =>
        GetErrorCode(exception) == NativeErrors.DirectoryNotEmpty;

    private protected override bool IsNotDirectory(Exception exception) =>
        GetErrorCode(exception)
            is NativeErrors.NotDirectory
                or NativeErrors.IsDirectory
                or NativeErrors.OperationNotPermitted;

    private protected override bool IsNotFound(Exception exception) =>
        GetErrorCode(exception) == NativeErrors.NotFound;

    private void ValidateMoveSourceType(nint parent, string name, bool sourceIsDirectory)
    {
        try
        {
            using SafeFileHandle directory = OpenDirectoryAt(parent, name);
            if (!sourceIsDirectory)
            {
                throw new UnauthorizedAccessException($"The path '{name}' is a directory.");
            }
        }
        catch (SymbolicLinkEncounteredException) { }
        catch (Exception exception) when (IsNotDirectory(exception) && !sourceIsDirectory) { }
        catch (Exception exception) when (IsNotDirectory(exception))
        {
            throw new IOException($"The path '{name}' is not a directory.", exception);
        }
    }

    private static void ThrowSymbolicLinkOrNative(
        nint parent,
        string name,
        string operation,
        int error
    )
    {
        if (
            (error == NativeErrors.SymbolicLinkLoop || error == NativeErrors.NotDirectory)
            && TryGetLinkTarget(parent, name, out string? target, out _)
        )
        {
            throw new SymbolicLinkEncounteredException(target);
        }

        throw NativeError(operation, name, error);
    }

    private static bool TryGetLinkTarget(nint parent, string name, out string target, out int error)
    {
        for (int size = 256; size <= 65536; size *= 2)
        {
            byte[] buffer = new byte[size];
            nint length;
            unsafe
            {
                do
                {
                    length = NativeMethods.ReadLinkAt(
                        parent.ToInt32(),
                        name,
                        buffer,
                        (nuint)buffer.Length
                    );
                } while (
                    length < 0
                    && Marshal.GetLastPInvokeError() == NativeErrors.Interrupted
                );
            }
            if (length < 0)
            {
                error = Marshal.GetLastPInvokeError();
                target = "";
                return false;
            }

            if (length.ToInt64() < buffer.Length)
            {
                try
                {
                    target = Utf8.GetString(buffer, 0, length.ToInt32());
                    error = 0;
                    return true;
                }
                catch (DecoderFallbackException exception)
                {
                    throw new IOException(
                        $"The symbolic link '{name}' has a target that is not valid UTF-8.",
                        exception
                    );
                }
            }
        }

        throw new IOException($"The symbolic link '{name}' has an excessively long target.");
    }

    private static string ReadDirectoryEntryName(nint entry)
    {
        int recordLengthOffset;
        int nameOffset;
        if (OperatingSystem.IsMacOS())
        {
            recordLengthOffset = 16;
            nameOffset = 21;
        }
        else if (IntPtr.Size == sizeof(long))
        {
            recordLengthOffset = 16;
            nameOffset = 19;
        }
        else
        {
            recordLengthOffset = 8;
            nameOffset = 11;
        }

        int recordLength = unchecked((ushort)Marshal.ReadInt16(entry, recordLengthOffset));
        int maximumLength = recordLength - nameOffset;
        if (maximumLength <= 0)
        {
            throw new IOException("A directory entry has an invalid record length.");
        }

        int length = 0;
        while (length < maximumLength && Marshal.ReadByte(entry, nameOffset + length) != 0)
        {
            length++;
        }

        if (length == maximumLength)
        {
            throw new IOException("A directory entry name is not null terminated.");
        }

        byte[] bytes = new byte[length];
        Marshal.Copy(entry + nameOffset, bytes, 0, length);
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new IOException("A directory entry name is not valid UTF-8.", exception);
        }
    }

    private static NativePathException NativeError(string operation, string path) =>
        NativeError(operation, path, Marshal.GetLastPInvokeError());

    private static NativePathException NativeError(string operation, string path, int error) =>
        new(operation, path, error);

    private static int? GetErrorCode(Exception exception) =>
        exception is NativePathException native ? native.ErrorCode : null;

    private static class NativeErrors
    {
        internal const int OperationNotPermitted = 1;
        internal const int NotFound = 2;
        internal const int Interrupted = 4;
        internal const int AlreadyExists = 17;
        internal const int NotDirectory = 20;
        internal const int IsDirectory = 21;
        internal static readonly int FunctionNotImplemented = OperatingSystem.IsMacOS() ? 78 : 38;
        internal static readonly int DirectoryNotEmpty = OperatingSystem.IsMacOS() ? 66 : 39;
        internal static readonly int SymbolicLinkLoop = OperatingSystem.IsMacOS() ? 62 : 40;
    }

    private static class NativeFlags
    {
        private const int ReadOnly = 0;
        private const int WriteOnly = 1;
        private const int ReadWrite = 2;

        internal static readonly int Append = OperatingSystem.IsMacOS() ? 0x0008 : 0x0400;
        internal static readonly int Create = OperatingSystem.IsMacOS() ? 0x0200 : 0x0040;
        internal static readonly int Exclusive = OperatingSystem.IsMacOS() ? 0x0800 : 0x0080;
        internal static readonly int Truncate = OperatingSystem.IsMacOS() ? 0x0400 : 0x0200;
        internal static readonly int CloseOnExec = OperatingSystem.IsMacOS()
            ? 0x01000000
            : 0x00080000;
        internal static readonly int Directory = OperatingSystem.IsMacOS()
            ? 0x00100000
            : 0x00010000;
        internal static readonly int NoFollow = OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;
        internal static readonly int RemoveDirectory = OperatingSystem.IsMacOS() ? 0x0080 : 0x0200;
        internal static readonly int CurrentWorkingDirectory = OperatingSystem.IsMacOS()
            ? -2
            : -100;
        internal const uint RenameNoReplace = 1;
        internal const uint RenameExclusive = 0x00000004;

        internal static int DirectoryOpen => ReadOnly | CloseOnExec | Directory | NoFollow;
        internal static int RootDirectoryOpen => ReadOnly | CloseOnExec | Directory;

        internal static int ForOpen(FileMode mode, FileAccess access, bool inheritable)
        {
            int flags =
                access switch
                {
                    FileAccess.Read => ReadOnly,
                    FileAccess.Write => WriteOnly,
                    FileAccess.ReadWrite => ReadWrite,
                    _ => throw new ArgumentOutOfRangeException(nameof(access)),
                }
                | NoFollow
                | (inheritable ? 0 : CloseOnExec);

            return mode switch
            {
                FileMode.CreateNew => flags | Create | Exclusive,
                FileMode.Create => flags | Create | Truncate,
                FileMode.Open => flags,
                FileMode.OpenOrCreate => flags | Create,
                FileMode.Truncate => flags | Truncate,
                FileMode.Append => flags | Create | Append,
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
        }
    }

    private static class NativeMethods
    {
        private const string LibC = "libc";

        internal static int OpenAt(int directory, string path, int flags, uint mode) =>
            OperatingSystem.IsMacOS()
                ? OpenAtMacOS(directory, path, flags, mode)
                : OpenAtLinux(directory, path, flags, mode);

        internal static int RenameAtNoReplace(
            int oldDirectory,
            string oldPath,
            int newDirectory,
            string newPath
        )
        {
            try
            {
                return OperatingSystem.IsMacOS()
                    ? RenameAtMacOSNoReplace(
                        oldDirectory,
                        oldPath,
                        newDirectory,
                        newPath,
                        NativeFlags.RenameExclusive
                    )
                    : RenameAtLinuxNoReplace(
                        oldDirectory,
                        oldPath,
                        newDirectory,
                        newPath,
                        NativeFlags.RenameNoReplace
                    );
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new PlatformNotSupportedException(
                    "Atomic moves without replacement are not supported by this C library.",
                    exception
                );
            }
        }

        [DllImport(
            LibC,
            EntryPoint = "openat",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        private static safe extern int OpenAtLinux(
            int directory,
            string path,
            int flags,
            uint mode
        );

        [DllImport(
            LibC,
            EntryPoint = "__openat",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        private static safe extern int OpenAtMacOS(
            int directory,
            string path,
            int flags,
            uint mode
        );

        [DllImport(
            LibC,
            EntryPoint = "mkdirat",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        internal static safe extern int MkdirAt(int directory, string path, uint mode);

        [DllImport(
            LibC,
            EntryPoint = "symlinkat",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        internal static safe extern int SymlinkAt(
            string target,
            int newDirectory,
            string newPath
        );

        [DllImport(
            LibC,
            EntryPoint = "linkat",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        internal static safe extern int LinkAt(
            int oldDirectory,
            string oldPath,
            int newDirectory,
            string newPath,
            int flags
        );

        [DllImport(
            LibC,
            EntryPoint = "readlinkat",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        internal static unsafe extern nint ReadLinkAt(
            int directory,
            string path,
            [Out]
            byte[] buffer,
            nuint bufferSize
        );

        [DllImport(
            LibC,
            EntryPoint = "renameat",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        internal static safe extern int RenameAt(
            int oldDirectory,
            string oldPath,
            int newDirectory,
            string newPath
        );

        [DllImport(
            LibC,
            EntryPoint = "renameat2",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        private static safe extern int RenameAtLinuxNoReplace(
            int oldDirectory,
            string oldPath,
            int newDirectory,
            string newPath,
            uint flags
        );

        [DllImport(
            LibC,
            EntryPoint = "renameatx_np",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        private static safe extern int RenameAtMacOSNoReplace(
            int oldDirectory,
            string oldPath,
            int newDirectory,
            string newPath,
            uint flags
        );

        [DllImport(
            LibC,
            EntryPoint = "unlinkat",
            SetLastError = true,
            CharSet = CharSet.Ansi,
            ExactSpelling = true
        )]
        internal static safe extern int UnlinkAt(int directory, string path, int flags);

        [DllImport(LibC, EntryPoint = "dup", SetLastError = true, ExactSpelling = true)]
        internal static safe extern int Duplicate(int descriptor);

        [DllImport(LibC, EntryPoint = "fdopendir", SetLastError = true, ExactSpelling = true)]
        internal static unsafe extern nint OpenDirectoryStream(int descriptor);

        [DllImport(LibC, EntryPoint = "readdir", SetLastError = true, ExactSpelling = true)]
        internal static unsafe extern nint ReadDirectory(nint stream);

        [DllImport(LibC, EntryPoint = "closedir", SetLastError = true, ExactSpelling = true)]
        internal static unsafe extern int CloseDirectoryStream(nint stream);

        [DllImport(LibC, EntryPoint = "close", SetLastError = true, ExactSpelling = true)]
        internal static safe extern int Close(int descriptor);
    }
}
