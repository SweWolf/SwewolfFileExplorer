using System.IO;
using System.Runtime.InteropServices;

namespace SwewolfFileExplorer.Helpers;

/// <summary>What the tags of a music file say.</summary>
public record AudioTags(string Artist, string Title, uint TrackNumber);

/// <summary>
/// Reads the properties of a file (the tags of a music file) through the Windows property system,
/// the same way as Explorer's columns, so every format Windows has a handler for works (mp3, m4a, flac, wma, ...)
/// and no library is needed. Only for files on the disk. Safe to call from a background thread.
/// </summary>
public static class ShellProperties
{
    /// <summary>The file types whose tags are looked at (the others have none worth a column).</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".wma", ".ogg", ".oga", ".opus", ".aac", ".wav", ".aif", ".aiff", ".wv", ".mka"
    };

    public static bool HasTags(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    // System.Music.Artist, System.Title, System.Music.TrackNumber
    private static readonly PROPERTYKEY ArtistKey = new(new Guid("56A3372E-CE9C-11D2-9F0E-006097C686F6"), 2);
    private static readonly PROPERTYKEY TitleKey  = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
    private static readonly PROPERTYKEY TrackKey  = new(new Guid("56A3372E-CE9C-11D2-9F0E-006097C686F6"), 7);

    /// <summary>The artist, title and track number of a music file; null if it has none or cannot be read.</summary>
    public static AudioTags? ReadAudioTags(string path)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_DEFAULT, ref iid, out store);

            var artist = GetText(store, ArtistKey);
            var title  = GetText(store, TitleKey);
            var track  = GetNumber(store, TrackKey);

            return artist.Length == 0 && title.Length == 0 && track == 0
                ? null
                : new AudioTags(artist, title, track);
        }
        catch
        {
            return null;   // no handler, a file that is gone or locked: the cells stay empty
        }
        finally
        {
            if (store != null) Marshal.ReleaseComObject(store);
        }
    }

    private static string GetText(IPropertyStore store, PROPERTYKEY key)
    {
        var pv = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref key, out pv) != 0 || pv.vt == VT_EMPTY) return "";

            // Artist is a list of names (VT_VECTOR | VT_LPWSTR): join them.
            if (pv.vt == (VT_VECTOR | VT_LPWSTR))
            {
                var names = new List<string>();
                for (int i = 0; i < (int)(uint)pv.data1; i++)
                {
                    var p = Marshal.ReadIntPtr(pv.data2, i * IntPtr.Size);
                    var name = Marshal.PtrToStringUni(p);
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                }
                return string.Join("; ", names);
            }

            if (PropVariantToStringAlloc(ref pv, out var text) != 0) return "";
            try { return (Marshal.PtrToStringUni(text) ?? "").Trim(); }
            finally { Marshal.FreeCoTaskMem(text); }
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    private static uint GetNumber(IPropertyStore store, PROPERTYKEY key)
    {
        var pv = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref key, out pv) != 0 || pv.vt == VT_EMPTY) return 0;
            return PropVariantToUInt32WithDefault(ref pv, 0);
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    private const int GPS_DEFAULT = 0;
    private const ushort VT_EMPTY = 0;
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_VECTOR = 0x1000;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    // 24 bytes on x64: the type, three unused words, then the value (a vector: the count and the pointer).
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr data1;
        public IntPtr data2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(
        string path, IntPtr bindContext, int flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PropVariantToStringAlloc(ref PROPVARIANT pv, out IntPtr text);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern uint PropVariantToUInt32WithDefault(ref PROPVARIANT pv, uint defaultValue);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pv);
}
