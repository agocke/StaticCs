using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StaticCs;

internal sealed class WindowsPathRoot : PathRoot
{
    private const int DirectoryBufferSize = 64 * 1024;
    private const int MaximumReparseDataBufferSize = 16 * 1024;

    private WindowsPathRoot(SafeFileHandle rootHandle, string name)
        : base(rootHandle, name) { }

    internal static WindowsPathRoot OpenRoot(string path)
    {
        string nativePath = GetExtendedPath(path);
        SafeFileHandle handle = NativeMethods.CreateFile(
            nativePath,
            NativeConstants.FileListDirectory
                | NativeConstants.FileReadAttributes
                | NativeConstants.Synchronize,
            NativeConstants.ShareAll,
            0,
            NativeConstants.OpenExisting,
            NativeConstants.FileFlagBackupSemantics,
            0
        );
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw NativeError("CreateFileW", path, error);
        }

        try
        {
            FileAttributeTagInfo info;
            unsafe
            {
                if (
                    !NativeMethods.GetFileInformationByHandleEx(
                        handle,
                        NativeConstants.FileAttributeTagInfo,
                        &info,
                        (uint)sizeof(FileAttributeTagInfo)
                    )
                )
                {
                    throw NativeError("GetFileInformationByHandleEx", path);
                }
            }

            if ((info.FileAttributes & NativeConstants.FileAttributeDirectory) == 0)
            {
                throw new IOException($"The path '{path}' is not a directory.");
            }

            return new WindowsPathRoot(handle, path);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private protected override bool IsPathSeparator(char value) => value is '/' or '\\';

    private protected override bool IsAbsolutePath(string path) =>
        path.Length > 0 && (IsPathSeparator(path[0]) || (path.Length > 1 && path[1] == ':'));

    private protected override void ValidateComponent(string component)
    {
        if (component == "..")
        {
            return;
        }

        if (component.Length > ushort.MaxValue / sizeof(char))
        {
            throw new PathTooLongException();
        }

        foreach (char value in component)
        {
            if (
                value < ' '
                || value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
            )
            {
                throw new ArgumentException(
                    $"The path component '{component}' is not a valid Windows file name."
                );
            }
        }

        if (IsReservedDeviceName(component))
        {
            throw new ArgumentException(
                $"The path component '{component}' is a reserved Windows device name."
            );
        }
    }

    private protected override SafeFileHandle OpenDirectoryAt(nint parent, string name) =>
        OpenRelative(
            parent,
            name,
            NativeConstants.FileListDirectory
                | NativeConstants.FileReadAttributes
                | NativeConstants.Synchronize,
            NativeConstants.FileOpen,
            NativeConstants.FileDirectoryFile
                | NativeConstants.FileOpenForBackupIntent
                | NativeConstants.FileSynchronousIoNonAlert,
            preventReparse: true,
            followSymbolicLink: true
        );

    private protected override SafeFileHandle OpenFileAt(
        nint parent,
        string name,
        FileMode mode,
        FileAccess access,
        FileShare share
    )
    {
        uint desiredAccess = access switch
        {
            FileAccess.Read => NativeConstants.FileGenericRead,
            FileAccess.Write => NativeConstants.FileGenericWrite,
            FileAccess.ReadWrite => NativeConstants.FileGenericRead
                | NativeConstants.FileGenericWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };

        if (mode == FileMode.Append)
        {
            desiredAccess = NativeConstants.FileGenericWrite & ~NativeConstants.FileWriteData;
        }

        uint disposition = mode switch
        {
            FileMode.CreateNew => NativeConstants.FileCreate,
            FileMode.Create => NativeConstants.FileOpenIf,
            FileMode.Open => NativeConstants.FileOpen,
            FileMode.OpenOrCreate => NativeConstants.FileOpenIf,
            FileMode.Truncate => NativeConstants.FileOpen,
            FileMode.Append => NativeConstants.FileOpenIf,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        SafeFileHandle handle = OpenRelative(
            parent,
            name,
            desiredAccess,
            disposition,
            NativeConstants.FileNonDirectoryFile
                | NativeConstants.FileOpenForBackupIntent
                | NativeConstants.FileSynchronousIoNonAlert,
            preventReparse: true,
            followSymbolicLink: mode != FileMode.CreateNew,
            shareAccess: GetShareAccess(share),
            inheritHandle: (share & FileShare.Inheritable) != 0
        );

        try
        {
            if (mode is FileMode.Create or FileMode.Truncate)
            {
                SetEndOfFile(handle, 0);
            }
            else if (
                mode == FileMode.Append
                && !NativeMethods.SetFilePointerEx(handle, 0, out _, NativeConstants.FileEnd)
            )
            {
                throw NativeError("SetFilePointerEx", name);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private protected override void CreateDirectoryAt(nint parent, string name)
    {
        using SafeFileHandle handle = OpenRelative(
            parent,
            name,
            NativeConstants.FileListDirectory
                | NativeConstants.FileReadAttributes
                | NativeConstants.Synchronize,
            NativeConstants.FileCreate,
            NativeConstants.FileDirectoryFile
                | NativeConstants.FileOpenForBackupIntent
                | NativeConstants.FileSynchronousIoNonAlert,
            preventReparse: false,
            followSymbolicLink: false
        );
    }

    private protected override void CreateSymbolicLinkAt(
        nint parent,
        string path,
        string pathToTarget,
        bool targetIsDirectory
    )
    {
        (
            string substituteName,
            string printName,
            bool isRelative
        ) = PrepareSymbolicLinkTarget(pathToTarget);
        byte[] substituteNameBytes = Encoding.Unicode.GetBytes(substituteName);
        byte[] printNameBytes = Encoding.Unicode.GetBytes(printName);
        int pathBufferLength = substituteNameBytes.Length + printNameBytes.Length;
        int bufferLength = 20 + pathBufferLength;
        if (
            bufferLength > MaximumReparseDataBufferSize
            || pathBufferLength > ushort.MaxValue
        )
        {
            throw new PathTooLongException();
        }

        WithSymbolicLinkPrivilege(() =>
        {
            using SafeFileHandle link = OpenRelative(
                parent,
                path,
                NativeConstants.Synchronize
                    | NativeConstants.FileWriteAttributes
                    | NativeConstants.Delete,
                NativeConstants.FileCreate,
                NativeConstants.FileOpenReparsePoint
                    | NativeConstants.FileOpenForBackupIntent
                    | NativeConstants.FileSynchronousIoNonAlert
                    | (
                        targetIsDirectory
                            ? NativeConstants.FileDirectoryFile
                            : NativeConstants.FileNonDirectoryFile
                    ),
                preventReparse: false,
                followSymbolicLink: false,
                shareAccess: 0
            );

            byte[] buffer = new byte[bufferLength];
            Span<byte> span = buffer;
            BinaryPrimitives.WriteUInt32LittleEndian(
                span,
                NativeConstants.IoReparseTagSymbolicLink
            );
            BinaryPrimitives.WriteUInt16LittleEndian(
                span[4..],
                checked((ushort)(12 + pathBufferLength))
            );
            BinaryPrimitives.WriteUInt16LittleEndian(
                span[10..],
                checked((ushort)substituteNameBytes.Length)
            );
            BinaryPrimitives.WriteUInt16LittleEndian(
                span[12..],
                checked((ushort)substituteNameBytes.Length)
            );
            BinaryPrimitives.WriteUInt16LittleEndian(
                span[14..],
                checked((ushort)printNameBytes.Length)
            );
            BinaryPrimitives.WriteUInt32LittleEndian(
                span[16..],
                isRelative ? NativeConstants.SymbolicLinkFlagRelative : 0
            );
            substituteNameBytes.CopyTo(span[20..]);
            printNameBytes.CopyTo(span[(20 + substituteNameBytes.Length)..]);

            unsafe
            {
                fixed (byte* bufferPointer = buffer)
                {
                    if (
                        NativeMethods.DeviceIoControl(
                            link,
                            NativeConstants.FsctlSetReparsePoint,
                            bufferPointer,
                            (uint)buffer.Length,
                            null,
                            0,
                            out _,
                            0
                        )
                    )
                    {
                        return;
                    }

                    int error = Marshal.GetLastPInvokeError();
                    NativePathException failure = NativeError(
                        "FSCTL_SET_REPARSE_POINT",
                        path,
                        error
                    );
                    try
                    {
                        MarkForDeletion(link, path);
                    }
                    catch (IOException cleanupFailure)
                    {
                        throw new AggregateException(failure, cleanupFailure);
                    }

                    throw failure;
                }
            }
        });
    }

    private protected override void CreateHardLinkAt(
        nint parent,
        string path,
        nint targetParent,
        string pathToTarget
    )
    {
        using SafeFileHandle target = OpenRelative(
            targetParent,
            pathToTarget,
            desiredAccess: 0,
            disposition: NativeConstants.FileOpen,
            options: NativeConstants.FileOpenReparsePoint
                | NativeConstants.FileOpenForBackupIntent
                | NativeConstants.FileNonDirectoryFile,
            preventReparse: false,
            followSymbolicLink: false
        );
        byte[] information = CreateNameInformation(
            firstValue: 0,
            firstValueIsUInt32: false,
            parent,
            path
        );

        int status;
        unsafe
        {
            fixed (byte* informationPointer = information)
            {
                status = NativeMethods.NtSetInformationFile(
                    target,
                    out _,
                    informationPointer,
                    (uint)information.Length,
                    NativeConstants.FileLinkInformation
                );
            }
        }

        ThrowIfFailed(status, "NtSetInformationFile(FileLinkInformation)", path);
    }

    private protected override string GetSymbolicLinkTargetAt(nint parent, string name)
    {
        using SafeFileHandle handle = OpenEntry(
            parent,
            name,
            NativeConstants.FileReadAttributes
                | NativeConstants.FileReadEa
                | NativeConstants.Synchronize
        );
        return ReadLinkTarget(handle, name);
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
        using SafeFileHandle source = OpenRelative(
            sourceParent,
            sourceName,
            NativeConstants.Delete | NativeConstants.Synchronize,
            NativeConstants.FileOpen,
            NativeConstants.FileOpenReparsePoint
                | NativeConstants.FileOpenForBackupIntent
                | NativeConstants.FileSynchronousIoNonAlert
                | (
                    sourceIsDirectory
                        ? NativeConstants.FileDirectoryFile
                        : NativeConstants.FileNonDirectoryFile
                ),
            preventReparse: false,
            followSymbolicLink: false
        );
        byte[] information = CreateNameInformation(
            (overwrite ? NativeConstants.FileRenameReplaceIfExists : 0)
                | NativeConstants.FileRenamePosixSemantics,
            firstValueIsUInt32: true,
            destinationParent,
            destinationName
        );

        int status;
        unsafe
        {
            fixed (byte* informationPointer = information)
            {
                status = NativeMethods.NtSetInformationFile(
                    source,
                    out _,
                    informationPointer,
                    (uint)information.Length,
                    NativeConstants.FileRenameInformationEx
                );
            }
        }

        if (
            status
            is NativeConstants.StatusInvalidInfoClass
                or NativeConstants.StatusInvalidParameter
                or NativeConstants.StatusNotSupported
        )
        {
            information = CreateNameInformation(
                firstValue: overwrite ? 1u : 0u,
                firstValueIsUInt32: false,
                destinationParent,
                destinationName
            );
            unsafe
            {
                fixed (byte* informationPointer = information)
                {
                    status = NativeMethods.NtSetInformationFile(
                        source,
                        out _,
                        informationPointer,
                        (uint)information.Length,
                        NativeConstants.FileRenameInformation
                    );
                }
            }
        }

        ThrowIfFailed(status, "NtSetInformationFile(FileRenameInformation)", sourceName);
    }

    private protected override void DeleteFileAt(nint parent, string name)
    {
        try
        {
            DeleteEntryAt(parent, name, NativeConstants.FileNonDirectoryFile);
        }
        catch (Exception exception) when (IsNotFound(exception)) { }
    }

    private protected override void DeleteDirectoryAt(nint parent, string name) =>
        DeleteEntryAt(parent, name, NativeConstants.FileDirectoryFile);

    private protected override void DeleteSymbolicLinkAt(nint parent, string name) =>
        DeleteEntryAt(parent, name, typeOption: 0);

    private static void DeleteEntryAt(nint parent, string name, uint typeOption)
    {
        SafeFileHandle handle;
        try
        {
            handle = OpenRelative(
                parent,
                name,
                NativeConstants.Delete
                    | NativeConstants.FileReadAttributes
                    | NativeConstants.Synchronize,
                NativeConstants.FileOpen,
                NativeConstants.FileOpenReparsePoint
                    | NativeConstants.FileOpenForBackupIntent
                    | NativeConstants.FileSynchronousIoNonAlert
                    | typeOption,
                preventReparse: false,
                followSymbolicLink: false
            );
        }
        catch (NativePathException exception)
            when (exception.ErrorCode == NativeConstants.ErrorAccessDenied)
        {
            handle = OpenRelative(
                parent,
                name,
                NativeConstants.Delete,
                NativeConstants.FileOpen,
                NativeConstants.FileOpenReparsePoint
                    | NativeConstants.FileOpenForBackupIntent
                    | NativeConstants.FileSynchronousIoNonAlert
                    | typeOption,
                preventReparse: false,
                followSymbolicLink: false
            );
        }

        using (handle)
        {
            MarkForDeletion(handle, name);
        }
    }

    private protected override IEnumerable<string> EnumerateDirectory(nint directory)
    {
        var names = new List<string>();
        nint buffer = Marshal.AllocHGlobal(DirectoryBufferSize);
        try
        {
            unsafe
            {
                while (true)
                {
                    int status = NativeMethods.NtQueryDirectoryFile(
                        directory,
                        0,
                        0,
                        0,
                        out IoStatusBlock ioStatus,
                        (void*)buffer,
                        DirectoryBufferSize,
                        NativeConstants.FileFullDirectoryInformation,
                        returnSingleEntry: 0,
                        fileName: null,
                        restartScan: 0
                    );

                    if (
                        status
                        is NativeConstants.StatusNoMoreFiles
                            or NativeConstants.StatusFileNotFound
                            or NativeConstants.StatusObjectNameNotFound
                    )
                    {
                        break;
                    }

                    if (status < 0 && status != NativeConstants.StatusBufferOverflow)
                    {
                        ThrowIfFailed(status, "NtQueryDirectoryFile", DisplayName);
                    }

                    nuint returned = ioStatus.Information;
                    if (returned == 0)
                    {
                        break;
                    }

                    nuint offset = 0;
                    while (offset < returned)
                    {
                        var entry = (FileFullDirectoryInformation*)(
                            buffer + checked((nint)offset)
                        );
                        uint nameLength = entry->FileNameLength;
                        if (
                            (nameLength & 1) != 0
                            || offset
                                + (nuint)NativeConstants.FileFullDirectoryInformationNameOffset
                                + nameLength
                                > returned
                        )
                        {
                            throw new IOException("Windows returned an invalid directory entry.");
                        }

                        string name = new(&entry->FileName, 0, checked((int)nameLength / 2));
                        if (name is not "." and not "..")
                        {
                            names.Add(name);
                        }

                        if (entry->NextEntryOffset == 0)
                        {
                            break;
                        }

                        if (entry->NextEntryOffset > returned - offset)
                        {
                            throw new IOException(
                                "Windows returned an invalid directory entry offset."
                            );
                        }

                        offset += entry->NextEntryOffset;
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return names;
    }

    private protected override PathRoot CreatePlatformRoot(
        SafeFileHandle rootHandle,
        string name
    ) =>
        new WindowsPathRoot(rootHandle, name);

    private protected override bool IsAlreadyExists(Exception exception) =>
        GetErrorCode(exception)
            is NativeConstants.ErrorFileExists
                or NativeConstants.ErrorAlreadyExists;

    private protected override bool IsDirectoryNotEmpty(Exception exception) =>
        GetErrorCode(exception) == NativeConstants.ErrorDirectoryNotEmpty;

    private protected override bool IsNotDirectory(Exception exception) =>
        GetErrorCode(exception) == NativeConstants.ErrorDirectory;

    private protected override bool IsNotFound(Exception exception) =>
        GetErrorCode(exception)
            is NativeConstants.ErrorFileNotFound
                or NativeConstants.ErrorPathNotFound;

    private protected override bool ResolveDotDotLexically => true;

    private static SafeFileHandle OpenEntry(nint parent, string name, uint desiredAccess) =>
        OpenRelative(
            parent,
            name,
            desiredAccess,
            NativeConstants.FileOpen,
            NativeConstants.FileOpenReparsePoint
                | NativeConstants.FileOpenForBackupIntent
                | NativeConstants.FileSynchronousIoNonAlert,
            preventReparse: false,
            followSymbolicLink: false
        );

    private static SafeFileHandle OpenRelative(
        nint parent,
        string name,
        uint desiredAccess,
        uint disposition,
        uint options,
        bool preventReparse,
        bool followSymbolicLink,
        uint shareAccess = NativeConstants.ShareAll,
        bool inheritHandle = false
    )
    {
        int status = NtCreateRelative(
            out nint rawHandle,
            parent,
            name,
            desiredAccess,
            shareAccess,
            disposition,
            options,
            preventReparse,
            inheritHandle
        );
        if (status >= 0)
        {
            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }

        if (status == NativeConstants.StatusInvalidParameter && preventReparse)
        {
            status = NtCreateRelative(
                out rawHandle,
                parent,
                name,
                desiredAccess,
                shareAccess,
                disposition,
                options | NativeConstants.FileOpenReparsePoint,
                preventReparse: false,
                inheritHandle
            );
            if (status >= 0)
            {
                var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
                try
                {
                    if (IsReparsePoint(handle))
                    {
                        string fallbackTarget = ReadLinkTarget(handle, name);
                        throw new SymbolicLinkEncounteredException(fallbackTarget);
                    }

                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
        }

        if (
            followSymbolicLink
            && status
                is NativeConstants.StatusReparsePointEncountered
                    or NativeConstants.StatusNotADirectory
            && TryGetLinkTarget(parent, name, out string? target)
        )
        {
            throw new SymbolicLinkEncounteredException(target);
        }

        if (
            !followSymbolicLink
            && status == NativeConstants.StatusReparsePointEncountered
            && disposition == NativeConstants.FileCreate
        )
        {
            throw NativeError("NtCreateFile", name, NativeConstants.ErrorAlreadyExists);
        }

        throw NativeErrorFromStatus("NtCreateFile", name, status);
    }

    private static int NtCreateRelative(
        out nint handle,
        nint parent,
        string name,
        uint desiredAccess,
        uint shareAccess,
        uint disposition,
        uint options,
        bool preventReparse,
        bool inheritHandle
    )
    {
        unsafe
        {
            string objectName = name == "." ? "" : name;
            fixed (char* namePointer = objectName)
            {
                var unicodeName = new UnicodeString
                {
                    Length = checked((ushort)(objectName.Length * sizeof(char))),
                    MaximumLength = checked((ushort)(objectName.Length * sizeof(char))),
                    Buffer = objectName.Length == 0 ? null : namePointer,
                };
                var attributes = new ObjectAttributes
                {
                    Length = (uint)sizeof(ObjectAttributes),
                    RootDirectory = parent,
                    ObjectName = &unicodeName,
                    Attributes =
                        NativeConstants.ObjCaseInsensitive
                        | (preventReparse ? NativeConstants.ObjDontReparse : 0)
                        | (inheritHandle ? NativeConstants.ObjInherit : 0),
                };

                return NativeMethods.NtCreateFile(
                    out handle,
                    desiredAccess,
                    &attributes,
                    out _,
                    null,
                    NativeConstants.FileAttributeNormal,
                    shareAccess,
                    disposition,
                    options,
                    null,
                    0
                );
            }
        }
    }

    private static bool TryGetLinkTarget(nint parent, string name, out string target)
    {
        try
        {
            using SafeFileHandle handle = OpenEntry(
                parent,
                name,
                NativeConstants.FileReadAttributes
                    | NativeConstants.FileReadEa
                    | NativeConstants.Synchronize
            );
            target = ReadLinkTarget(handle, name);
            return true;
        }

        catch (NativePathException exception)
            when (exception.ErrorCode
                    is NativeConstants.ErrorNotAReparsePoint
                        or NativeConstants.ErrorFileNotFound
                        or NativeConstants.ErrorPathNotFound
            )
        {
            target = "";
            return false;
        }
    }

    private static uint GetShareAccess(FileShare share)
    {
        uint result = 0;
        if ((share & FileShare.Read) != 0)
        {
            result |= NativeConstants.FileShareRead;
        }

        if ((share & FileShare.Write) != 0)
        {
            result |= NativeConstants.FileShareWrite;
        }

        if ((share & FileShare.Delete) != 0)
        {
            result |= NativeConstants.FileShareDelete;
        }

        return result;
    }

    private static bool IsReparsePoint(SafeFileHandle handle)
    {
        unsafe
        {
            FileAttributeTagInfo info;
            if (
                !NativeMethods.GetFileInformationByHandleEx(
                    handle,
                    NativeConstants.FileAttributeTagInfo,
                    &info,
                    (uint)sizeof(FileAttributeTagInfo)
                )
            )
            {
                throw NativeError("GetFileInformationByHandleEx", "");
            }

            return (info.FileAttributes & NativeConstants.FileAttributeReparsePoint) != 0;
        }
    }

    private static string ReadLinkTarget(SafeFileHandle handle, string path)
    {
        byte[] buffer = new byte[MaximumReparseDataBufferSize];
        uint returned;
        unsafe
        {
            fixed (byte* bufferPointer = buffer)
            {
                if (
                    !NativeMethods.DeviceIoControl(
                        handle,
                        NativeConstants.FsctlGetReparsePoint,
                        null,
                        0,
                        bufferPointer,
                        (uint)buffer.Length,
                        out returned,
                        0
                    )
                )
                {
                    throw NativeError("FSCTL_GET_REPARSE_POINT", path);
                }
            }
        }

        if (returned < 8)
        {
            throw new IOException("Windows returned an invalid reparse point.");
        }

        ReadOnlySpan<byte> data = buffer.AsSpan(0, checked((int)returned));
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data);
        ushort reparseDataLength = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (8 + reparseDataLength > data.Length)
        {
            throw new IOException("Windows returned a truncated reparse point.");
        }

        int pathBufferOffset;
        ushort substituteOffset;
        ushort substituteLength;
        bool isRelative = false;
        if (tag == NativeConstants.IoReparseTagSymbolicLink)
        {
            if (reparseDataLength < 12)
            {
                throw new IOException("Windows returned an invalid symbolic link.");
            }

            substituteOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
            substituteLength = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
            isRelative = (flags & NativeConstants.SymbolicLinkFlagRelative) != 0;
            pathBufferOffset = 20;
        }
        else if (tag == NativeConstants.IoReparseTagMountPoint)
        {
            if (reparseDataLength < 8)
            {
                throw new IOException("Windows returned an invalid mount point.");
            }

            substituteOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
            substituteLength = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
            pathBufferOffset = 16;
        }
        else
        {
            throw NativeError("FSCTL_GET_REPARSE_POINT", path, NativeConstants.ErrorCantAccessFile);
        }

        int reparseEnd = 8 + reparseDataLength;
        if (
            (substituteOffset & 1) != 0
            || (substituteLength & 1) != 0
            || pathBufferOffset + substituteOffset + substituteLength > reparseEnd
        )
        {
            throw new IOException("Windows returned an invalid reparse target.");
        }

        string target = Encoding.Unicode.GetString(
            data.Slice(pathBufferOffset + substituteOffset, substituteLength)
        );
        return isRelative ? target : NormalizeNtPath(target);
    }

    private static string GetExtendedPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (
            fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)
        )
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath[2..];
        }

        return @"\\?\" + fullPath;
    }

    private static (
        string SubstituteName,
        string PrintName,
        bool IsRelative
    ) PrepareSymbolicLinkTarget(string pathToTarget)
    {
        string target = pathToTarget.Replace('/', '\\');
        bool hasVolumeName =
            target.Length > 1 && target[1] == ':'
            || target.StartsWith(@"\\", StringComparison.Ordinal);
        bool isFullyQualified =
            target.Length > 2 && target[1] == ':' && target[2] == '\\'
            || target.StartsWith(@"\\", StringComparison.Ordinal);

        if (hasVolumeName && !isFullyQualified)
        {
            target = Path.GetFullPath(target);
        }

        if (!hasVolumeName)
        {
            return (target, target, true);
        }

        if (target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = target[8..];
            return (@"\??\UNC\" + suffix, @"\\" + suffix, false);
        }

        if (
            target.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
        )
        {
            string suffix = target[4..];
            return (@"\??\" + suffix, target, false);
        }

        if (target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (@"\??\UNC\" + target[2..], target, false);
        }

        return (@"\??\" + target, target, false);
    }

    private static string NormalizeNtPath(string path)
    {
        const string ntPrefix = @"\??\";
        const string uncPrefix = @"\??\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        if (path.StartsWith(ntPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string dosPath = path[ntPrefix.Length..];
            if (dosPath.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\?\" + dosPath;
            }

            return dosPath;
        }

        return path;
    }

    private static void SetEndOfFile(SafeFileHandle handle, long length)
    {
        unsafe
        {
            var information = new FileEndOfFileInfo { EndOfFile = length };
            if (
                !NativeMethods.SetFileInformationByHandle(
                    handle,
                    NativeConstants.FileEndOfFileInfo,
                    &information,
                    (uint)sizeof(FileEndOfFileInfo)
                )
            )
            {
                throw NativeError("SetFileInformationByHandle(FileEndOfFileInfo)", "");
            }
        }
    }

    private static byte[] CreateNameInformation(
        uint firstValue,
        bool firstValueIsUInt32,
        nint rootDirectory,
        string name
    )
    {
        int rootOffset = IntPtr.Size;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);
        int minimumLength = nameOffset + sizeof(uint);
        byte[] information = new byte[Math.Max(minimumLength, nameOffset + nameBytes.Length)];
        Span<byte> span = information;

        if (firstValueIsUInt32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, firstValue);
        }
        else
        {
            span[0] = checked((byte)firstValue);
        }

        if (IntPtr.Size == sizeof(long))
        {
            BinaryPrimitives.WriteInt64LittleEndian(span[rootOffset..], rootDirectory);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[rootOffset..], rootDirectory.ToInt32());
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            span[lengthOffset..],
            checked((uint)nameBytes.Length)
        );
        nameBytes.CopyTo(span[nameOffset..]);
        return information;
    }

    private static void MarkForDeletion(SafeFileHandle handle, string path)
    {
        unsafe
        {
            uint flags =
                NativeConstants.FileDispositionDelete
                | NativeConstants.FileDispositionPosixSemantics
                | NativeConstants.FileDispositionIgnoreReadonlyAttribute;
            int status = NativeMethods.NtSetInformationFile(
                handle,
                out _,
                &flags,
                sizeof(uint),
                NativeConstants.FileDispositionInformationEx
            );
            if (
                status
                is NativeConstants.StatusInvalidInfoClass
                    or NativeConstants.StatusInvalidParameter
                    or NativeConstants.StatusNotSupported
            )
            {
                byte delete = 1;
                status = NativeMethods.NtSetInformationFile(
                    handle,
                    out _,
                    &delete,
                    sizeof(byte),
                    NativeConstants.FileDispositionInformation
                );
            }

            ThrowIfFailed(status, "NtSetInformationFile(FileDispositionInformation)", path);
        }
    }

    private static void WithSymbolicLinkPrivilege(Action action)
    {
        bool revertToSelf = false;
        SafeFileHandle token;
        if (
            !NativeMethods.OpenThreadToken(
                NativeMethods.GetCurrentThread(),
                NativeConstants.TokenQuery | NativeConstants.TokenAdjustPrivileges,
                openAsSelf: false,
                out token
            )
        )
        {
            int error = Marshal.GetLastPInvokeError();
            token.Dispose();
            if (
                error != NativeConstants.ErrorNoToken
                || !NativeMethods.ImpersonateSelf(NativeConstants.SecurityImpersonation)
            )
            {
                action();
                return;
            }

            revertToSelf = true;
        }

        try
        {
            if (revertToSelf)
            {
                if (
                    !NativeMethods.OpenThreadToken(
                        NativeMethods.GetCurrentThread(),
                        NativeConstants.TokenQuery | NativeConstants.TokenAdjustPrivileges,
                        openAsSelf: false,
                        out token
                    )
                )
                {
                    token.Dispose();
                    action();
                    return;
                }
            }

            using (token)
            {
                if (
                    NativeMethods.LookupPrivilegeValue(
                        null,
                        "SeCreateSymbolicLinkPrivilege",
                        out Luid luid
                    )
                )
                {
                    var privileges = new TokenPrivileges
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = NativeConstants.SePrivilegeEnabled,
                    };
                    RunWithPrivilege(token, ref privileges, action);
                    return;
                }

                action();
            }
        }
        finally
        {
            if (revertToSelf && !NativeMethods.RevertToSelf())
            {
                Environment.FailFast("PathRoot could not revert the thread impersonation token.");
            }
        }
    }

    private static void RunWithPrivilege(
        SafeFileHandle token,
        ref TokenPrivileges privileges,
        Action action
    )
    {
        TokenPrivileges previousState = default;
        uint returnLength = 0;
        bool adjusted;
        int error;
        unsafe
        {
            fixed (TokenPrivileges* privilegesPointer = &privileges)
            {
                adjusted = NativeMethods.AdjustTokenPrivileges(
                    token,
                    disableAllPrivileges: false,
                    privilegesPointer,
                    (uint)sizeof(TokenPrivileges),
                    &previousState,
                    &returnLength
                );
                error = Marshal.GetLastPInvokeError();
            }
        }

        if (!adjusted || error == NativeConstants.ErrorNotAllAssigned)
        {
            action();
            return;
        }

        try
        {
            action();
        }
        finally
        {
            if (previousState.PrivilegeCount != 0)
            {
                bool restored;
                unsafe
                {
                    restored = NativeMethods.AdjustTokenPrivileges(
                        token,
                        disableAllPrivileges: false,
                        &previousState,
                        0,
                        null,
                        null
                    );
                }

                if (!restored)
                {
                    Environment.FailFast(
                        "PathRoot could not restore the thread token privileges."
                    );
                }
            }
        }
    }

    private static bool IsReservedDeviceName(string component)
    {
        string candidate = component.TrimEnd(' ', '.');
        int extension = candidate.IndexOf('.');
        if (extension >= 0)
        {
            candidate = candidate[..extension];
        }

        if (
            candidate.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || candidate.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return candidate.Length == 4
            && candidate[3] is >= '1' and <= '9'
            && (
                candidate.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
            );
    }

    private static void ThrowIfFailed(int status, string operation, string path)
    {
        if (status < 0)
        {
            throw NativeErrorFromStatus(operation, path, status);
        }
    }

    private static NativePathException NativeErrorFromStatus(
        string operation,
        string path,
        int status
    ) => NativeError(operation, path, unchecked((int)NativeMethods.RtlNtStatusToDosError(status)));

    private static NativePathException NativeError(string operation, string path) =>
        NativeError(operation, path, Marshal.GetLastPInvokeError());

    private static NativePathException NativeError(string operation, string path, int error) =>
        new(operation, path, error);

    private static int? GetErrorCode(Exception exception) =>
        exception is NativePathException native ? native.ErrorCode : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal nint RootDirectory;
        internal UnicodeString* ObjectName;
        internal uint Attributes;
        internal void* SecurityDescriptor;
        internal void* SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint StatusOrPointer;
        internal nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileEndOfFileInfo
    {
        internal long EndOfFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileFullDirectoryInformation
    {
        internal uint NextEntryOffset;
        internal uint FileIndex;
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal long EndOfFile;
        internal long AllocationSize;
        internal uint FileAttributes;
        internal uint FileNameLength;
        internal uint EaSize;
        internal char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        internal uint PrivilegeCount;
        internal Luid Luid;
        internal uint Attributes;
    }

    private static class NativeConstants
    {
        internal const uint ObjInherit = 0x00000002;
        internal const uint ObjCaseInsensitive = 0x00000040;
        internal const uint ObjDontReparse = 0x00001000;

        internal const uint Delete = 0x00010000;
        internal const uint ReadControl = 0x00020000;
        internal const uint Synchronize = 0x00100000;

        internal const uint FileReadData = 0x00000001;
        internal const uint FileListDirectory = 0x00000001;
        internal const uint FileWriteData = 0x00000002;
        internal const uint FileAppendData = 0x00000004;
        internal const uint FileReadEa = 0x00000008;
        internal const uint FileWriteEa = 0x00000010;
        internal const uint FileReadAttributes = 0x00000080;
        internal const uint FileWriteAttributes = 0x00000100;

        internal const uint FileGenericRead =
            ReadControl | FileReadData | FileReadAttributes | FileReadEa | Synchronize;
        internal const uint FileGenericWrite =
            ReadControl
            | FileWriteData
            | FileWriteAttributes
            | FileWriteEa
            | FileAppendData
            | Synchronize;

        internal const uint FileShareRead = 0x1;
        internal const uint FileShareWrite = 0x2;
        internal const uint FileShareDelete = 0x4;
        internal const uint ShareAll = FileShareRead | FileShareWrite | FileShareDelete;

        internal const uint FileAttributeDirectory = 0x00000010;
        internal const uint FileAttributeNormal = 0x00000080;
        internal const uint FileAttributeReparsePoint = 0x00000400;

        internal const uint FileOpen = 1;
        internal const uint FileCreate = 2;
        internal const uint FileOpenIf = 3;

        internal const uint FileDirectoryFile = 0x00000001;
        internal const uint FileSynchronousIoNonAlert = 0x00000020;
        internal const uint FileNonDirectoryFile = 0x00000040;
        internal const uint FileOpenForBackupIntent = 0x00004000;
        internal const uint FileOpenReparsePoint = 0x00200000;

        internal const uint FileFullDirectoryInformation = 2;
        internal const uint FileEndOfFileInfo = 6;
        internal const uint FileAttributeTagInfo = 9;
        internal const uint FileRenameInformation = 10;
        internal const uint FileLinkInformation = 11;
        internal const uint FileDispositionInformation = 13;
        internal const uint FileDispositionInformationEx = 64;
        internal const uint FileRenameInformationEx = 65;

        internal const uint FileDispositionDelete = 0x1;
        internal const uint FileDispositionPosixSemantics = 0x2;
        internal const uint FileDispositionIgnoreReadonlyAttribute = 0x10;
        internal const uint FileRenameReplaceIfExists = 0x1;
        internal const uint FileRenamePosixSemantics = 0x2;

        internal const uint FsctlSetReparsePoint = 0x000900A4;
        internal const uint FsctlGetReparsePoint = 0x000900A8;
        internal const uint IoReparseTagMountPoint = 0xA0000003;
        internal const uint IoReparseTagSymbolicLink = 0xA000000C;
        internal const uint SymbolicLinkFlagRelative = 1;

        internal const uint OpenExisting = 3;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileEnd = 2;

        internal const int StatusBufferOverflow = unchecked((int)0x80000005);
        internal const int StatusNoMoreFiles = unchecked((int)0x80000006);
        internal const int StatusInvalidInfoClass = unchecked((int)0xC0000003);
        internal const int StatusInvalidParameter = unchecked((int)0xC000000D);
        internal const int StatusFileNotFound = unchecked((int)0xC000000F);
        internal const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        internal const int StatusNotSupported = unchecked((int)0xC00000BB);
        internal const int StatusNotADirectory = unchecked((int)0xC0000103);
        internal const int StatusReparsePointEncountered = unchecked((int)0xC000050B);

        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const int ErrorAccessDenied = 5;
        internal const int ErrorFileExists = 80;
        internal const int ErrorAlreadyExists = 183;
        internal const int ErrorDirectoryNotEmpty = 145;
        internal const int ErrorDirectory = 267;
        internal const int ErrorNoToken = 1008;
        internal const int ErrorNotAllAssigned = 1300;
        internal const int ErrorCantAccessFile = 1920;
        internal const int ErrorNotAReparsePoint = 4390;

        internal const int SecurityImpersonation = 2;
        internal const uint TokenAdjustPrivileges = 0x0020;
        internal const uint TokenQuery = 0x0008;
        internal const uint SePrivilegeEnabled = 0x00000002;

        internal static readonly int FileFullDirectoryInformationNameOffset = Marshal
            .OffsetOf<WindowsPathRoot.FileFullDirectoryInformation>(
                nameof(WindowsPathRoot.FileFullDirectoryInformation.FileName)
            )
            .ToInt32();
    }

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true
        )]
        internal static safe extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile
        );

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern unsafe bool GetFileInformationByHandleEx(
            SafeFileHandle fileHandle,
            uint fileInformationClass,
            void* fileInformation,
            uint bufferSize
        );

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern unsafe bool SetFileInformationByHandle(
            SafeFileHandle fileHandle,
            uint fileInformationClass,
            void* fileInformation,
            uint bufferSize
        );

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern unsafe bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            void* inputBuffer,
            uint inputBufferSize,
            void* outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            nint overlapped
        );

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static safe extern bool SetFilePointerEx(
            SafeFileHandle file,
            long distance,
            out long newPosition,
            uint moveMethod
        );

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static safe extern nint GetCurrentThread();

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static safe extern bool ImpersonateSelf(int impersonationLevel);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static safe extern bool RevertToSelf();

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static safe extern bool OpenThreadToken(
            nint thread,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool openAsSelf,
            out SafeFileHandle token
        );

        [DllImport(
            "advapi32.dll",
            EntryPoint = "LookupPrivilegeValueW",
            ExactSpelling = true,
            CharSet = CharSet.Unicode,
            SetLastError = true
        )]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static safe extern bool LookupPrivilegeValue(
            string? systemName,
            string name,
            out Luid luid
        );

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe extern bool AdjustTokenPrivileges(
            SafeFileHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            TokenPrivileges* newState,
            uint bufferLength,
            TokenPrivileges* previousState,
            uint* returnLength
        );

        [DllImport("ntdll.dll", ExactSpelling = true)]
        internal static extern unsafe int NtCreateFile(
            out nint fileHandle,
            uint desiredAccess,
            ObjectAttributes* objectAttributes,
            out IoStatusBlock ioStatusBlock,
            long* allocationSize,
            uint fileAttributes,
            uint shareAccess,
            uint createDisposition,
            uint createOptions,
            void* eaBuffer,
            uint eaLength
        );

        [DllImport("ntdll.dll", ExactSpelling = true)]
        internal static extern unsafe int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            void* fileInformation,
            uint length,
            uint fileInformationClass
        );

        [DllImport("ntdll.dll", ExactSpelling = true)]
        internal static extern unsafe int NtQueryDirectoryFile(
            nint fileHandle,
            nint @event,
            nint apcRoutine,
            nint apcContext,
            out IoStatusBlock ioStatusBlock,
            void* fileInformation,
            uint length,
            uint fileInformationClass,
            byte returnSingleEntry,
            UnicodeString* fileName,
            byte restartScan
        );

        [DllImport("ntdll.dll", ExactSpelling = true)]
        internal static safe extern uint RtlNtStatusToDosError(int status);
    }
}
