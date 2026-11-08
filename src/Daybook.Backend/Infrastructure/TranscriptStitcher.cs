using System.Text.RegularExpressions;

namespace Daybook.Backend.Infrastructure;

/// <summary>Removes exact repeated word overlap created by adjacent audio chunks. It never fuzzy-rewrites words.</summary>
public static class TranscriptStitcher
{
    private static readonly Regex Token = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    public static string Stitch(IEnumerable<string?> chunks)
    {
        var output = "";
        foreach (var chunk in chunks.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()))
        {
            if (output.Length == 0) { output = chunk; continue; }
            var left = Token.Matches(output);
            var right = Token.Matches(chunk);
            var max = Math.Min(30, Math.Min(left.Count, right.Count));
            var overlap = 0;
            for (var count = max; count >= 2; count--)
            {
                var match = true;
                for (var i = 0; i < count; i++)
                {
                    var a = left[left.Count - count + i].Value;
                    var b = right[i].Value;
                    if (!a.Equals(b, StringComparison.OrdinalIgnoreCase)) { match = false; break; }
                }
                if (match) { overlap = count; break; }
            }
            var remainder = chunk;
            if (overlap > 0)
            {
                var lastDuplicate = right[overlap - 1];
                remainder = chunk[(lastDuplicate.Index + lastDuplicate.Length)..].TrimStart(' ', '\t', '\r', '\n', ',', ';', ':', '-', '–');
            }
            if (!string.IsNullOrWhiteSpace(remainder)) output += " " + remainder;
        }
        return output.Trim();
    }
}
