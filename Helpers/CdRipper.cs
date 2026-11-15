// CdRipper.cs: rips the tracks of an audio CD to MP3, FLAC or M4A files (the Rip button in the toolbar; plan: D:\Dev\dev-notes\FileExplorer-audio-cd-ripping-plan.md).
//
// How: Windows has no ripping feature a program can call, and the .cda files on a disc are 44-byte stubs. So the audio is read from the drive with
// Windows' own raw read (DeviceIoControl IOCTL_CDROM_RAW_READ on \\.\E:, CD-DA mode: 2352-byte sectors, 16-bit 44.1 kHz stereo PCM; no driver or
// package is needed) and encoded by ffmpeg (ffmpeg.exe is found as for the preview frames: FfmpegFrame.Find). Where a track begins and how long it is
// comes from its stub (the same numbers CdDiscInfo uses for the MusicBrainz disc id). Artist, title, album and cover come from CdDiscInfo's kept
// information (MusicBrainz) and are written into each file with TagLibSharp.
//
// A track is read into a temporary PCM file first (%TEMP%, deleted afterwards), then encoded: that is what makes the check possible.
//   - Every sector that the drive cannot read at once is read again (up to 4 more times, one by one); one that never reads is filled with silence
//     and counted ("unreadable").
//   - Verify (optional): the track is read a second time, starting half a track further on so that the drive's own cache cannot answer, and
//     compared with the first read. Where the two differ the place is read again until two reads agree (up to 8 more times); if none agree it is
//     "unresolved" and the first read is kept. This finds damage that the drive does not report (it corrects errors by guessing).
// A track is "damaged" when sectors were unreadable or places stayed unresolved (it may click or skip); retries that ended well are only mentioned.
// The report says where in the track a problem was (seconds). Not done: AccurateRip, the drive's C2 error flags, drive read offsets, a time-out
// for one read that hangs on a very bad disc (Cancel is looked at between reads).
//
// Files: written under a hidden temporary name in the output folder, tagged, then renamed to "Artist - Title.ext" (a taken name gets " (1)", " (2)"
// ...: nothing is ever replaced). Cancel removes the file in progress; finished files stay.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwewolfFileExplorer.Helpers;

public static class CdRipper
{
    public const int SectorBytes = 2352;
    private const int ChunkSectors = 24;    // 24 x 2352 = 56 KB: below the limit of one raw read (about 27 sectors)
    private const int SectorRetries = 4;
    private const int ReReads = 8;

    // ── Formats and qualities ────────────────────────────────────────────

    /// <summary>One quality choice of a format: the text in the box and the ffmpeg options (the codec included).</summary>
    public sealed record Quality(string Label, string[] Args);

    public sealed record RipFormat(string Key, string Name, string Extension, Quality[] Qualities);

    public static readonly RipFormat[] Formats =
    {
        new("mp3", "MP3", ".mp3", new Quality[]
        {
            new("Best (Variable, About 245 kbps)",  new[] { "-c:a", "libmp3lame", "-q:a", "0" }),
            new("High (Variable, About 190 kbps)",  new[] { "-c:a", "libmp3lame", "-q:a", "2" }),
            new("320 kbps",                         new[] { "-c:a", "libmp3lame", "-b:a", "320k" }),
            new("192 kbps",                         new[] { "-c:a", "libmp3lame", "-b:a", "192k" }),
            new("128 kbps",                         new[] { "-c:a", "libmp3lame", "-b:a", "128k" }),
        }),
        new("flac", "FLAC (Lossless)", ".flac", new Quality[]
        {
            new("Standard",                         new[] { "-c:a", "flac", "-compression_level", "5" }),
            new("Smallest Files (Slower)",          new[] { "-c:a", "flac", "-compression_level", "8" }),
            new("Fastest",                          new[] { "-c:a", "flac", "-compression_level", "0" }),
        }),
        new("m4a", "M4A (AAC)", ".m4a", new Quality[]
        {
            new("256 kbps",                         new[] { "-c:a", "aac", "-b:a", "256k" }),
            new("192 kbps",                         new[] { "-c:a", "aac", "-b:a", "192k" }),
            new("128 kbps",                         new[] { "-c:a", "aac", "-b:a", "128k" }),
        }),
    };

    // ── What to rip, what came of it ─────────────────────────────────────

    /// <summary>One track to rip: where it lies on the disc, the name of its file and what goes into its tags.</summary>
    public sealed class TrackJob
    {
        public int Number { get; init; }
        public int Start { get; init; }          // first sector (as in the stub: without the 150 sectors of lead-in)
        public int Length { get; init; }         // in sectors, 75 to a second
        public string FileName { get; init; } = "";   // without the extension
        public string Title { get; init; } = "";
        public string? Artist { get; init; }
        public string? Album { get; init; }
        public string? AlbumArtist { get; init; }
        public int TrackCount { get; init; }
    }

    public sealed class Options
    {
        public required string Root { get; init; }            // "E:\"
        public required RipFormat Format { get; init; }
        public required int QualityIndex { get; init; }
        public required bool Verify { get; init; }
        public required string OutputFolder { get; init; }
        public byte[]? Cover { get; init; }
    }

    public sealed class TrackReport
    {
        public int Number { get; init; }
        public string Name { get; init; } = "";
        public string? OutputPath { get; set; }
        public string? Error { get; set; }                  // the track could not be ripped at all
        public string? TagError { get; set; }               // ripped, but the tags / cover could not be written
        public int RetriedSectors { get; set; }             // needed another try, then read
        public int UnreadableSectors { get; set; }          // never read: silence in the file
        public int DisagreedChunks { get; set; }            // verify: the two reads differed here
        public int UnresolvedChunks { get; set; }           // ... and no two reads agreed
        public bool Verified { get; set; }
        public List<int> ProblemSectors { get; } = new();   // where (sector in the track), the first few

        public bool Failed => Error != null;
        public bool Damaged => UnreadableSectors > 0 || UnresolvedChunks > 0;
        public bool NeededExtraReads => !Damaged && (RetriedSectors > 0 || DisagreedChunks > 0);

        /// <summary>"about 2:31, 3:05": where in the track the problems are (the first three places).</summary>
        public string WhereText => string.Join(", ", ProblemSectors.Select(s => $"{s / 75 / 60}:{s / 75 % 60:00}").Distinct().Take(3));
    }

    public sealed record RipResult(List<TrackReport> Tracks, bool Canceled);

    // ── Planning ─────────────────────────────────────────────────────────

    /// <summary>
    /// Works out what to rip from the .cda files chosen (all on the same disc): where each track lies, and the file name ("Artist - Title", or
    /// "Track 02" when the disc is not known) and tags from what is kept about the disc.
    /// </summary>
    public static List<TrackJob> Plan(IReadOnlyList<string> trackFiles, out byte[]? cover)
    {
        cover = null;
        var jobs = new List<TrackJob>();
        if (trackFiles.Count == 0) return jobs;

        var root = Path.GetPathRoot(Path.GetFullPath(trackFiles[0])) ?? "";
        int trackCount = Directory.GetFiles(root, "*.cda").Length;
        var discId = CdDiscInfo.DiscId(root);
        var disc = discId == null ? null : CdDiscInfo.Load(discId);
        if (discId != null && CdDiscInfo.CoverFile(discId) is { } coverFile)
            try { cover = File.ReadAllBytes(coverFile); } catch { }

        foreach (var file in trackFiles)
        {
            var stub = CdDiscInfo.StubOf(file);
            int number = stub?.Number is >= 1 ? stub.Value.Number : CdDiscInfo.TrackNumber(file);
            var info = CdDiscInfo.InfoOf(file);   // null when the disc or the track is not known

            string? artist = info?.Artist?.Trim();
            string? title  = info?.Title?.Trim();
            if (string.IsNullOrEmpty(artist)) artist = null;
            bool known = !string.IsNullOrEmpty(title);

            string name = known ? (artist != null ? $"{artist} - {title}" : title!) : $"Track {number:00}";
            jobs.Add(new TrackJob
            {
                Number      = number,
                Start       = stub?.Start ?? 0,
                Length      = stub?.Length ?? 0,
                FileName    = SafeFileName(name),
                Title       = known ? title! : $"Track {number:00}",
                Artist      = artist,
                Album       = string.IsNullOrWhiteSpace(info?.Album) ? null : info!.Album!.Trim(),
                AlbumArtist = string.IsNullOrWhiteSpace(disc?.AlbumArtist) ? null : disc!.AlbumArtist!.Trim(),
                TrackCount  = trackCount,
            });
        }
        return jobs;
    }

    /// <summary>A text as a file name: characters Windows does not allow become "_", no dot or space at the end, not too long.</summary>
    public static string SafeFileName(string text)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(c < ' ' || Array.IndexOf(bad, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > 150) s = s[..150].TrimEnd('.', ' ');
        return s.Length == 0 ? "Track" : s;
    }

    // ── Running ──────────────────────────────────────────────────────────

    /// <summary>Rips the tracks one after the other on a background thread; <paramref name="progress"/> follows it (track n of N, bytes read).</summary>
    public static Task<RipResult> RunAsync(IReadOnlyList<TrackJob> jobs, Options options, TransferProgress progress, CancellationToken cancel) =>
        Task.Run(() => Run(jobs, options, progress, cancel));

    private static RipResult Run(IReadOnlyList<TrackJob> jobs, Options options, TransferProgress progress, CancellationToken cancel)
    {
        var reports = new List<TrackReport>();
        var ffmpeg = FfmpegFrame.Find() ?? throw new InvalidOperationException("ffmpeg.exe was not found.");
        var quality = options.Format.Qualities[Math.Clamp(options.QualityIndex, 0, options.Format.Qualities.Length - 1)];

        progress.ItemWord  = "Track";
        progress.FileTotal = Math.Max(jobs.Count, 1);
        progress.BytesTotal = jobs.Sum(j => (long)Math.Max(j.Length, 0) * SectorBytes * (options.Verify ? 2 : 1));
        progress.Preparing = false;

        var temp = Path.Combine(Path.GetTempPath(), "SwewolfFileExplorerRip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using var drive = OpenDrive(options.Root);
            for (int i = 0; i < jobs.Count; i++)
            {
                if (cancel.IsCancellationRequested) return new RipResult(reports, true);
                var job = jobs[i];
                progress.FileIndex   = i + 1;
                progress.CurrentName = job.FileName;

                var report = new TrackReport { Number = job.Number, Name = job.FileName };
                reports.Add(report);
                try
                {
                    if (drive.IsInvalid && ReadOverrideForTests == null) throw new IOException("The disc drive could not be opened for reading.");
                    if (job.Length <= 0) throw new IOException("The position of this track on the disc could not be read.");
                    RipTrack(drive, job, options, quality, ffmpeg, temp, report, progress, cancel);
                }
                catch (OperationCanceledException)
                {
                    reports.Remove(report);
                    return new RipResult(reports, true);
                }
                catch (Exception ex)
                {
                    report.Error = ex.Message;
                }
            }
            return new RipResult(reports, false);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* %TEMP% is cleaned up sooner or later */ }
        }
    }

    private static void RipTrack(SafeFileHandle drive, TrackJob job, Options options, Quality quality, string ffmpeg, string temp,
                                 TrackReport report, TransferProgress progress, CancellationToken cancel)
    {
        var pcm = Path.Combine(temp, $"track{job.Number:00}.pcm");
        var buffer = new byte[ChunkSectors * SectorBytes];

        using (var file = new FileStream(pcm, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
        {
            // Pass 1: read the whole track.
            for (int s = 0; s < job.Length; s += ChunkSectors)
            {
                cancel.ThrowIfCancellationRequested();
                int n = Math.Min(ChunkSectors, job.Length - s);
                ReadChunk(drive, job.Start + s, n, buffer, report, s);
                file.Write(buffer, 0, n * SectorBytes);
                progress.BytesDone += n * SectorBytes;
            }

            // Pass 2 (Verify): read it again and compare. It starts half a track further on, so that every place is read again long after
            // the first time and the drive's own cache cannot give the same answer.
            if (options.Verify)
            {
                var other  = new byte[buffer.Length];
                var stored = new byte[buffer.Length];
                var scratch = new TrackReport();   // what the second read goes through is not counted twice
                int chunks = (job.Length + ChunkSectors - 1) / ChunkSectors;
                int first  = chunks / 2;
                for (int k = 0; k < chunks; k++)
                {
                    cancel.ThrowIfCancellationRequested();
                    int s0 = (first + k) % chunks * ChunkSectors;
                    int n  = Math.Min(ChunkSectors, job.Length - s0);
                    int bytes = n * SectorBytes;

                    ReadChunk(drive, job.Start + s0, n, other, scratch, s0);
                    file.Position = (long)s0 * SectorBytes;
                    file.ReadExactly(stored, 0, bytes);

                    if (!other.AsSpan(0, bytes).SequenceEqual(stored.AsSpan(0, bytes)))
                        Resolve(drive, job, s0, n, stored, other, file, report, cancel);
                    progress.BytesDone += bytes;
                }
                report.Verified = true;
            }
        }

        // Encode, tag, and put the file under its name.
        var final = Encode(ffmpeg, pcm, job, options, quality, report, cancel);
        report.OutputPath = final;
        try { File.Delete(pcm); } catch { }
    }

    /// <summary>The two reads of a place differ: read it again until two reads agree (the answer is written into the file), else keep the first read.</summary>
    private static void Resolve(SafeFileHandle drive, TrackJob job, int s0, int n, byte[] first, byte[] second, FileStream file,
                                TrackReport report, CancellationToken cancel)
    {
        report.DisagreedChunks++;
        int bytes = n * SectorBytes;
        var seen = new Dictionary<string, (byte[] Data, int Count)>();
        byte[]? winner = null;

        void Add(byte[] data)
        {
            var key = Convert.ToHexString(SHA1.HashData(data.AsSpan(0, bytes)));
            seen.TryGetValue(key, out var entry);
            var copy = entry.Data ?? data.AsSpan(0, bytes).ToArray();
            seen[key] = (copy, entry.Count + 1);
            if (entry.Count + 1 >= 2) winner ??= copy;
        }
        Add(first);
        Add(second);

        var scratch = new TrackReport();
        var again = new byte[ChunkSectors * SectorBytes];
        var tiny  = new byte[SectorBytes];
        for (int t = 0; t < ReReads && winner == null; t++)
        {
            cancel.ThrowIfCancellationRequested();
            // A read from somewhere else in between, so the drive does not hand back what it still holds.
            ReadRaw(drive, job.Start + (s0 + job.Length / 2) % job.Length, 1, tiny);
            ReadChunk(drive, job.Start + s0, n, again, scratch, s0);
            Add(again);
        }

        if (winner == null)
        {
            report.UnresolvedChunks++;
            AddProblem(report, s0);
            return;
        }
        if (!winner.AsSpan().SequenceEqual(first.AsSpan(0, bytes)))
        {
            file.Position = (long)s0 * SectorBytes;
            file.Write(winner, 0, bytes);
            file.Flush();
        }
    }

    /// <summary>
    /// Reads <paramref name="count"/> sectors. One call for the lot; if that fails every sector is read by itself, with up to
    /// <see cref="SectorRetries"/> more tries; one that never reads becomes silence and is counted.
    /// </summary>
    private static void ReadChunk(SafeFileHandle drive, int lba, int count, byte[] buffer, TrackReport report, int trackSector)
    {
        if (ReadRaw(drive, lba, count, buffer)) return;

        var one = new byte[SectorBytes];
        for (int i = 0; i < count; i++)
        {
            bool ok = false;
            int tries = 0;
            for (; tries <= SectorRetries && !ok; tries++)
                ok = ReadRaw(drive, lba + i, 1, one);

            if (ok)
            {
                Buffer.BlockCopy(one, 0, buffer, i * SectorBytes, SectorBytes);
                if (tries > 1) report.RetriedSectors++;   // read on a later try (the first single read is only the chunk that failed being taken apart)
            }
            else
            {
                Array.Clear(buffer, i * SectorBytes, SectorBytes);
                report.UnreadableSectors++;
                AddProblem(report, trackSector + i);
            }
        }
    }

    private static void AddProblem(TrackReport report, int sector)
    {
        if (report.ProblemSectors.Count < 20) report.ProblemSectors.Add(sector);
    }

    // ── ffmpeg, tags, the file's name ────────────────────────────────────

    private static string Encode(string ffmpeg, string pcm, TrackJob job, Options options, Quality quality, TrackReport report, CancellationToken cancel)
    {
        var ext  = options.Format.Extension;
        var temp = Path.Combine(options.OutputFolder, $".rip-{Guid.NewGuid():N}{ext}");   // the extension tells ffmpeg the container
        try
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "s16le", "-ar", "44100", "-ac", "2", "-i", pcm }) start.ArgumentList.Add(a);
            foreach (var a in quality.Args) start.ArgumentList.Add(a);
            start.ArgumentList.Add(temp);

            using (var process = Process.Start(start) ?? throw new IOException("ffmpeg could not be started."))
            {
                process.StandardInput.Close();
                var error = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();
                while (!process.WaitForExit(200))
                {
                    if (!cancel.IsCancellationRequested) continue;
                    try { process.Kill(entireProcessTree: true); } catch { }
                    cancel.ThrowIfCancellationRequested();
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    var text = error.Wait(2000) ? error.Result.Trim() : "";
                    throw new IOException($"ffmpeg could not encode the track{(text.Length > 0 ? ": " + text.Split('\n')[0].Trim() : "")}.");
                }
            }

            try { WriteTags(temp, job, options.Cover); }
            catch (Exception ex) { report.TagError = ex.Message; }   // the music is fine: the file is kept without tags

            return ClaimName(options.OutputFolder, job.FileName, ext, temp);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    private static void WriteTags(string path, TrackJob job, byte[]? cover)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title        = job.Title;
        tag.Performers   = job.Artist != null ? new[] { job.Artist } : Array.Empty<string>();
        tag.AlbumArtists = job.AlbumArtist != null ? new[] { job.AlbumArtist } : Array.Empty<string>();
        tag.Album        = job.Album;
        tag.Track        = (uint)Math.Max(job.Number, 0);
        tag.TrackCount   = (uint)Math.Max(job.TrackCount, 0);
        if (cover is { Length: > 0 })
        {
            bool png = cover.Length > 8 && cover[0] == 0x89 && cover[1] == 0x50;
            tag.Pictures = new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = png ? "image/png" : "image/jpeg", Description = "Front cover" },
            };
        }
        file.Save();
    }

    /// <summary>Gives the finished file its name: "name.ext", else "name (1).ext", "name (2).ext" ... Never replaces a file.</summary>
    public static string ClaimName(string folder, string baseName, string ext, string tempFile)
    {
        for (int n = 0; n < 10000; n++)
        {
            var path = Path.Combine(folder, n == 0 ? baseName + ext : $"{baseName} ({n}){ext}");
            if (File.Exists(path) || Directory.Exists(path)) continue;
            try { File.Move(tempFile, path, overwrite: false); return path; }
            catch (IOException) when (File.Exists(path)) { /* taken a moment ago: the next number */ }
        }
        throw new IOException("No free file name was found.");
    }

    // ── The report ───────────────────────────────────────────────────────

    /// <summary>
    /// What the message after the rip says: how many tracks were done, then either "no read problems" or one line per track that had trouble
    /// ("Track 7 had read problems (around 2:31). It may click or skip."), and the offer to open the folder. <paramref name="problems"/> says
    /// whether a track failed or is damaged (the message then carries a warning symbol).
    /// </summary>
    public static string ReportText(RipResult rip, string folder, int total, bool verify, bool allTracks, out bool problems)
    {
        var done = rip.Tracks.Where(t => t.OutputPath != null).ToList();
        var lines = new List<string>();
        foreach (var t in rip.Tracks)
        {
            string which = $"Track {t.Number}";
            if (t.Failed) lines.Add($"{which} could not be ripped: {t.Error}");
            else if (t.Damaged)
                lines.Add($"{which} had read problems{(t.ProblemSectors.Count > 0 ? $" (around {t.WhereText})" : "")}. It may click or skip.");
            else if (t.NeededExtraReads)
                lines.Add(t.Verified ? $"{which} needed extra reads, but the check says it was read correctly."
                                     : $"{which} needed extra reads. Verification was off, so this was not checked.");
            if (t.TagError != null && !t.Failed) lines.Add($"{which}: the file was made, but its tags could not be written ({t.TagError}).");
        }

        problems = rip.Tracks.Any(t => t.Failed || t.Damaged);
        var text = new StringBuilder();
        text.Append(rip.Canceled ? $"Ripping was canceled: {done.Count} of {total} tracks were ripped to \"{folder}\"."
                    : done.Count == total ? $"Ripped {(total == 1 ? "the track" : allTracks ? $"all {total} tracks" : $"{total} tracks")} to \"{folder}\"."
                    : $"Ripped {done.Count} of {total} tracks to \"{folder}\".");
        text.AppendLine().AppendLine();

        if (lines.Count == 0)
            text.Append(verify ? "No read problems. Every track was checked by reading it twice."
                               : "No read problems were reported by the drive. (Verification was off.)");
        else
        {
            foreach (var line in lines.Take(12)) text.AppendLine(line);
            if (lines.Count > 12) text.AppendLine($"... and {lines.Count - 12} more.");
        }
        text.AppendLine().AppendLine().Append("Do you want to open the folder in a new tab?");
        return text.ToString();
    }

    // ── Windows: the raw read ────────────────────────────────────────────

    /// <summary>Only for tests: reads sectors from this function instead of the drive (lba, count, buffer; false = the read failed).</summary>
    public static Func<int, int, byte[], bool>? ReadOverrideForTests { get; set; }

    private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint IOCTL_CDROM_RAW_READ = 0x2403E;
    private const int CDDA = 2;   // TRACK_MODE_TYPE

    [StructLayout(LayoutKind.Sequential)]
    private struct RawReadInfo
    {
        public long DiskOffset;     // the sector number x 2048
        public uint SectorCount;
        public int TrackMode;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref RawReadInfo input, int inputSize,
                                               byte[] output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>Opens the drive ("E:\") for reading sectors; the handle is invalid if that did not work.</summary>
    public static SafeFileHandle OpenDrive(string root) =>
        CreateFileW(@"\\.\" + root.TrimEnd('\\', '/'), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>One raw read of <paramref name="count"/> audio sectors into <paramref name="buffer"/>; false if the drive could not read them.</summary>
    public static bool ReadRaw(SafeFileHandle drive, int lba, int count, byte[] buffer)
    {
        if (ReadOverrideForTests is { } hook) return hook(lba, count, buffer);
        var info = new RawReadInfo { DiskOffset = (long)lba * 2048, SectorCount = (uint)count, TrackMode = CDDA };
        int wanted = count * SectorBytes;
        return DeviceIoControl(drive, IOCTL_CDROM_RAW_READ, ref info, Marshal.SizeOf<RawReadInfo>(), buffer, wanted, out int got, IntPtr.Zero) && got == wanted;
    }
}
