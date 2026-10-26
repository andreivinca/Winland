using System;
using System.Drawing;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Winland.Common;

namespace Winland.Indicator;

/// <summary>
/// The badge's colours, following the Windows app mode (Settings → Personalization → Colors → "Choose your
/// default app mode"). Light is a faint white badge with black items; Dark is its exact inverse — a faint
/// black badge with white items. The workspace on screen is drawn in the same ink, underlined. The alphas
/// are the same in both, so only the hue changes, never how see-through the badge is.
/// </summary>
internal sealed record BadgeTheme(Color Backing, Color Ink)
{
    private const int BackingAlpha = 64;        // ~25% opaque — a hint, not a widget
    private const int InkAlpha = 170;           // the items stay stronger than their backing, still soft

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    public static readonly BadgeTheme Light = Of(paper: Color.White, ink: Color.Black);

    public static readonly BadgeTheme Dark = Of(paper: Color.Black, ink: Color.White);

    private static BadgeTheme Of(Color paper, Color ink) => new(
        Color.FromArgb(BackingAlpha, paper),
        Color.FromArgb(InkAlpha, ink));

    /// <summary>
    /// The theme for a raw <c>AppsUseLightTheme</c> registry value: <c>0</c> is dark; anything else —
    /// including a missing value (older Windows, never set) — is light, which is the Windows default.
    /// </summary>
    public static BadgeTheme FromAppsUseLightTheme(object? value) => value is int i && i == 0 ? Dark : Light;

    /// <summary>The theme for the current user's Windows app mode, read from the registry now.</summary>
    public static BadgeTheme Current()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return FromAppsUseLightTheme(key?.GetValue(AppsUseLightThemeValue));
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
        {
            Log.Line($"indicator: could not read the app theme, using light: {e.Message}");
            return Light;
        }
    }
}
