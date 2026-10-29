using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The "Edit" command of Windows Explorer's menu (right under Open): the file type's own "edit" verb, as .bat / .cmd (Notepad),
/// pictures (Paint) and others have it. Shown only for a type that has one.
/// </summary>
public static class EditVerb
{
    /// <summary>Does Windows know an "edit" verb for this file name? (the type's own, its extension's, or its perceived type's: image, text ...)</summary>
    public static bool Exists(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) return false;

        try
        {
            using var extKey = Registry.ClassesRoot.OpenSubKey(ext);
            if (extKey == null) return false;

            if (HasEdit($@"SystemFileAssociations\{ext}")) return true;

            // The program type the extension points to (batfile, jpegfile ...), also from the user's own choice.
            using var choice = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice");
            var progId = choice?.GetValue("ProgId") as string ?? extKey.GetValue(null) as string;
            if (!string.IsNullOrEmpty(progId) && HasEdit(progId)) return true;

            var perceived = extKey.GetValue("PerceivedType") as string;
            return !string.IsNullOrEmpty(perceived) && HasEdit($@"SystemFileAssociations\{perceived}");
        }
        catch
        {
            return false;
        }
    }

    private static bool HasEdit(string classKey)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{classKey}\shell\edit");
        return key != null;
    }

    /// <summary>Starts the editor of the file's type, in the file's own folder. False if the user said no to an administrator question.</summary>
    public static bool Run(string path)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = path, Verb = "edit", UseShellExecute = true };
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) psi.WorkingDirectory = folder;
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }
}
