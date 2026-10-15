using System.Runtime.InteropServices;

namespace SweWolfFileExplorer.Helpers;

/// <summary>
/// Shows the standard Windows "How do you want to open this file?" dialog (Explorer's Open with).
/// Process.Start with the "openas" verb does not work for many file types (for example .jpg:
/// "No application is associated with the specified file"), so the shell function is called directly.
/// </summary>
public static class OpenWithHelper
{
    public static void Show(string path, IntPtr owner)
    {
        var info = new OPENASINFO
        {
            pcszFile      = System.IO.Path.GetFullPath(path),
            pcszClass     = null,
            oaifInFlags   = OAIF_ALLOW_REGISTRATION | OAIF_EXEC   // let the dialog run the chosen program
        };

        int hr = SHOpenWithDialog(owner, ref info);
        // The user pressing Cancel is not an error (HRESULT_FROM_WIN32(ERROR_CANCELLED)).
        if (hr != 0 && hr != unchecked((int)0x800704C7))
            Marshal.ThrowExceptionForHR(hr);
    }

    private const uint OAIF_ALLOW_REGISTRATION = 0x00000001;
    private const uint OAIF_EXEC               = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string  pcszFile;
        public string? pcszClass;
        public uint    oaifInFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);
}
