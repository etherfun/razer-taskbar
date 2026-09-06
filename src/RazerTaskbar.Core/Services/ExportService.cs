// CSV export for the history page (data-capability work). Timestamps are
// written in LOCAL time to match the chart axis; charging/connected use 1/0
// to mirror the samples table storage.

using System.Text;

namespace RazerTaskbar.Core;

public static class ExportService
{
    public const string Header = "timestamp,level,charging,connected";

    /// <summary>Render samples as CSV rows under a header line. Input order
    /// is preserved (callers pass time-ascending samples).</summary>
    public static string ToCsv(IEnumerable<Sample> samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var s in samples)
        {
            sb.Append(FormatStamp(s.Ts)).Append(',')
              .Append(s.Level).Append(',')
              .Append(s.Charging ? '1' : '0').Append(',')
              .Append(s.Connected ? '1' : '0')
              .AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Local-time "yyyy-MM-dd HH:mm:ss" (chart-axis convention).</summary>
    public static string FormatStamp(long ts)
        => DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}
