using System.Diagnostics;
using System.Globalization;
using Daybook.Backend.Application;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Infrastructure;

public sealed class FfmpegAudioChunker(IOptions<WorkerOptions> options) : IAudioChunker
{
    public async Task<IReadOnlyList<string>> CreateChunksAsync(string inputPath, string outputDirectory,
        ActivityProcessingProfile profile, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var oldFile in Directory.EnumerateFiles(outputDirectory, "chunk_*.wav")) File.Delete(oldFile);

        var duration = await GetDurationAsync(inputPath, ct);
        if (duration <= 0) throw new InvalidOperationException("Could not determine source audio duration.");
        var chunkSeconds = Math.Max(60, profile.ChunkDurationMinutes * 60);
        var overlap = Math.Clamp(profile.ChunkOverlapSeconds, 0, 5);
        var filters = BuildFilters(profile);
        var files = new List<string>();
        var sequence = 0;
        for (double start = 0; start < duration; start += chunkSeconds)
        {
            ct.ThrowIfCancellationRequested();
            var actualDuration = Math.Min(duration - start, chunkSeconds + (start + chunkSeconds < duration ? overlap : 0));
            var output = Path.Combine(outputDirectory, $"chunk_{sequence:D4}.wav");
            var psi = new ProcessStartInfo
            {
                FileName = options.Value.FfmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-y", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", inputPath, "-t", actualDuration.ToString("0.###", CultureInfo.InvariantCulture),
                "-vn", "-af", filters, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", output })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg failed for chunk {sequence}: {stderr}");
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidOperationException($"FFmpeg produced an empty chunk {sequence}.");
            files.Add(output);
            sequence++;
        }
        return files;
    }

    private async Task<double> GetDurationAsync(string inputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.Value.FfprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", inputPath }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFprobe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0 || !double.TryParse(stdout.Trim(), CultureInfo.InvariantCulture, out var duration))
            throw new InvalidOperationException($"FFprobe failed: {stderr}");
        return duration;
    }

    private static string BuildFilters(ActivityProcessingProfile profile)
    {
        var filters = new List<string>();
        if (profile.PreprocessingProfile.Equals("enhanced", StringComparison.OrdinalIgnoreCase))
        {
            if (profile.HighPassHz > 0) filters.Add($"highpass=f={Math.Clamp(profile.HighPassHz, 40, 300)}");
            if (profile.LowPassHz > 0) filters.Add($"lowpass=f={Math.Clamp(profile.LowPassHz, 3000, 7900)}");
            if (profile.EnableDenoise) filters.Add($"afftdn=nf=-{Math.Clamp(profile.DenoiseNoiseFloorDb, 10, 50)}");
        }
        filters.Add($"loudnorm=I={Math.Clamp(profile.LoudnessTargetI, -24, -12).ToString(CultureInfo.InvariantCulture)}:TP=-1.5:LRA=11");
        return string.Join(",", filters);
    }
}
