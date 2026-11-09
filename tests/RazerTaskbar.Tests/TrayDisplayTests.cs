// Tray icon display style: the pure parts of `tray_display_mode` (tolerant
// parsing, canonical spelling, the digits/"--" label). The actual GDI rendering
// lives in the WinUI exe (Native/TrayIcon.cs), which the tests deliberately do
// not reference — same split as DisplayMode/BatteryType.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class TrayDisplayTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("icon")]
    [InlineData("ICON")]
    [InlineData(" Icon ")]
    [InlineData("bogus")]
    [InlineData("数字")]
    public void ParseFallsBackToTheBatteryGlyph(string? value)
        => Assert.Equal(TrayDisplayMode.Icon, TrayDisplay.Parse(value));

    [Theory]
    [InlineData("number")]
    [InlineData("NUMBER")]
    [InlineData("Number")]
    [InlineData(" number ")]
    public void ParseAcceptsTheNumberStyleTolerantly(string value)
        => Assert.Equal(TrayDisplayMode.Number, TrayDisplay.Parse(value));

    [Fact]
    public void ToConfigIsCanonicalAndRoundTrips()
    {
        Assert.Equal("icon", TrayDisplayMode.Icon.ToConfig());
        Assert.Equal("number", TrayDisplayMode.Number.ToConfig());
        foreach (var mode in new[] { TrayDisplayMode.Icon, TrayDisplayMode.Number })
        {
            Assert.Equal(mode, TrayDisplay.Parse(mode.ToConfig()));
        }
    }

    [Theory]
    [InlineData(null, "--")]
    [InlineData(0, "0")]
    [InlineData(7, "7")]
    [InlineData(29, "29")]
    [InlineData(100, "100")]
    [InlineData(150, "100")]
    [InlineData(-3, "0")]
    public void DisplayTextClampsToThreeDigitsOrDashes(int? level, string expected)
        => Assert.Equal(expected, TrayDisplay.DisplayText(level));

    [Fact]
    public void NewConfigDefaultsToTheBatteryGlyph()
    {
        Assert.Equal("icon", new Config().TrayDisplayMode);
        Assert.Equal(TrayDisplayMode.Icon, TrayDisplay.Parse(new Config().TrayDisplayMode));
    }
}
