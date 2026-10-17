using System.Globalization;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// What the user may enter as a rating or an expectation (as in TeraTool, with the scale 1-10 decided for this app).
/// Anything that is not a number from 1 to 10 (blank, text, zero, a negative number, more than 10) means "remove it".
/// A comma is taken as a decimal point. A rating is a whole number: 8.9 (or 8,9) is 8. An expectation keeps its decimals.
/// </summary>
public static class RatingRules
{
    public const int Min = 1;
    public const int Max = 10;

    private static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        text = text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        if (double.IsNaN(value) || value < Min || value > Max) return null;
        return value;
    }

    /// <summary>The rating the text means: 1-10 (the decimals cut off), or null = remove it.</summary>
    public static int? ParseRating(string? text) =>
        ParseNumber(text) is { } value ? (int)Math.Floor(value) : null;

    /// <summary>The expectation the text means: 1-10 with decimals (kept to two), or null = remove it.</summary>
    public static double? ParseExpectation(string? text) =>
        ParseNumber(text) is { } value ? Math.Round(value, 2) : null;

    /// <summary>A rating for the list (and the input box): nothing when there is none.</summary>
    public static string Format(int? rating) => rating?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>An expectation for the list (and the input box): with a decimal point, no needless zeros; nothing when there is none.</summary>
    public static string Format(double? expectation) =>
        expectation?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
}
