// Config load/save contract: defaults on missing file, round-trip fidelity,
// and the atomic save (a crashed write must never leave a half settings.json
// that loads as defaults — the next Save would then persist the defaults).

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

[Collection("I18nSequential")]
public sealed class ConfigTests
{
    private static string WithScratchAppData(out string oldAppData)
    {
        var saved = Environment.GetEnvironmentVariable("APPDATA");
        var scratch = Path.Combine(Path.GetTempPath(), "razer-taskbar-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("APPDATA", scratch);
        oldAppData = saved;
        return scratch;
    }

    [Fact]
    public void MissingFileLoadsDefaults()
    {
        var scratch = WithScratchAppData(out var old);
        try
        {
            var cfg = ConfigService.Load();
            Assert.Equal(15ul, cfg.PollingThrottleSecs);
            Assert.Equal("fixed", cfg.DisplayMode);
            Assert.Equal("auto", cfg.BatterySource);
            Assert.True(cfg.RecordBatteryHistory);
            Assert.True(cfg.ShowEstimatedTime);
            Assert.False(cfg.ColorBatteryIcon);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SaveLoadRoundTripLeavesNoTempFile()
    {
        var scratch = WithScratchAppData(out var old);
        try
        {
            var cfg = new Config
            {
                PollingThrottleSecs = 42,
                ShownDeviceHandle = "SI-123",
                DisplayMode = "rotate",
                RotateIntervalSecs = 17,
                WidgetSide = "left",
                EmbedIntoWidgetsSpace = true,
                ColorBatteryIcon = true,
                HistoryPollIntervalSecs = 7,
                Language = "zh",
            };
            ConfigService.Save(cfg);
            Assert.True(File.Exists(ConfigService.ConfigPath));
            // The atomic replace must not strand its staging file.
            Assert.False(File.Exists(ConfigService.ConfigPath + ".tmp"));

            var loaded = ConfigService.Load();
            Assert.Equal(42ul, loaded.PollingThrottleSecs);
            Assert.Equal("SI-123", loaded.ShownDeviceHandle);
            Assert.Equal("rotate", loaded.DisplayMode);
            Assert.Equal(17ul, loaded.RotateIntervalSecs);
            Assert.Equal("left", loaded.WidgetSide);
            Assert.True(loaded.EmbedIntoWidgetsSpace);
            Assert.True(loaded.ColorBatteryIcon);
            Assert.Equal(7ul, loaded.HistoryPollIntervalSecs);
            Assert.Equal("zh", loaded.Language);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void BatteryTypeOverridesLoadAndFilterUnknownSpellings()
    {
        var scratch = WithScratchAppData(out var old);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigService.ConfigPath)!);
            // Legacy file: the key does not exist yet, and a hand-edited file
            // may spell it as null. Both must load as an empty (never null)
            // lookup.
            File.WriteAllText(ConfigService.ConfigPath, "{\"polling_throttle_secs\": 9}");
            var legacy = ConfigService.Load();
            Assert.Equal(9ul, legacy.PollingThrottleSecs);
            Assert.Empty(legacy.DeviceBatteryTypes);

            File.WriteAllText(ConfigService.ConfigPath, "{\"device_battery_types\": null}");
            Assert.Empty(ConfigService.Load().DeviceBatteryTypes);

            // Round trip, with one unreadable value dropped on load (Parse
            // would read it back as auto, so keeping it only hides the real
            // setting from the history page).
            var cfg = ConfigService.Load();
            cfg.DeviceBatteryTypes["HID:1532:0094"] = BatteryType.Replaceable.ToConfig();
            cfg.DeviceBatteryTypes["SI-1"] = "bogus";
            ConfigService.Save(cfg);

            var loaded = ConfigService.Load();
            Assert.Equal(
                BatteryType.Replaceable,
                BatteryTypes.Parse(loaded.DeviceBatteryTypes["HID:1532:0094"]));
            Assert.False(loaded.DeviceBatteryTypes.ContainsKey("SI-1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }
}
