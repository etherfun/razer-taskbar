// Tests for the history-page CSV export (P1 data capability).

using RazerTaskbar.Core;
using Xunit;

namespace RazerTaskbar.Tests;

public sealed class ExportServiceTests
{
    private static string[] Lines(string csv)
        => csv.Replace(Environment.NewLine, "\n").TrimEnd('\n').Split('\n');

    [Fact]
    public void EmptySeriesProducesHeaderOnly()
    {
        var csv = ExportService.ToCsv(Array.Empty<Sample>());
        Assert.Equal(ExportService.Header + Environment.NewLine, csv);
        Assert.Single(Lines(csv));
    }

    [Fact]
    public void RowsMirrorSampleStorage()
    {
        var samples = new[]
        {
            new Sample(0, 100, false, true),
            new Sample(3600, 90, false, true),
            new Sample(3660, 90, true, false),
        };
        var lines = Lines(ExportService.ToCsv(samples));
        Assert.Equal(4, lines.Length);
        Assert.Equal(ExportService.Header, lines[0]);
        // Timestamps local-time "yyyy-MM-dd HH:mm:ss"; charging/connected as 1/0; order preserved.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},100,0,1$", lines[1]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},90,0,1$", lines[2]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},90,1,0$", lines[3]);
    }

    [Fact]
    public void LevelPassesThroughUnchanged()
    {
        var csv = ExportService.ToCsv([new Sample(0, 7, true, true)]);
        Assert.EndsWith(",7,1,1" + Environment.NewLine, csv, StringComparison.Ordinal);
    }
}
