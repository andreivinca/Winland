using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winland.Indicator.Tests;

[TestClass]
public sealed class BadgeThemeTests
{
    [TestMethod]
    public void AppsUseLightThemeZero_IsDark()
    {
        Assert.AreEqual(BadgeTheme.Dark, BadgeTheme.FromAppsUseLightTheme(0));
    }

    [TestMethod]
    public void AppsUseLightThemeOne_IsLight()
    {
        Assert.AreEqual(BadgeTheme.Light, BadgeTheme.FromAppsUseLightTheme(1));
    }

    [TestMethod]
    public void MissingOrUnreadableValue_FallsBackToLight_TheWindowsDefault()
    {
        Assert.AreEqual(BadgeTheme.Light, BadgeTheme.FromAppsUseLightTheme(null));
        Assert.AreEqual(BadgeTheme.Light, BadgeTheme.FromAppsUseLightTheme("0"));
    }

    [TestMethod]
    public void DarkBadge_IsBlackWithWhiteItems()
    {
        BadgeTheme dark = BadgeTheme.Dark;

        Assert.AreEqual(Color.Black.ToArgb() & 0xFFFFFF, dark.Backing.ToArgb() & 0xFFFFFF);
        Assert.AreEqual(Color.White.ToArgb() & 0xFFFFFF, dark.Ink.ToArgb() & 0xFFFFFF);
    }

    [TestMethod]
    public void DarkIsTheInverseOfLight_AtTheSameTransparency()
    {
        BadgeTheme light = BadgeTheme.Light;
        BadgeTheme dark = BadgeTheme.Dark;

        Assert.AreEqual(light.Backing.A, dark.Backing.A);
        Assert.AreEqual(light.Ink.A, dark.Ink.A);
        Assert.AreEqual(light.Backing.ToArgb() & 0xFFFFFF, dark.Ink.ToArgb() & 0xFFFFFF);
        Assert.AreEqual(light.Ink.ToArgb() & 0xFFFFFF, dark.Backing.ToArgb() & 0xFFFFFF);
    }
}
