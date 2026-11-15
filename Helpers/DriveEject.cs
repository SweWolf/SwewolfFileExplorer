using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Eject of a CD / DVD drive ("Eject" in Windows Explorer's menu of the drive). The same steps as Explorer: open the volume, lock it (fails
/// when something has files open on it), dismount it, allow the media to be removed, eject. Done with the device calls and not the shell's
/// "Eject" verb, whose name depends on the language of Windows. Only for optical drives (<see cref="CanEject"/>); Format is not offered
/// on purpose.
///
/// History: 2026-11-15 first version (SwewolfFileExplorer).
/// </summary>
public static class DriveEject
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018, FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804, IoctlStorageEjectMedia = 0x002D4808;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    /// <summary>What <see cref="Eject"/> returns when another program has the disc open (the caller may ask, then call again with force).</summary>
    public const string InUse = "The disc is in use by a program.";

    /// <summary>True for the root of a CD / DVD drive.</summary>
    public static bool CanEject(string? root)
    {
        try { return !string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':' && new DriveInfo(root.Substring(0, 1)).DriveType == DriveType.CDRom; }
        catch { return false; }
    }

    /// <summary>Ejects the disc. Returns null when it worked, else a sentence saying why not (e.g. the disc is in use).</summary>
    public static string? Eject(string root, bool force = false)
    {
        char letter = char.ToUpperInvariant(root[0]);
        string device = "\\\\.\\" + letter + ":";   // \\.\E:
        using var h = CreateFile(device, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            // Some drives refuse write access to the volume handle: a read-only handle can still eject.
            using var h2 = CreateFile(device, GenericRead,ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h2.IsInvalid) return $"Drive {letter}: could not be opened: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return DoEject(h2, lockFirst: false, force, letter);
        }
        return DoEject(h, lockFirst: true, force, letter);
    }

    private static string? DoEject(SafeFileHandle h, bool lockFirst, bool force, char letter)
    {
        if (lockFirst)
        {
            // A lock fails while a program has a file open on the disc (Explorer says the disc is in use).
            bool locked = false;
            for (int i = 0; i < 4 && !locked; i++)
            {
                locked = DeviceIoControl(h, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                if (!locked) Thread.Sleep(300);
            }
            if (!locked && !force) return InUse;
            if (locked) DeviceIoControl(h, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }

        // PREVENT_MEDIA_REMOVAL { bool PreventMediaRemoval } = false
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 0);
            DeviceIoControl(h, IoctlStorageMediaRemoval, buffer, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(buffer); }

        if (!DeviceIoControl(h, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            return $"Drive {letter}: could not eject the disc: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        return null;
    }
}
