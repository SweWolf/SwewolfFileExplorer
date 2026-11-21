using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Mounts a disc image (.iso) as a read-only virtual DVD drive with a drive letter of its own, and ejects it again. Done with Windows' own
/// virtual disk functions (virtdisk.dll: OpenVirtualDisk / AttachVirtualDisk / DetachVirtualDisk), which is what Explorer's "Mount" does, so it
/// works whatever program has taken over the .iso file type. The image stays mounted after this program ends (permanent lifetime), as in
/// Explorer; <see cref="Eject"/> or Explorer's Eject takes it away. No administrator rights are needed for an ISO.
///
/// History: 2026-11-20 first version (SwewolfFileExplorer).
/// </summary>
public static class DiscImageMount
{
    private const uint DeviceIso = 1;
    private const uint AccessAttachRo = 0x00010000, AccessDetach = 0x00040000, AccessGetInfo = 0x00080000;
    private const uint AttachReadOnly = 1, AttachPermanentLifetime = 4;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageType { public uint DeviceId; public Guid VendorId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber { public uint DeviceType; public uint DeviceNumber; public uint PartitionNumber; }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int OpenVirtualDisk(ref StorageType type, string path, uint access, uint flags, IntPtr parameters, out IntPtr handle);

    [DllImport("virtdisk.dll")]
    private static extern int AttachVirtualDisk(IntPtr handle, IntPtr securityDescriptor, uint flags, uint providerFlags, IntPtr parameters, IntPtr overlapped);

    [DllImport("virtdisk.dll")]
    private static extern int DetachVirtualDisk(IntPtr handle, uint flags, uint providerFlags);

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int GetVirtualDiskPhysicalPath(IntPtr handle, ref int size, StringBuilder path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuffer, int inSize, out StorageDeviceNumber outBuffer, int outSize, out int returned, IntPtr overlapped);

    private static int Open(string isoPath, uint access, out IntPtr handle)
    {
        var type = new StorageType { DeviceId = DeviceIso, VendorId = VendorMicrosoft };
        return OpenVirtualDisk(ref type, isoPath, access, 0, IntPtr.Zero, out handle);
    }

    /// <summary>The device of the mounted image (<c>\\.\CDROM3</c>), or null when the image is not mounted.</summary>
    private static string? PhysicalPath(string isoPath)
    {
        if (Open(isoPath, AccessGetInfo, out var handle) != 0) return null;
        try
        {
            int size = 520;   // bytes; room for a device path
            var path = new StringBuilder(size / 2);
            return GetVirtualDiskPhysicalPath(handle, ref size, path) == 0 ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>True when the image is mounted now (by this program, by Explorer or by another one).</summary>
    public static bool IsMounted(string isoPath)
    {
        try { return PhysicalPath(isoPath) != null; }
        catch { return false; }
    }

    /// <summary>The drive root (<c>E:\</c>) of the mounted image, or null when it is not mounted or has no letter (yet).</summary>
    public static string? DriveRoot(string isoPath)
    {
        try
        {
            var physical = PhysicalPath(isoPath);
            if (physical == null) return null;
            int digits = physical.Length;
            while (digits > 0 && char.IsDigit(physical[digits - 1])) digits--;
            if (digits == physical.Length || !uint.TryParse(physical[digits..], out uint number)) return null;

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.CDRom) continue;
                using var h = CreateFile("\\\\.\\" + drive.Name[0] + ":", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                if (DeviceIoControl(h, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, out var device, Marshal.SizeOf<StorageDeviceNumber>(), out _, IntPtr.Zero)
                    && device.DeviceType == 2 /* CD-ROM */ && device.DeviceNumber == number)
                    return drive.RootDirectory.FullName;
            }
        }
        catch { /* no letter found */ }
        return null;
    }

    /// <summary>
    /// Mounts the image. Returns the drive root, or null with <paramref name="error"/> set (it may also return null with no error when the
    /// image is mounted but Windows has not given it a letter within a few seconds).
    /// </summary>
    public static string? Mount(string isoPath, out string? error)
    {
        error = null;
        string? existing = DriveRoot(isoPath);
        if (existing != null) return existing;   // already mounted

        int result = Open(isoPath, AccessAttachRo | AccessGetInfo | AccessDetach, out var handle);
        if (result != 0) { error = Describe(result); return null; }
        try
        {
            result = AttachVirtualDisk(handle, IntPtr.Zero, AttachReadOnly | AttachPermanentLifetime, 0, IntPtr.Zero, IntPtr.Zero);
            if (result != 0) { error = Describe(result); return null; }
        }
        finally { CloseHandle(handle); }

        for (int i = 0; i < 40; i++)   // the letter appears a moment after the attach
        {
            var root = DriveRoot(isoPath);
            if (root != null) return root;
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>Ejects the mounted image. Returns null when it worked (or it was not mounted), else a sentence saying why not.</summary>
    public static string? Eject(string isoPath)
    {
        // The drive is ejected the way Explorer does it first: that refuses while a program has files open on it.
        string? root = DriveRoot(isoPath);
        if (root != null)
        {
            var problem = DriveEject.Eject(root);
            if (problem == DriveEject.InUse) return "The disc image is in use by a program. Close what uses it and try again.";
        }

        if (!IsMounted(isoPath)) return null;   // the eject already took it away

        int result = Open(isoPath, AccessDetach | AccessGetInfo, out var handle);
        if (result != 0) return Describe(result);
        try
        {
            result = DetachVirtualDisk(handle, 0, 0);
            return result == 0 ? null : Describe(result);
        }
        finally { CloseHandle(handle); }
    }

    private static string Describe(int error) => new Win32Exception(error).Message;
}
