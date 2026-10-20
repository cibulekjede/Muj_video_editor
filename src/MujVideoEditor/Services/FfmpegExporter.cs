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
        var totalDuration = videoDuration > audioDuration ? videoDuration : audioDuration;

        // ===== Stopa Video (+ zvuk z videi) =====
        foreach (var clip in timeline.VideoClips)
        {
            string video = $"[v{segmentCount}]";
            string audio = $"[a{segmentCount}]";
            string duration = Sec(clip.Duration);

            if (clip.Media.Kind == MediaKind.Image)
            {
                args.AddRange(["-loop", "1", "-i", clip.Media.FilePath]);
                filters.Add($"[{inputIndex}:v]trim=duration={duration},setpts=PTS-STARTPTS,{FitVideo(settings)}{video}");
                filters.Add(Silence(clip.Duration, audio));
            }
            else
            {
                string start = fastSeek ? "0" : Sec(clip.SourceIn);
                if (fastSeek)
                    args.AddRange(["-ss", Sec(clip.SourceIn), "-t", duration]);
                args.AddRange(["-i", clip.Media.FilePath]);

                filters.Add($"[{inputIndex}:v]trim=start={start}:duration={duration},setpts=PTS-STARTPTS,{FitVideo(settings)}{video}");
                filters.Add(await HasAudioAsync(clip.Media.FilePath)
                    // apad + atrim zajisti presnou delku, i kdyz je zvuk ve videu kratsi
                    ? $"[{inputIndex}:a]atrim=start={start}:duration={duration},asetpts=PTS-STARTPTS,{AudioFormat},apad,atrim=duration={duration}{audio}"
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
            filters.Add("[v0]null[vout]");
            filters.Add("[a0]anull[amain]");
        }
        else
        {
            string segments = string.Concat(Enumerable.Range(0, segmentCount).Select(i => $"[v{i}][a{i}]"));
            filters.Add($"{segments}concat=n={segmentCount}:v=1:a=1[vout][amain]");
        }

        // ===== Stopa Zvuk =====
        string audioOut = "[amain]";
        if (timeline.AudioClips.Count > 0)
        {
            int musicCount = 0;
            foreach (var clip in timeline.AudioClips)
            {
                string start = fastSeek ? "0" : Sec(clip.SourceIn);
                if (fastSeek)
                    args.AddRange(["-ss", Sec(clip.SourceIn), "-t", Sec(clip.Duration)]);
                args.AddRange(["-i", clip.Media.FilePath]);
                filters.Add($"[{inputIndex}:a]atrim=start={start}:duration={Sec(clip.Duration)},asetpts=PTS-STARTPTS,{AudioFormat}[m{musicCount}]");
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

        await RunEncoderAsync(args, totalDuration, progress, cancellationToken, settings.OutputPath);
    }

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
