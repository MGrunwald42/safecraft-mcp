using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SafeCraftMcp.Settings;

/// <summary>
/// Turns a user-supplied path into the one real local path it names, or refuses it. Path strings
/// have many spellings for the same place (<c>\??\C:\x</c>, <c>/\localhost\C$\x</c>, 8.3 short
/// names, junctions, symlinks), and a protected-folder check on the spelling misses them. So the
/// path is opened and Windows is asked for its final name (GetFinalPathNameByHandle), which
/// resolves every link and short name; only a plain <c>X:\...</c> result is accepted.
/// </summary>
public static partial class RealPath
{
    /// <summary>The real path of an existing file or folder, or null with the reason in <paramref name="error"/>.</summary>
    public static string? Resolve(string path, out string error)
    {
        error = "";
        if (!IsPlainLocal(path))
        {
            error = $"'{path}' isn't an absolute local path like C:\\Users\\me\\Pictures\\SafeCraft-out.";
            return null;
        }
        var full = Path.GetFullPath(path);
        if (!IsPlainLocal(full))
        {
            error = $"'{path}' isn't an absolute local path like C:\\Users\\me\\Pictures\\SafeCraft-out.";
            return null;
        }
        // FILE_FLAG_BACKUP_SEMANTICS opens folders too; no access rights are needed to ask for a name.
        using var handle = CreateFileW(full, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            error = $"'{full}' doesn't exist or can't be opened ({new Win32Exception(Marshal.GetLastPInvokeError()).Message}).";
            return null;
        }
        var final = FinalPath(handle);
        if (final is null)
        {
            error = $"'{full}': Windows didn't return its final path ({new Win32Exception(Marshal.GetLastPInvokeError()).Message}).";
            return null;
        }
        // The final name comes back as \\?\C:\... for a local volume and \\?\UNC\... for a share.
        if (!final.StartsWith(@"\\?\", StringComparison.Ordinal) || !IsPlainLocal(final[4..]))
        {
            error = $"'{path}' resolves to '{final}', which isn't on a local drive.";
            return null;
        }
        return Path.TrimEndingDirectorySeparator(final[4..]);
    }

    /// <summary>
    /// The real spelling of a path that may not exist (yet): the nearest existing ancestor is
    /// resolved and the rest appended. Used for the protected folders, which may sit behind a
    /// junction ("Documents\GitHub" → "D:\src") or not exist at all on this host.
    /// </summary>
    public static string ResolveAsFarAsExists(string path)
    {
        var rest = new Stack<string>();
        var current = Path.TrimEndingDirectorySeparator(path);
        while (Path.GetDirectoryName(current) is { } parent && !Path.Exists(current))
        {
            rest.Push(Path.GetFileName(current));
            current = parent;
        }
        var real = Resolve(current, out _) ?? current;
        return rest.Aggregate(real, Path.Combine);
    }

    /// <summary>
    /// The real path of an open file handle: where it leads after every link and short name,
    /// or null when it isn't a plain local path. Checking the handle rather than a path string
    /// means a file swapped for a link after the check can't redirect the read or write.
    /// </summary>
    public static string? OfHandle(SafeFileHandle handle)
    {
        var final = FinalPath(handle);
        return final is not null && final.StartsWith(@"\\?\", StringComparison.Ordinal) && IsPlainLocal(final[4..])
            ? final[4..]
            : null;
    }

    /// <summary>
    /// How many names the open file has. A hard link's final path is the name it was opened by,
    /// so only the count shows that a file in a workspace is also some other file.
    /// </summary>
    public static unsafe uint LinkCount(SafeFileHandle handle)
    {
        ByHandleFileInformation info;
        if (!GetFileInformationByHandle(handle, &info))
            throw new IOException($"Couldn't read the file's information ({new Win32Exception(Marshal.GetLastPInvokeError()).Message}).");
        return info.NumberOfLinks;
    }

    // BY_HANDLE_FILE_INFORMATION; its FILETIMEs are 4-byte aligned, hence Pack = 4.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandle(SafeFileHandle file, ByHandleFileInformation* information);

    /// <summary>A folder's real path, or null with the reason: <see cref="Resolve"/> plus "is a folder".</summary>
    public static string? ResolveFolder(string path, out string error)
    {
        var real = Resolve(path, out error);
        if (real is not null && !Directory.Exists(real))
        {
            error = $"'{real}' isn't a folder.";
            return null;
        }
        return real;
    }

    private static unsafe string? FinalPath(SafeFileHandle handle)
    {
        const int capacity = 32_768;
        var buffer = new char[capacity];
        fixed (char* p = buffer)
        {
            var length = GetFinalPathNameByHandleW(handle, p, capacity, 0);
            return length is 0 or >= capacity ? null : new string(p, 0, (int)length);
        }
    }

    /// <summary><c>X:\</c> followed by no further colon (no alternate data stream, no device).</summary>
    public static bool IsPlainLocal(string path) => PlainLocalPattern().IsMatch(path);

    [GeneratedRegex(@"\A[A-Za-z]:\\[^:]*\z")]
    private static partial Regex PlainLocalPattern();

    private const uint BackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);
}
