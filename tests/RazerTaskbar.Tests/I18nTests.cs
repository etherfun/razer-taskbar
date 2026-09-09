// Port of src/i18n.rs #[cfg(test)] tests.

using System.Text.RegularExpressions;
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
        try
        {
            Assert.Equal("Exit", I18n.Tr("Exit"));
            Assert.Equal("unknown key", I18n.Tr("unknown key"));
            I18n.SetSetting(LanguageSetting.Zh);
            Assert.Equal("退出", I18n.Tr("Exit"));
            Assert.Equal("刷新间隔", I18n.Tr("Poll interval"));
            // Missing key falls back to the English source.
            Assert.Equal("unknown key", I18n.Tr("unknown key"));
        }
        finally
        {
            I18n.SetSetting(LanguageSetting.Auto);
        }
    }

    [Fact]
    public void TranslationSpots()
    {
        I18n.SetSetting(LanguageSetting.Zh);
        try
        {
            Assert.Equal("关机", I18n.Tr("off"));
            Assert.Equal("未找到设备", I18n.Tr("No devices found"));
            Assert.Equal("暂无可导出的数据", I18n.Tr("No data to export"));
        }
        finally
        {
            I18n.SetSetting(LanguageSetting.Auto);
        }
    }

    /// <summary>Locate the repo checkout the test binaries were built from
    /// (tests run via `dotnet test` in the repo, see docs/agent-csharp.md).</summary>
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "build.ps1")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        return dir!;
    }

    private static List<string> SourceFiles(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

    [Fact]
    public void TrCallSitesMatchZhMapBothWays()
    {
        // Two-way guard (supersedes the old three-key spot check):
        //  1. every string literal inside I18n.Tr(...) arguments must be a
        //     zh key — a stray period/qualifier silently falls back to
        //     English in Zh mode (ternaries carry both spellings);
        //  2. every zh key must still appear as a literal somewhere in src/
        //     — dead entries rot the table and mislead the next translator.
        //     Any literal match counts, so the AppendItem→Tr menu
        //     indirection is covered from this side.
        string root = FindRepoRoot();
        var files = SourceFiles(root);
        Assert.NotEmpty(files);

        var sourceTexts = files.Select(File.ReadAllText).ToList();

        // Only the call's argument span (up to the first ')') — rest-of-line
        // noise (log interpolations, raw combo labels) is not Tr's business.
        var args = new Regex("I18n\\.Tr\\(([^)]*)\\)");
        var literal = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"");
        var unmapped = new SortedSet<string>();
        foreach (var text in sourceTexts)
        {
            foreach (var line in text.Split('\n'))
            {
                foreach (Match call in args.Matches(line))
                {
                    foreach (Match m in literal.Matches(call.Groups[1].Value))
                    {
                        // Undo the C# escapes the compiler would resolve
                        // (keys contain \\ and \" at most).
                        string key = Regex.Replace(m.Groups[1].Value, "\\\\(.)", "$1");
                        if (!I18n.ZhMap.ContainsKey(key))
                        {
                            unmapped.Add(key);
                        }
                    }
                }
            }
        }
        Assert.True(unmapped.Count == 0,
            $"I18n.Tr call sites without a zh mapping (silent English in Zh mode): {string.Join(", ", unmapped)}");

        var dead = new SortedSet<string>();
        foreach (var key in I18n.ZhMap.Keys)
        {
            // Re-apply the C# escapes: the source spells `HKCU\\...` for the
            // key `HKCU\...`.
            string sourceForm = key.Replace("\\", "\\\\").Replace("\"", "\\\"");
            bool referenced = sourceTexts.Any(t => t.Contains($"\"{sourceForm}\"", StringComparison.Ordinal));
            if (!referenced)
            {
                dead.Add(key);
            }
        }
        Assert.True(dead.Count == 0,
            $"zh entries no longer referenced from src: {string.Join(", ", dead)}");
    }
}
