using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The web browsers installed on this computer (the ones that register themselves under <c>Clients\StartMenuInternet</c>, as Windows'
/// "Default apps" list does) and a way to open a web address in one of them, or in the default one.
/// </summary>
internal static class WebBrowsers
{
    internal sealed record Browser(string Name, string ExePath);

    /// <summary>Only http and https addresses are ever opened (never a file or another kind of link found in a file).</summary>
    public static bool IsWebAddress(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The installed browsers by name, each once.</summary>
    public static List<Browser> Installed()
    {
        var found = new Dictionary<string, Browser>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                    if (root == null) continue;

                    foreach (var name in root.GetSubKeyNames())
                    {
                        using var key = root.OpenSubKey(name);
                        using var command = key?.OpenSubKey(@"shell\open\command");
                        var exe = ExeOf(command?.GetValue(null) as string);
                        if (exe == null || !File.Exists(exe) || found.ContainsKey(exe)) continue;

                        var title = key?.GetValue(null) as string;
                        found[exe] = new Browser(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(exe) : title.Trim(), exe);
                    }
                }
                catch
                {
                    // no access to that part of the registry
                }
            }
        }

        return found.Values.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The program in a registry command line: "C:\x\a.exe" --flag, or C:\x\a.exe --flag.</summary>
    private static string? ExeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command;
    }

    /// <summary>Opens the address in the browser Windows has as the default one.</summary>
    public static void OpenInDefault(string url) =>
        Process.Start(new ProcessStartInfo(url.Trim()) { UseShellExecute = true });

    /// <summary>Opens the address in the given browser.</summary>
    public static void Open(Browser browser, string url)
    {
        var info = new ProcessStartInfo(browser.ExePath) { UseShellExecute = false };
        info.ArgumentList.Add(url.Trim());
        Process.Start(info);
    }
}
