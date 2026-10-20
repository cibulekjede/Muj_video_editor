using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MujVideoEditor.Models;

namespace MujVideoEditor.Services;

public record ExportSettings(string OutputPath, int Width, int Height, int FramesPerSecond, int Crf);

public class ExportFailedException(string message, string log) : Exception(message)
{
    public string Log { get; } = log;
}

/// <summary>
/// Slozi casovou osu do jednoho MP4 pomoci FFmpeg (H.264 + AAC).
/// </summary>
public partial class FfmpegExporter(string ffmpegPath)
{
    private const string AudioFormat = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Najde ffmpeg.exe: vedle aplikace, z winget instalace, nebo v PATH.</summary>
    public static string? FindFfmpeg()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe")
        };

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));

        string wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(wingetPackages))
        {
            foreach (var package in Directory.EnumerateDirectories(wingetPackages, "*FFmpeg*"))
                candidates.AddRange(SafeFindFiles(package, "ffmpeg.exe"));
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir))
                candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task ExportAsync(TimelineState timeline, ExportSettings settings,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Novejsi FFmpeg (od verze 2.1) umi presne skocit na cas uz pri cteni souboru, coz je mnohem rychlejsi.
        // Stary FFmpeg musi soubor dekodovat od zacatku a usek vybrat filtrem trim.
        bool fastSeek = await SupportsAccurateInputSeekAsync();

        var args = new List<string> { "-y" };
        var filters = new List<string>();
        int inputIndex = 0;
        int segmentCount = 0;

        var videoDuration = Sum(timeline.VideoClips);
        var audioDuration = Sum(timeline.AudioClips);
        var textEnd = timeline.Texts.Count == 0 ? TimeSpan.Zero : timeline.Texts.Max(c => c.End);
        var totalDuration = new[] { videoDuration, audioDuration, textEnd }.Max();

        // ===== Stopa Video (+ zvuk z videi) =====
        foreach (var clip in timeline.VideoClips)
        {
            string video = $"[v{segmentCount}]";
            string audio = $"[a{segmentCount}]";

            if (clip.Media.Kind == MediaKind.Image)
            {
                args.AddRange(["-loop", "1", "-i", clip.Media.FilePath]);
                filters.Add($"[{inputIndex}:v]trim=duration={Sec(clip.Duration)},setpts=PTS-STARTPTS,{FitVideo(settings)}{VideoFades(clip)}{video}");
                filters.Add(Silence(clip.Duration, audio));
            }
            else
            {
                string start = AddSourceInput(args, clip, fastSeek);
                string sourceSpan = Sec(clip.SourceOut - clip.SourceIn);
                filters.Add($"[{inputIndex}:v]trim=start={start}:duration={sourceSpan},{SpeedPts(clip.Speed)},{FitVideo(settings)}{VideoFades(clip)}{video}");
                filters.Add(await HasAudioAsync(clip.Media.FilePath)
                    ? AudioChain(inputIndex, start, clip) + audio
                    : Silence(clip.Duration, audio));
            }

            inputIndex++;
            segmentCount++;
        }

        // Kdyz je hudba delsi nez video, doplnime konec cernou
        var gap = totalDuration - videoDuration;
        if (gap > TimeSpan.FromMilliseconds(10))
        {
            filters.Add($"color=c=black:s={settings.Width}x{settings.Height}:r={settings.FramesPerSecond}:d={Sec(gap)},format=yuv420p,setsar=1[v{segmentCount}]");
            filters.Add(Silence(gap, $"[a{segmentCount}]"));
            segmentCount++;
        }

        if (segmentCount == 1)
        {
            // Stary FFmpeg neumi concat s jedinym vstupem
            filters.Add("[v0]null[vcat]");
            filters.Add("[a0]anull[amain]");
        }
        else
        {
            string segments = string.Concat(Enumerable.Range(0, segmentCount).Select(i => $"[v{i}][a{i}]"));
            filters.Add($"{segments}concat=n={segmentCount}:v=1:a=1[vcat][amain]");
        }

        // ===== Stopa Text =====
        string tempDir = Path.Combine(Path.GetTempPath(), "MujVideoEditor", Guid.NewGuid().ToString("N"));
        var textFilters = new List<string>();
        for (int i = 0; i < timeline.Texts.Count; i++)
        {
            var clip = timeline.Texts[i];
            if (clip.Overlay is not { } overlay || string.IsNullOrWhiteSpace(overlay.Text))
                continue;

            // Text predavame souborem: odpadne slozite escapovani diakritiky, uvozovek a dvojtecek
            Directory.CreateDirectory(tempDir);
            string textFile = Path.Combine(tempDir, $"text{i}.txt");
            await File.WriteAllTextAsync(textFile, overlay.Text.Replace("\r\n", "\n"), new UTF8Encoding(false), cancellationToken);
            textFilters.Add(DrawText(overlay, textFile, clip.Start, clip.End, settings, fastSeek));
        }
        filters.Add(textFilters.Count == 0 ? "[vcat]null[vout]" : $"[vcat]{string.Join(",", textFilters)}[vout]");

        // ===== Stopa Zvuk =====
        string audioOut = "[amain]";
        if (timeline.AudioClips.Count > 0)
        {
            int musicCount = 0;
            foreach (var clip in timeline.AudioClips)
            {
                string start = AddSourceInput(args, clip, fastSeek);
                filters.Add(AudioChain(inputIndex, start, clip) + $"[m{musicCount}]");
                inputIndex++;
                musicCount++;
            }

            string music = string.Concat(Enumerable.Range(0, musicCount).Select(i => $"[m{i}]"));
            filters.Add(musicCount == 1 ? "[m0]anull[music]" : $"{music}concat=n={musicCount}:v=0:a=1[music]");
            // amix ztlumi kazdy vstup na polovinu, volume=2 to vraci
            filters.Add("[amain][music]amix=inputs=2:duration=first:dropout_transition=0,volume=2[aout]");
            audioOut = "[aout]";
        }

        args.AddRange(["-filter_complex", string.Join(";", filters)]);
        args.AddRange(["-map", "[vout]", "-map", audioOut]);
        args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", settings.Crf.ToString(Inv), "-pix_fmt", "yuv420p"]);
        args.AddRange(["-c:a", "aac", "-strict", "-2", "-b:a", "192k"]);
        args.AddRange(["-r", settings.FramesPerSecond.ToString(Inv), "-movflags", "+faststart", settings.OutputPath]);

        try
        {
            await RunEncoderAsync(args, totalDuration, progress, cancellationToken, settings.OutputPath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Docasne soubory s textem se nepodarilo smazat - nevadi
            }
        }
    }

    /// <summary>Prida vstupni soubor klipu; vrati, od kolika sekund ma filtr trim cist.</summary>
    private static string AddSourceInput(List<string> args, TimelineClip clip, bool fastSeek)
    {
        if (fastSeek)
            args.AddRange(["-ss", Sec(clip.SourceIn), "-t", Sec(clip.SourceOut - clip.SourceIn)]);
        args.AddRange(["-i", clip.Media.FilePath]);
        return fastSeek ? "0" : Sec(clip.SourceIn);
    }

    /// <summary>Zvuk klipu: usek ze zdroje, rychlost, hlasitost, presna delka a prolinani.</summary>
    private static string AudioChain(int inputIndex, string start, TimelineClip clip)
    {
        string chain = $"[{inputIndex}:a]atrim=start={start}:duration={Sec(clip.SourceOut - clip.SourceIn)},asetpts=PTS-STARTPTS,";
        chain += Tempo(clip.Speed) + AudioFormat;
        if (Math.Abs(clip.Volume - 1) > 0.001)
            chain += $",volume={clip.Volume.ToString("0.##", Inv)}";
        // apad + atrim zajisti presnou delku, i kdyz je zvuk ve zdroji kratsi
        chain += $",apad,atrim=duration={Sec(clip.Duration)}";
        return chain + AudioFades(clip);
    }

    private static string SpeedPts(double speed) =>
        Math.Abs(speed - 1) < 0.001 ? "setpts=PTS-STARTPTS" : $"setpts=(PTS-STARTPTS)/{speed.ToString("0.###", Inv)}";

    /// <summary>atempo umi jen 0,5 az 2x, vetsi zmeny se skladaji z vice filtru za sebou.</summary>
    private static string Tempo(double speed)
    {
        var factors = new List<double>();
        while (speed > 2) { factors.Add(2); speed /= 2; }
        while (speed < 0.5) { factors.Add(0.5); speed /= 0.5; }
        if (Math.Abs(speed - 1) > 0.001)
            factors.Add(speed);
        return string.Concat(factors.Select(f => $"atempo={f.ToString("0.####", Inv)},"));
    }

    private static (double In, double Out) FadeLengths(TimelineClip clip)
    {
        double half = clip.Duration.TotalSeconds / 2;
        return (Math.Min(clip.FadeIn, half), Math.Min(clip.FadeOut, half));
    }

    private static string VideoFades(TimelineClip clip) => Fades(clip, "fade");

    private static string AudioFades(TimelineClip clip) => Fades(clip, "afade");

    private static string Fades(TimelineClip clip, string filter)
    {
        var (fadeIn, fadeOut) = FadeLengths(clip);
        string result = "";
        if (fadeIn > 0)
            result += $",{filter}=t=in:st=0:d={fadeIn.ToString("0.###", Inv)}";
        if (fadeOut > 0)
            result += $",{filter}=t=out:st={(clip.Duration.TotalSeconds - fadeOut).ToString("0.###", Inv)}:d={fadeOut.ToString("0.###", Inv)}";
        return result;
    }

    private static readonly Dictionary<string, (string Regular, string Bold)> FontFiles = new()
    {
        ["Segoe UI"] = ("segoeui.ttf", "segoeuib.ttf"),
        ["Arial"] = ("arial.ttf", "arialbd.ttf"),
        ["Impact"] = ("impact.ttf", "impact.ttf"),
        ["Georgia"] = ("georgia.ttf", "georgiab.ttf"),
        ["Comic Sans MS"] = ("comic.ttf", "comicbd.ttf"),
        ["Consolas"] = ("consola.ttf", "consolab.ttf")
    };

    private static string FontFile(TextOverlay overlay)
    {
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var files = FontFiles.GetValueOrDefault(overlay.FontFamily, FontFiles["Arial"]);
        string path = Path.Combine(fontsDir, overlay.Bold ? files.Bold : files.Regular);
        return File.Exists(path) ? path : Path.Combine(fontsDir, "arial.ttf");
    }

    /// <summary>Filtr drawtext pro jeden text (velikosti jsou definovane pro kratsi stranu 1080 px).</summary>
    private static string DrawText(TextOverlay overlay, string textFile, TimeSpan start, TimeSpan end,
        ExportSettings settings, bool modern)
    {
        double scale = Math.Min(settings.Width, settings.Height) / 1080.0;
        string color = overlay.Color.TrimStart('#');
        var parts = new List<string>
        {
            $"fontfile='{FilterPath(FontFile(overlay))}'",
            $"textfile='{FilterPath(textFile)}'",
            $"fontsize={Math.Max(1, Math.Round(overlay.FontSize * scale)).ToString(Inv)}",
            $"fontcolor=0x{color}",
            $"x='{overlay.X.ToString("0.####", Inv)}*w-text_w/2'",
            $"y='{overlay.Y.ToString("0.####", Inv)}*h-text_h/2'"
        };

        if (overlay.HasBackground)
        {
            parts.Add("box=1:boxcolor=black@0.5");
            if (modern)
                parts.Add($"boxborderw={Math.Round(10 * scale).ToString(Inv)}");
        }

        if (overlay.HasShadow)
        {
            string offset = Math.Max(1, Math.Round(3 * scale)).ToString(Inv);
            parts.Add($"shadowcolor=black@0.6:shadowx={offset}:shadowy={offset}");
        }

        // Novejsi FFmpeg ma volbu enable, stary jen draw
        string window = $"between(t,{Sec(start)},{Sec(end)})";
        parts.Add(modern ? $"enable='{window}'" : $"draw='{window}'");

        return "drawtext=" + string.Join(":", parts);
    }

    /// <summary>Cesta k souboru ve tvaru, kteremu rozumi filtry FFmpeg (C\:/slozka/soubor).</summary>
    private static string FilterPath(string path) =>
        path.Replace('\\', '/').Replace(":", "\\:").Replace("'", "'\\''");

    private async Task RunEncoderAsync(List<string> args, TimeSpan totalDuration, IProgress<double> progress,
        CancellationToken cancellationToken, string outputPath)
    {
        using var process = new Process { StartInfo = CreateStartInfo(args) };
        var log = new Queue<string>();
        process.Start();

        // FFmpeg pise prubeh do stderr jako "... time=00:00:05.12 ..." oddelene \r
        var readTask = Task.Run(async () =>
        {
            var line = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        HandleLine(line.ToString());
                        line.Clear();
                    }
                    else
                    {
                        line.Append(c);
                    }
                }
            }
            HandleLine(line.ToString());
        });

        void HandleLine(string text)
        {
            if (text.Length == 0)
                return;

            lock (log)
            {
                log.Enqueue(text);
                if (log.Count > 40)
                    log.Dequeue();
            }

            var match = TimeRegex().Match(text);
            if (match.Success && totalDuration > TimeSpan.Zero)
            {
                var time = new TimeSpan(0, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), 0)
                           + TimeSpan.FromSeconds(double.Parse(match.Groups[3].Value, Inv));
                progress.Report(Math.Clamp(time / totalDuration, 0, 1));
            }
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await readTask;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            TryDelete(outputPath);
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDelete(outputPath);
            string tail;
            lock (log)
                tail = string.Join(Environment.NewLine, log.Where(l => !l.StartsWith("frame=")).TakeLast(15));
            throw new ExportFailedException("FFmpeg skončil s chybou.", tail);
        }

        progress.Report(1);
    }

    private async Task<bool> SupportsAccurateInputSeekAsync()
    {
        string output = await RunAndCaptureAsync(["-version"]);
        var release = Regex.Match(output, @"ffmpeg version n?(\d+)\.(\d+)");
        if (release.Success)
        {
            int major = int.Parse(release.Groups[1].Value);
            int minor = int.Parse(release.Groups[2].Value);
            return major > 2 || (major == 2 && minor >= 1);
        }

        // Vyvojove buildy "N-12345-g..." - cislo roste s kazdou zmenou; 2.1 odpovida zhruba N-58000
        var gitBuild = Regex.Match(output, @"ffmpeg version N-(\d+)");
        return gitBuild.Success && int.Parse(gitBuild.Groups[1].Value) >= 58000;
    }

    private readonly Dictionary<string, bool> _hasAudioCache = [];

    private async Task<bool> HasAudioAsync(string filePath)
    {
        if (_hasAudioCache.TryGetValue(filePath, out bool cached))
            return cached;

        // "ffmpeg -i soubor" vypise seznam stop (a skonci chybou, protoze chybi vystup - to je v poradku)
        string output = await RunAndCaptureAsync(["-hide_banner", "-i", filePath]);
        if (output.Contains("Unrecognized option"))
            output = await RunAndCaptureAsync(["-i", filePath]);

        bool hasAudio = Regex.IsMatch(output, @"Stream #\d+:\d+.*: Audio:");
        _hasAudioCache[filePath] = hasAudio;
        return hasAudio;
    }

    private async Task<string> RunAndCaptureAsync(IEnumerable<string> args)
    {
        using var process = new Process { StartInfo = CreateStartInfo(args) };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return await stdout + await stderr;
    }

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    /// <summary>Zmensi/zvetsi obraz, aby se vesel do vystupu, a doplni cerne pruhy (letterbox).</summary>
    private static string FitVideo(ExportSettings s)
    {
        string ratio = ((double)s.Width / s.Height).ToString("0.######", Inv);
        return $"scale=w='if(gt(iw/ih,{ratio}),{s.Width},trunc({s.Height}*iw/ih/2)*2)'" +
               $":h='if(gt(iw/ih,{ratio}),trunc({s.Width}*ih/iw/2)*2,{s.Height})'," +
               $"pad={s.Width}:{s.Height}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps=fps={s.FramesPerSecond},format=yuv420p";
    }

    private static string Silence(TimeSpan duration, string label) =>
        $"anullsrc=r=48000:cl=stereo,atrim=duration={Sec(duration)},{AudioFormat}{label}";

    private static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.###", Inv);

    private static TimeSpan Sum(IEnumerable<TimelineClip> clips) =>
        clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

    private static IEnumerable<string> SafeFindFiles(string dir, string name)
    {
        try
        {
            return Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Nedokonceny soubor se nepodarilo smazat - nevadi
        }
    }

    [GeneratedRegex(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex TimeRegex();
}
