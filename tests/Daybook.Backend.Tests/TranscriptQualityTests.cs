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
}
