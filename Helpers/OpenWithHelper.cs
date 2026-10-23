using System.Runtime.InteropServices;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Shows the standard Windows "How do you want to open this file?" dialog (Explorer's Open with).
/// Process.Start with the "openas" verb does not work for many file types (for example .jpg:
/// "No application is associated with the specified file"), so the verb is invoked through Shell.Application
/// (what Explorer does; it has the "Always" button), and the shell function is the fallback.
/// </summary>
public static class OpenWithHelper
{
    public static void Show(string path, IntPtr owner)
    {
        // First the shell's own "openas" verb, the way Explorer opens the dialog: Windows 11 gives that dialog an "Always"
        // button, which SHOpenWithDialog below never gets (found by the user 2026-10-23; Process.Start with the verb
        // fails for many types, but the verb through Shell.Application works). The dialog is Windows' own process and does
        // not wait for an answer, so it has no owner window.
        if (TryShellVerb(path)) return;

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

    /// <summary>Invokes the "openas" verb of the file through Shell.Application (needs an STA thread: the UI thread is one). False if that did not work.</summary>
    private static bool TryShellVerb(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return false;

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic? item = shell.Namespace(System.IO.Path.GetDirectoryName(full))?.ParseName(System.IO.Path.GetFileName(full));
            if (item == null) return false;

            item.InvokeVerb("openas");
            return true;
        }
        catch
        {
            return false;
        }
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
