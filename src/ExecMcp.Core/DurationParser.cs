using System.Globalization;
using System.Text.RegularExpressions;

namespace ExecMcp.Core;

public static partial class DurationParser
{
    [GeneratedRegex("^(?<value>\\d+(?:\\.\\d+)?)(?<unit>ms|s|m|h|d)$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    public static int Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds >= 0)
            return milliseconds;

        var match = DurationRegex().Match(value);
        if (!match.Success)
            throw new ArgumentException($"Invalid duration: {value}", nameof(value));

        var number = decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        var factor = match.Groups["unit"].Value switch
        {
            "ms" => 1m,
            "s" => 1000m,
            "m" => 60_000m,
            "h" => 3_600_000m,
            "d" => 86_400_000m,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        var rounded = decimal.Round(number * factor, 0, MidpointRounding.AwayFromZero);
        if (rounded < 0 || rounded > int.MaxValue)
            throw new OverflowException("Duration is outside the supported range.");
        return checked((int)rounded);
    }
}
