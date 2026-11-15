using Daybook.Backend.Application;
using Daybook.Backend.Infrastructure;
using Daybook.Backend.Options;
using Xunit;

namespace Daybook.Backend.Tests;

public sealed class TranscriptQualityTests
{
    [Fact]
    public void Wer_CalculatesSubstitutionRate()
    {
        var result = WordErrorRate.Calculate("hello world", "hello there");
        Assert.Equal(0.5, result.Wer, 3);
        Assert.Equal(1, result.Substitutions);
        Assert.Equal(0, result.Deletions);
        Assert.Equal(0, result.Insertions);
    }

    [Fact]
    public void Wer_CalculatesMixedEditDistance()
    {
        var result = WordErrorRate.Calculate("one two three", "one three four");
        Assert.Equal(2, result.Substitutions + result.Deletions + result.Insertions);
        Assert.Equal(2d / 3d, result.Wer, 3);
    }

    [Fact]
    public void Cer_CalculatesCharacterErrorRate()
    {
        var result = CharacterErrorRate.Calculate("cat", "cut");
        Assert.Equal(1d / 3d, result, 3);
    }

    [Fact]
    public void Stitcher_RemovesExactOverlapBetweenAdjacentChunks()
    {
        var result = TranscriptStitcher.Stitch(new[] {
            "kita akan membahas proses pembayaran hari ini",
            "proses pembayaran hari ini dan tindak lanjutnya"
        });
        Assert.Equal("kita akan membahas proses pembayaran hari ini dan tindak lanjutnya", result);
    }

    [Fact]
    public void QualityChecker_FlagsRepeatedHallucinationPattern()
    {
        var profile = new ActivityProcessingProfile { RepeatedWordRatioThreshold = 0.1, MaxRepeatedNgramRatio = 0.1 };
        var result = new SttTranscriptionResult(
            "sebuah sebuah sebuah sebuah sebuah sebuah sebuah sebuah",
            "whisper-large-v3", "id", 0.98, 20,
            Array.Empty<SttSegmentResult>());
        var quality = new HeuristicTranscriptQualityChecker().Check(result, profile);
        Assert.True(quality.IsSuspicious);
        Assert.Contains(quality.Reasons, x => x.Contains("repeated", StringComparison.OrdinalIgnoreCase));
    }
    [Theory]
    [InlineData("id", "Indonesian")]
    [InlineData("id-ID", "ind")]
    [InlineData("en-US", "English")]
    [InlineData("ja", "Japanese")]
    public void QualityChecker_DoesNotFlagEquivalentLanguageNames(string configured, string detected)
    {
        var profile = new ActivityProcessingProfile { Language = configured };
        var result = new SttTranscriptionResult("ini adalah contoh kalimat untuk pengujian",
            "whisper-large-v3", detected, null, 5, Array.Empty<SttSegmentResult>());
        var quality = new HeuristicTranscriptQualityChecker().Check(result, profile);
        Assert.DoesNotContain(quality.Reasons, x => x.Contains("differs from configured language"));
    }

    [Fact]
    public void QualityChecker_FlagsCannedIndonesianOutro()
    {
        var profile = new ActivityProcessingProfile { Language = "id" };
        var result = new SttTranscriptionResult("Terima kasih telah menonton.",
            "whisper-large-v3", "Indonesian", null, 18.68,
            new[] { new SttSegmentResult("Terima kasih telah menonton.", -0.465776,
                0.0129852295, 0.7837838, 0, 18.68) });
        var quality = new HeuristicTranscriptQualityChecker().Check(result, profile);
        Assert.True(quality.IsSuspicious);
        Assert.Contains(quality.Reasons, x => x.Contains("canned", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(quality.Reasons, x => x.Contains("differs from configured language"));
    }

    [Fact]
    public void QualityChecker_AppliesLowDensityCheckToShortRecordings()
    {
        var profile = new ActivityProcessingProfile { MinSpeechWordsPerMinute = 20 };
        var result = new SttTranscriptionResult("ini contoh", "whisper-large-v3",
            "Indonesian", null, 18.68, Array.Empty<SttSegmentResult>());
        var quality = new HeuristicTranscriptQualityChecker().Check(result, profile);
        Assert.Contains(quality.Reasons, x => x.Contains("density", StringComparison.OrdinalIgnoreCase));
    }

}
