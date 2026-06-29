using System.Text;

namespace TinyCosmos.Core;

public static class OutputSanitizer
{
    public static string SanitizeStructuredText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var scalar = rune.Value;
            if (scalar == '\n' || scalar == '\r' || scalar == '\t')
            {
                builder.Append(rune);
                continue;
            }

            if (scalar < 0x20 || scalar == 0x7f || IsBidiControl(scalar))
            {
                builder.Append("\\u");
                builder.Append(scalar.ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    private static bool IsBidiControl(int scalar) =>
        scalar is 0x061c or 0x200e or 0x200f or >= 0x202a and <= 0x202e or >= 0x2066 and <= 0x2069;
}
