using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The thumbnail Windows itself shows for a file (what Explorer's large icons show: a frame of a video), through
/// IShellItemImageFactory. Only a real thumbnail counts: where Windows has none (no codec for the format) it would give the
/// icon of the file type, and this returns null instead (SIIGBF_THUMBNAILONLY).
/// </summary>
public static class ShellThumbnail
{
    private const int SIIGBF_BIGGERSIZEOK  = 0x01;
    private const int SIIGBF_THUMBNAILONLY = 0x08;

    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Width; public int Height; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>The thumbnail of a file, at most <paramref name="size"/> pixels on its longer side; null if Windows has none.</summary>
    public static BitmapSource? Get(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            if (!File.Exists(path)) return null;

            var id = ImageFactoryId;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out factory) != 0 || factory == null) return null;

            if (factory.GetImage(new SIZE { Width = size, Height = size }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out hBitmap) != 0
                || hBitmap == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }
}
