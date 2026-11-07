using System.IO;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The program that opens a text file for reading: the one in settings.json (<see cref="AppSettings.TextEditorPath"/>), else Visual Studio Code,
/// else Windows Notepad (the same choice as for .nfo files, <see cref="NfoOpener.FindTextEditor"/>). One editor for every kind of script: the
/// "edit" verb of each extension differs from one computer to the next (Notepad for .bat, IDLE for .py, the PowerShell ISE for .ps1).
/// </summary>
public static class TextEditor
{
    public sealed record Editor(string Path, string Name, System.Windows.Media.ImageSource? Icon);

    private static Editor? _found;
    private static bool _looked;

    /// <summary>The editor and its name for a button ("Visual Studio Code", "Notepad"), or null if none can be found. Looked up once per run.</summary>
    public static Editor? Find()
    {
        if (_looked) return _found;
        _looked = true;
        try
        {
            var path = NfoOpener.FindTextEditor();
            if (path == null)
            {
                var notepad = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
                if (File.Exists(notepad)) path = notepad;
            }
            if (path != null)
                _found = new Editor(path, ExeInfo.FriendlyName(path) ?? System.IO.Path.GetFileNameWithoutExtension(path), IconHelper.GetIcon(path));   // the 16 px icon of the editor, for the button
        }
        catch { /* no editor: the button is not shown */ }
        return _found;
    }
}
