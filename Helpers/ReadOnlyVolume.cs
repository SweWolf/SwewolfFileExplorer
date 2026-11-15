using System.IO;
using System.Runtime.InteropServices;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Is a path on a read-only volume (an audio CD, a CD-ROM or DVD-ROM, a finished data disc)? Windows says so with the volume flag
/// FILE_READ_ONLY_VOLUME, which GetVolumeInformation returns: it is set for CDFS and read-only UDF, and not for a writable disc
/// (a rewritable disc formatted for direct writing) or for a hard disk. The right-click menus hide what writes (New Folder, Paste ...).
/// Network paths and network drives are not asked: a share that only lets the user read looks writable to this flag (the permission is
/// checked per folder, when something is written), so it would only show a wrong answer and could stall.
///
/// History: 2026-11-15 first version (SwewolfFileExplorer).
/// </summary>
public static class ReadOnlyVolume
{
    private const uint FileReadOnlyVolume = 0x00080000;

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, IntPtr name, int nameSize, IntPtr serial, IntPtr maxComponent,
        out uint flags, IntPtr fileSystem, int fileSystemSize);

    public static bool Is(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length < 3) return false;
            if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown) return false;
            return GetVolumeInformation(root, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out uint flags, IntPtr.Zero, 0)
                   && (flags & FileReadOnlyVolume) != 0;
        }
        catch { return false; }
    }
}
