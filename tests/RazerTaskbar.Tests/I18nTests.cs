// Port of src/i18n.rs #[cfg(test)] tests.

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

[Collection("I18nSequential")]
public sealed class I18nTests
{
    [Fact]
    public void TranslationBasics()
    {
        I18n.SetSetting(LanguageSetting.En);
        Assert.Equal("Exit", I18n.Tr("Exit"));
        Assert.Equal("unknown key", I18n.Tr("unknown key"));
        I18n.SetSetting(LanguageSetting.Zh);
        Assert.Equal("退出", I18n.Tr("Exit"));
        Assert.Equal("刷新间隔", I18n.Tr("Poll interval"));
        // Missing key falls back to the English source.
        Assert.Equal("unknown key", I18n.Tr("unknown key"));
        // Restore the default so other tests are unaffected.
        I18n.SetSetting(LanguageSetting.Auto);
    }
}
