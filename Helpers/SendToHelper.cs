using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media;

namespace SwewolfFileExplorer.Helpers;

/// <summary>One item of the Send To menu: a file in the SendTo folder, or a subfolder with children.</summary>
public class SendToEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public List<SendToEntry>? Children { get; init; }
}

/// <summary>
/// The Send To menu, built the way Windows Explorer does it: the items are the files in the
/// user's SendTo folder, and sending means dropping the selected files on the item's shell
/// drop target. That is why shortcuts, "Compressed (zipped) Folder", "Mail Recipient" and
/// "Desktop (create shortcut)" all work, and not only plain shortcuts.
/// Must be called on an STA thread (the WPF UI thread).
/// </summary>
public static class SendToHelper
{
    // ── The menu ─────────────────────────────────────────────────────────────

    public static List<SendToEntry> GetEntries() =>
        ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.SendTo));

    // The shell takes seconds to find the icons of the SendTo shortcuts the first time (it reads each target program from the disk).
    // They are frozen bitmaps, so a background thread can fetch them once (MainWindow does that when the window is shown) and the menu,
    // which still reads the folder each time, takes them from here. An icon is looked up again when its shortcut file changes.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, DateTime Written), ImageSource?> IconCache = new();

    private static ImageSource? IconOf(string path)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); } catch { return IconHelper.GetIcon(path); }
        return IconCache.GetOrAdd((path, written), _ => IconHelper.GetIcon(path));
    }

    private static List<SendToEntry> ReadFolder(string folder)
    {
        var result = new List<SendToEntry>();
        if (!Directory.Exists(folder)) return result;

        // Subfolders become submenus; both come sorted by name, like in Explorer.
        foreach (var path in Directory.EnumerateFileSystemEntries(folder)
                     .OrderBy(p => Directory.Exists(p) ? 0 : 1)
                     .ThenBy(p => System.IO.Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                bool isDir = (attrs & FileAttributes.Directory) != 0;
                if (isDir)
                {
                    var children = ReadFolder(path);
                    if (children.Count == 0) continue;
                    result.Add(new SendToEntry
                    {
                        Name     = System.IO.Path.GetFileName(path),
                        Path     = path,
                        Icon     = IconOf(path),
                        Children = children
                    });
                }
                else
                {
                    result.Add(new SendToEntry
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        Path = path,
                        Icon = IconOf(path)
                    });
                }
            }
            catch { /* unreadable entry: leave it out */ }
        }

        return result;
    }

    // ── Sending ──────────────────────────────────────────────────────────────

    /// <summary>Drops <paramref name="files"/> on the Send To item <paramref name="targetPath"/>.</summary>
    public static void SendTo(string targetPath, IReadOnlyCollection<string> files)
    {
        if (files.Count == 0) return;

        object? targetObj = null;
        IntPtr  dropPtr   = IntPtr.Zero;

        try
        {
            var dataObject = CreateDataObject(files);

            // The drop target of the Send To item.
            var iidShellItem = IID_IShellItem;
            Check(SHCreateItemFromParsingName(System.IO.Path.GetFullPath(targetPath), IntPtr.Zero, ref iidShellItem, out targetObj));
            var targetItem = (IShellItem)targetObj;
            var bhidUiObject = BHID_SFUIObject;
            var iidDropTarget = IID_IDropTarget;
            Check(targetItem.BindToHandler(IntPtr.Zero, ref bhidUiObject, ref iidDropTarget, out dropPtr));
            var dropTarget = (IDropTarget)Marshal.GetObjectForIUnknown(dropPtr);

            // Copy (or link): never move, as Send To in Explorer.
            var pt = new POINTL();
            int effect = DROPEFFECT_COPY | DROPEFFECT_LINK;
            Check(dropTarget.DragEnter(dataObject, MK_LBUTTON, pt, ref effect));
            if (effect == DROPEFFECT_NONE)
            {
                dropTarget.DragLeave();
                throw new InvalidOperationException("The Send To item does not accept these files.");
            }

            Check(dropTarget.Drop(dataObject, MK_LBUTTON, pt, ref effect));
        }
        finally
        {
            if (dropPtr != IntPtr.Zero) Marshal.Release(dropPtr);
            if (targetObj != null) Marshal.ReleaseComObject(targetObj);
        }
    }

    /// <summary>
    /// The files as a shell data object (CF_HDROP and friends). Also used by
    /// <see cref="PropertiesHelper"/> for the properties of several items.
    /// </summary>
    internal static IDataObject CreateDataObject(IReadOnlyCollection<string> files)
    {
        var pidls = new List<IntPtr>();
        object? arrayObj = null;
        IntPtr  dataPtr  = IntPtr.Zero;

        try
        {
            foreach (var file in files)
            {
                Check(SHParseDisplayName(System.IO.Path.GetFullPath(file), IntPtr.Zero, out var pidl, 0, out _));
                pidls.Add(pidl);
            }

            Check(SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out arrayObj));
            var array = (IShellItemArray)arrayObj;
            var bhidDataObject = BHID_DataObject;
            var iidDataObject  = IID_IDataObject;
            Check(array.BindToHandler(IntPtr.Zero, ref bhidDataObject, ref iidDataObject, out dataPtr));
            return (IDataObject)Marshal.GetObjectForIUnknown(dataPtr);
        }
        finally
        {
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
            if (dataPtr != IntPtr.Zero) Marshal.Release(dataPtr);
            if (arrayObj != null) Marshal.ReleaseComObject(arrayObj);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    // ── Shell interop ────────────────────────────────────────────────────────

    // Left button down, no other keys: a plain left-button drop. With 0 the shell shows
    // an "Open with / Cancel" menu for shortcuts to programs.
    private const int MK_LBUTTON = 1;

    private const int DROPEFFECT_NONE = 0;
    private const int DROPEFFECT_COPY = 1;
    private const int DROPEFFECT_LINK = 4;

    private static readonly Guid BHID_DataObject  = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");
    private static readonly Guid BHID_SFUIObject  = new("3981E225-F559-11D3-8E3A-00C04F6837D5");
    private static readonly Guid IID_IDataObject  = new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDropTarget  = new("00000122-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellItem   = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL { public int x; public int y; }

    // Only the first method of each shell interface is declared: that is all we call,
    // and the vtable order stays correct.
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter(IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig] int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext,
        out IntPtr pidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint cidl, IntPtr[] rgpidl,
        [MarshalAs(UnmanagedType.Interface)] out object ppsiItemArray);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
}
