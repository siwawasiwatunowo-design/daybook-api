using System.Text.RegularExpressions;

namespace Daybook.Backend.Application;

public sealed record WerResult(double Wer, int Substitutions, int Deletions, int Insertions,
    int ReferenceWordCount);

public static class WordErrorRate
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static WerResult Calculate(string reference, string hypothesis)
    {
        var r = Words.Matches(reference ?? "").Select(x => x.Value.ToLowerInvariant()).ToArray();
        var h = Words.Matches(hypothesis ?? "").Select(x => x.Value.ToLowerInvariant()).ToArray();
        if (r.Length == 0) throw new ArgumentException("Reference transcript must contain at least one word.", nameof(reference));
        var d = new int[r.Length + 1, h.Length + 1];
        for (var i = 0; i <= r.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= h.Length; j++) d[0, j] = j;
        for (var i = 1; i <= r.Length; i++)
            for (var j = 1; j <= h.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));

        // Backtrack to classify S/D/I; tie-breaking is deterministic.
        var subs = 0; var dels = 0; var ins = 0; var x = r.Length; var y = h.Length;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && r[x - 1] == h[y - 1] && d[x, y] == d[x - 1, y - 1]) { x--; y--; continue; }
            if (x > 0 && y > 0 && d[x, y] == d[x - 1, y - 1] + 1) { subs++; x--; y--; continue; }
            if (x > 0 && d[x, y] == d[x - 1, y] + 1) { dels++; x--; continue; }
            if (y > 0) { ins++; y--; continue; }
        }
        var wer = (double)(subs + dels + ins) / r.Length;
        return new WerResult(wer, subs, dels, ins, r.Length);
    }
}

public static class CharacterErrorRate
{
    private static readonly Regex Characters = new(@"[\p{L}\p{N}]", RegexOptions.Compiled);

    public static double Calculate(string reference, string hypothesis)
    {
        var r = string.Concat(Characters.Matches(reference ?? "").Select(x => x.Value.ToLowerInvariant()));
        var h = string.Concat(Characters.Matches(hypothesis ?? "").Select(x => x.Value.ToLowerInvariant()));
        if (r.Length == 0) throw new ArgumentException("Reference transcript must contain at least one letter or digit.", nameof(reference));
        var previous = Enumerable.Range(0, h.Length + 1).ToArray();
        for (var i = 1; i <= r.Length; i++)
        {
            var current = new int[h.Length + 1];
            current[0] = i;
            for (var j = 1; j <= h.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
            previous = current;
        }
        return (double)previous[h.Length] / r.Length;
    }
}
