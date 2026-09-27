using System.Windows;
using System.Windows.Media;

namespace Nightrunner.UI;

/// <summary>
/// The one accent colour in play, and which game it stands for.
/// </summary>
/// <remarks>
/// The app shows a single accent at a time: it identifies the game being worked on. `Warn` and `Error` keep their
/// own fixed hues and are sub-colours only — they never become the accent. To add a game, add a row here.
/// </remarks>
public static class GameAccent
{
    /// <summary>Accent per game id, and the colour used before a game is chosen.</summary>
    public static readonly Color Neutral = Hex("#6F7885");   // slate — no game open

    private static readonly Dictionary<string, Color> ByGame = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dltb"] = Hex("#DC5C57"),   // Dying Light: The Beast — red
        ["dl2"] = Hex("#4C8DDA"),    // Dying Light 2 — blue
        ["dl1"] = Hex("#E0B341"),    // Dying Light — yellow
    };

    public static Color For(string? gameId) =>
        gameId is not null && ByGame.TryGetValue(gameId, out var c) ? c : Neutral;

    public static SolidColorBrush BrushFor(string? gameId)
    {
        var b = new SolidColorBrush(For(gameId));
        b.Freeze();
        return b;
    }

    /// <summary>Swap the live accent. Everything accent-driven binds it with DynamicResource, so this repaints.</summary>
    public static void Apply(string? gameId) => Apply(For(gameId));

    /// <summary>Raised after the accent brushes change, for anything that cannot use DynamicResource.</summary>
    public static event Action? Changed;

    public static void Apply(Color accent)
    {
        var res = Application.Current?.Resources;
        if (res is null) return;
        res["AccentColor"] = accent;
        res["Accent"] = Freeze(accent);
        res["AccentDim"] = Freeze(Mix(accent, Hex("#15171A"), 0.42));
        res["AccentFaint"] = Freeze(Mix(accent, Hex("#15171A"), 0.80));
        // deep: what a tab or caption is painted with. The full accent behind white text is too bright to read.
        res["AccentDeep"] = Freeze(Mix(accent, Hex("#15171A"), 0.58));
        Changed?.Invoke();
    }

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary><paramref name="t"/> = 0 keeps <paramref name="a"/>, 1 gives <paramref name="b"/>.</summary>
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s)!;
}
