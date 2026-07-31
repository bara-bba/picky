using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace Picky;

/// <summary>
/// Single source of truth for the app accent color. Mutating the shared
/// SolidColorBrush resources in place updates every control that references
/// them (buttons, combo, textbox focus, the snip overlay) live.
/// </summary>
internal static class AccentTheme
{
    public static Color Current { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>Readable foreground for content sitting on the accent: white on dark accents, near-black on light ones.</summary>
    public static Color OnAccent { get; private set; } = Colors.White;

    /// <summary>Parses "#RRGGBB" or "RRGGBB"; returns false for anything else.</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        color = Current;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        color = Color.FromRgb(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static void Apply(Color c)
    {
        Current = c;
        OnAccent = Luminance(c) > 0.5 ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Colors.White;

        SetBrush("Brush.Accent", c);
        SetBrush("Brush.AccentHover", Lerp(c, Colors.White, 0.12));
        SetBrush("Brush.AccentPressed", Lerp(c, Colors.Black, 0.12));
        SetBrush("Brush.OnAccent", OnAccent);
    }

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    private static double Luminance(Color c)
    {
        static double Channel(double v)
        {
            v /= 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static void SetBrush(string key, Color c)
    {
        // Replace the resource entry; accent consumers reference it via DynamicResource
        // so the swap propagates to every control live.
        Application.Current.Resources[key] = new SolidColorBrush(c);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
