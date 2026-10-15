using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// Reads the archives .NET cannot (RAR, 7z) with 7-Zip's command line program, <c>7z.exe</c>, as TeraTool did.
/// 7-Zip must be installed. The archive is read in place: <c>7z l</c> lists it, <c>7z e -so</c> writes one file to the output.
/// </summary>
internal static class SevenZipRunner
{
    private static string? _exe;
    private static bool _searched;

    /// <summary>Where 7z.exe is: the folder 7-Zip says it is installed in, the usual places, then the PATH. Null if not found.</summary>
    public static string? Find()
    {
        if (_searched) return _exe;
        _searched = true;

        var folders = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\7-Zip");
                    foreach (var name in new[] { "Path64", "Path" })
                        if (key?.GetValue(name) is string path && path.Length > 0) folders.Add(path);
                }
                catch
                {
                    // no access to that key
                }
            }
        }

        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip"));
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip"));
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), "7z.exe");
                if (File.Exists(candidate)) return _exe = candidate;
            }
            catch
            {
                // a bad entry in the PATH
            }
        }

        return null;
    }

    private static string Require() =>
        Find() ?? throw new ArchiveException(
            "7-Zip is needed to open this kind of archive (RAR, 7z), and it was not found. Install it from www.7-zip.org.");

    private static ProcessStartInfo Create(params string[] arguments)
    {
        var info = new ProcessStartInfo(Require())
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,    // closed at once: 7-Zip must never wait for a password that nobody can type
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };
        // A password that is surely wrong: an archive that needs one then fails with "Wrong password" at once.
        // Without a -p switch 7-Zip asks for it, and gets nothing (its input is closed): "Break signaled", which says nothing.
        info.ArgumentList.Add("-pSwewolfFileExplorer-no-password");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    /// <summary>Every entry of the archive, in the order stored in it.</summary>
    public static List<RawEntry> List(string archiveFile)
    {
        // l = list, -slt = one "name = value" line per property, -ba = no headings, -sccUTF-8 = names in UTF-8
        var info = Create("l", "-slt", "-ba", "-sccUTF-8", "--", archiveFile);

        using var process = Process.Start(info) ?? throw new ArchiveException("7-Zip could not be started.");
        process.StandardInput.Close();

        var errors  = process.StandardError.ReadToEndAsync();   // read at the same time, or a full pipe stops 7-Zip
        var entries = Parse(process.StandardOutput, File.GetLastWriteTime(archiveFile));
        process.WaitForExit();

        // Exit code 1 is only a warning (for example a damaged file in the archive): what was listed is shown.
        if (process.ExitCode >= 2 || (entries.Count == 0 && process.ExitCode != 0))
            throw Failure(archiveFile, errors.Result);

        return entries;
    }

    private static List<RawEntry> Parse(TextReader output, DateTime fallbackDate)
    {
        var entries = new List<RawEntry>();
        var fields  = new Dictionary<string, string>(StringComparer.Ordinal);

        void Flush()
        {
            if (fields.TryGetValue("Path", out var path) && path.Length > 0 && Get("Alternate Stream") != "+")   // not the hidden NTFS streams
            {
                bool isFolder = Get("Folder") == "+" || Get("Attributes").Contains('D');
                long.TryParse(Get("Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size);
                var modified = DateTime.TryParse(Get("Modified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)
                    ? date
                    : fallbackDate;   // a RAR does not have to store a date

                entries.Add(new RawEntry("", path, isFolder, isFolder ? 0 : size, modified, Get("Encrypted") == "+", path));
            }
            fields.Clear();
        }

        string Get(string name) => fields.GetValueOrDefault(name, "");

        string? line;
        while ((line = output.ReadLine()) != null)
        {
            if (line.Length == 0) { Flush(); continue; }   // an empty line ends the entry

            int split = line.IndexOf(" = ", StringComparison.Ordinal);
            if (split > 0) fields[line[..split]] = line[(split + 3)..];
            else if (line.EndsWith(" =", StringComparison.Ordinal)) fields[line[..^2]] = "";
        }
        Flush();

        return entries;
    }

    // ── Reading one file ─────────────────────────────────────────────────────

    /// <summary>Writes one file of the archive to <paramref name="destination"/>. <paramref name="storedName"/> is its path as stored in the archive.</summary>
    public static long Extract(string archiveFile, string storedName, Stream destination)
    {
        // e = extract, -so = to the standard output, -spd = the name is not a wildcard, -y = yes to all questions
        var info = Create("e", "-so", "-spd", "-y", "--", archiveFile, storedName);

        using var process = Process.Start(info) ?? throw new ArchiveException("7-Zip could not be started.");
        process.StandardInput.Close();

        var errors = process.StandardError.ReadToEndAsync();

        long total = 0;
        var buffer = new byte[81920];
        var source = process.StandardOutput.BaseStream;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            total += read;
        }

        process.WaitForExit();
        if (process.ExitCode >= 2) throw Failure(archiveFile, errors.Result);

        return total;
    }

    // ── Errors ───────────────────────────────────────────────────────────────

    private static ArchiveException Failure(string archiveFile, string errors)
    {
        string name = Path.GetFileName(archiveFile);

        if (errors.Contains("password", StringComparison.OrdinalIgnoreCase) || errors.Contains("Break signaled", StringComparison.OrdinalIgnoreCase))
            return new ArchiveException($"'{name}' is protected with a password. Archives with a password are not supported yet.");

        // The first line that says what is wrong ("ERROR: ...", "Can not open the file as archive").
        string reason = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) ?? "";

        return new ArchiveException(reason.Length > 0
            ? $"'{name}' cannot be read: {reason.Replace("ERROR: ", "")}"
            : $"'{name}' is not a valid archive, or it is damaged.");
    }
}
