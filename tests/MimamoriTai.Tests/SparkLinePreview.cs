using System.Globalization;
using System.Text;
using MimamoriTai.Web.Charts;

namespace MimamoriTai.Tests;

/// <summary>
/// Renders the plug telemetry charts to a standalone HTML file so the layout can be
/// looked at, using the real geometry rather than a re-implementation of it.
///
/// Skipped by default: it is a development aid, not an assertion. Remove the Skip to
/// regenerate the preview after changing SparkLineGeometry or the chart CSS.
/// </summary>
public class SparkLinePreview
{
    [Fact(Skip = "Development aid: renders a preview, asserts nothing.")]
    public void Render()
    {
        var start = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(9));
        var rand = new Random(7);

        List<SparkPoint> watts = [], volts = [], amps = [];

        for (var i = 0; i < 288; i++)
        {
            // A two-hour poller outage mid-afternoon, to prove the line breaks.
            if (i is >= 60 and < 84)
            {
                continue;
            }

            var at = start.AddMinutes(i * 5);
            var w = at.Hour switch
            {
                >= 18 and < 22 => 78 + (rand.NextDouble() * 6),
                >= 12 and < 13 => 22 + (rand.NextDouble() * 3),
                _ => 0.3
            };

            if (i is 132 or 133)
            {
                w = 1180 + (rand.NextDouble() * 40);
            }

            var v = 103.4 + ((rand.NextDouble() - 0.5) * 2.2);
            var a = w < 1 ? 300 + (rand.NextDouble() * 40) : w / v * 1000 * 1.15;

            watts.Add(new SparkPoint(at, w));
            volts.Add(new SparkPoint(at, v));
            amps.Add(new SparkPoint(at, a));
        }

        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MimamoriTai.Web", "wwwroot", "app.css"));

        var body = new StringBuilder();
        body.Append(Figure(watts, "消費電力", "W", 1));
        body.Append(Figure(amps, "電流", "mA", 0));
        body.Append(Figure(volts, "電圧", "V", 1));

        var html = $$"""
            <!doctype html><html lang="ja"><head><meta charset="utf-8"><style>
            {{css}}
            body { background: #f6f7f9; padding: 20px; font-family: system-ui, sans-serif; }
            .wrap { max-width: 420px; margin: 0 auto; background: #fff; border-radius: 12px;
                    padding: 16px; border: 1px solid #e5e7eb; }
            h2 { font-size: 14px; margin: 0 0 12px; }
            </style></head><body><div class="wrap">
            <h2>電力・電圧・電流の推移 <span style="font-weight:400;color:#6b7280">直近24時間</span></h2>
            {{body}}
            <p class="chart-note">消費電力と電流は家電の使われ方、電圧は電力会社から届いている電気の状態を表します。電圧はほとんど変わらないのが正常です。</p>
            </div></body></html>
            """;

        var outPath = Path.Combine(Path.GetTempPath(), "sparkline-preview.html");
        File.WriteAllText(outPath, html, Encoding.UTF8);
    }

    private static string Figure(List<SparkPoint> points, string caption, string unit, int decimals)
    {
        var (min, max) = SparkLineGeometry.Range(points);
        var from = points.Min(p => p.At);
        var to = points.Max(p => p.At);
        var fmt = $"F{decimals}";

        var poly = string.Join("\n", SparkLineGeometry.Segments(points)
            .Select(s => $"""<polyline points="{s}" class="spark-line-path" />"""));

        var dots = string.Join("\n", SparkLineGeometry.Isolated(points).Select(d =>
        {
            var x = SparkLineGeometry.F(SparkLineGeometry.X(d.At, from, to));
            var y = SparkLineGeometry.Y(d.Value, min, max);
            return $"""<line x1="{x}" y1="{SparkLineGeometry.F(y - 1)}" x2="{x}" y2="{SparkLineGeometry.F(y + 1)}" class="spark-line-dot" />""";
        }));

        return $"""
            <figure class="spark-line device-spark">
              <figcaption>
                <span class="spark-line-title">{caption}</span>
                <span class="spark-line-current">{points[^1].Value.ToString(fmt, CultureInfo.InvariantCulture)} {unit}</span>
              </figcaption>
              <div class="spark-line-plot">
                <p class="spark-line-scale" aria-hidden="true">
                  <span>{max.ToString(fmt, CultureInfo.InvariantCulture)}{unit}</span>
                  <span>{min.ToString(fmt, CultureInfo.InvariantCulture)}{unit}</span>
                </p>
                <svg viewBox="0 0 {SparkLineGeometry.F(SparkLineGeometry.ViewWidth)} {SparkLineGeometry.F(SparkLineGeometry.ViewHeight)}" preserveAspectRatio="none">
                  <line x1="0" y1="{SparkLineGeometry.F(SparkLineGeometry.PlotBottom)}" x2="{SparkLineGeometry.F(SparkLineGeometry.ViewWidth)}" y2="{SparkLineGeometry.F(SparkLineGeometry.PlotBottom)}" class="spark-line-axis" />
                  {poly}
                  {dots}
                </svg>
              </div>
              <p class="spark-line-span" aria-hidden="true">
                <span>{from:M/d HH:mm}</span>
                <span>{to:M/d HH:mm}</span>
              </p>
            </figure>
            """;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MimamoriTai.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

