# StaticCs.PathRoot

`PathRoot` provides filesystem operations rooted at an open directory handle.
Paths passed to a `PathRoot` are relative to that directory and cannot escape it
through `..` components or symbolic links.

```csharp
using StaticCs;

using PathRoot root = PathRoot.Open("/srv/work");
string logs = Path.Combine("output", "logs");
using PathRoot logRoot = root.CreateSubRoot(logs);

using FileStream stream = logRoot.CreateFile("build.log");
```

Use `OpenSubRoot` to derive a root confined to a subdirectory:

```csharp
using PathRoot output = root.OpenSubRoot("output");
using FileStream stream = output.CreateFile("result.txt");
```

The API uses file- and directory-specific .NET-style names, including
`CreateDirectory`, `CreateSubRoot`, `CreateFileSymbolicLink`,
`CreateDirectorySymbolicLink`, `MoveFile`, `MoveDirectory`, `DeleteFile`,
`DeleteDirectory`, and `GetSymbolicLinkTarget`.

The root remains attached to the directory that was opened even if that
directory is subsequently renamed. Operations may traverse mount points and
may open device or other special files. On Windows, unknown reparse-point types
are rejected, and creating a symbolic link may require
`SeCreateSymbolicLinkPrivilege`.

Confinement remains enforced while other processes modify the tree. As with
the corresponding `File` and `Directory` APIs, file-versus-directory errors are
not guaranteed if another process replaces the final entry during an operation.
