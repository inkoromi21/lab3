using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace WpfApp2;

public sealed class ServiceException(string message) : Exception(message);

public sealed class ChartServiceClient
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://image-charts.com/"), Timeout = TimeSpan.FromSeconds(12) };

    public async Task<byte[]> RenderAsync(double a, double b, double?[] values, double? root, CancellationToken cancellationToken)
    {
        if (values.Length < 2) throw new ServiceException("Недостаточно точек для графика.");
        double[] finite = values.Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToArray();
        if (finite.Length == 0) throw new ServiceException("Функция не определена на графике.");
        double lower = finite[(int)((finite.Length - 1) * 0.05)];
        double upper = finite[(int)((finite.Length - 1) * 0.90)];
        double spread = upper - lower;
        double padding = spread > 0 ? spread * 0.08 : Math.Max(Math.Abs(lower) * 0.1, 0.0001);
        double yMin = Math.Min(0, lower) - padding;
        double yMax = Math.Max(0, upper) + padding;
        if (!double.IsFinite(yMin) || !double.IsFinite(yMax) || yMin >= yMax)
            throw new ServiceException("Слишком большие значения для графика.");
        double xLabelStep = NiceStep((b - a) / 14);
        double yLabelStep = NiceStep((yMax - yMin) / 14);
        yMin = Math.Floor(yMin / yLabelStep) * yLabelStep;
        yMax = Math.Ceiling(yMax / yLabelStep) * yLabelStep;
        double xMinorStep = NiceStep((b - a) / 40);
        double yMinorStep = NiceStep((yMax - yMin) / 40);
        double cutoff = Math.Max(Math.Abs(yMin), Math.Abs(yMax)) * 5;
        if (!double.IsFinite(cutoff)) cutoff = double.MaxValue;

        var datasets = new List<object>
        {
            new { data = new[] { new { x = a, y = 0d }, new { x = b, y = 0d } },
                  showLine = true, fill = false, borderColor = "#16804a", borderWidth = 2, pointRadius = 0 }
        };
        if (a <= 0 && b >= 0)
            datasets.Add(new { data = new[] { new { x = 0d, y = yMin }, new { x = 0d, y = yMax } },
                               showLine = true, fill = false, borderColor = "#16804a", borderWidth = 2, pointRadius = 0 });

        AddMarks(datasets, a, b, 1, horizontal: true, major: true);
        AddMarks(datasets, a, b, xMinorStep, horizontal: true, major: false);
        if (a <= 0 && b >= 0)
        {
            AddMarks(datasets, yMin, yMax, 1, horizontal: false, major: true);
            AddMarks(datasets, yMin, yMax, yMinorStep, horizontal: false, major: false);
        }

        // Separate datasets prevent a line from crossing a gap or a pole.
        var segment = new List<object>();
        void AddSegment()
        {
            if (segment.Count == 0) return;
            datasets.Add(new { data = segment.ToArray(), showLine = segment.Count > 1, fill = false,
                               borderColor = "#2563eb", borderWidth = 2, pointRadius = segment.Count == 1 ? 2 : 0,
                               lineTension = 0 });
            segment.Clear();
        }
        for (int i = 0; i < values.Length; i++)
        {
            double? y = values[i];
            if (y is null || Math.Abs(y.Value) > cutoff) { AddSegment(); continue; }
            double x = i == values.Length - 1 ? b : a + (b - a) * i / (values.Length - 1);
            segment.Add(new { x, y = y.Value });
        }
        AddSegment();

        if (root is not null)
            datasets.Add(new { data = new[] { new { x = root.Value, y = 0d } },
                               showLine = false, pointRadius = 7, pointBackgroundColor = "#dc2626" });

        var chart = new
        {
            type = "scatter",
            data = new { datasets },
            options = new
            {
                animation = false,
                legend = new { display = false },
                scales = new
                {
                    xAxes = new[] { new { type = "linear", ticks = new { min = a, max = b, stepSize = xLabelStep },
                                          scaleLabel = new { display = true, labelString = "X" } } },
                    yAxes = new[] { new { ticks = new { min = yMin, max = yMax, stepSize = yLabelStep },
                                          scaleLabel = new { display = true, labelString = "Y" } } }
                }
            }
        };

        string url = "chart.js/2.8.0?width=1200&height=800&bkg=white&c=" +
                     Uri.EscapeDataString(JsonSerializer.Serialize(chart));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, cancellationToken); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new ServiceException("Сервис графиков не ответил."); }
        catch (HttpRequestException)
        { throw new ServiceException("Нет связи с сервисом графиков."); }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ServiceException("Лимит запросов. Повторите позже.");
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentType?.MediaType is not "image/png")
                throw new ServiceException("Сервис не построил график.");
            byte[] image = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (image.Length < 100 || image.Length > 8_000_000)
                throw new ServiceException("Неверный ответ сервиса графиков.");
            return image;
        }
    }

    private static void AddMarks(List<object> datasets, double min, double max, double step,
                                 bool horizontal, bool major)
    {
        if (!double.IsFinite(step) || step <= 0 || (max - min) / step > 160) return;
        var marks = new List<object>();
        double first = Math.Ceiling(min / step);
        for (int i = 0; i <= 160; i++)
        {
            double value = (first + i) * step;
            if (value > max + step * 1e-8) break;
            if (value < min - step * 1e-8) continue;
            if (!major && Math.Abs(value - Math.Round(value)) <= step * 1e-6) continue;
            marks.Add(horizontal ? new { x = value, y = 0d } : new { x = 0d, y = value });
        }
        if (marks.Count == 0) return;
        datasets.Add(new { data = marks, showLine = false, pointStyle = "line",
                           rotation = horizontal ? 90 : 0, pointRadius = major ? 5 : 3,
                           pointBorderWidth = major ? 2 : 1, pointBorderColor = "#16804a" });
    }

    private static double NiceStep(double raw)
    {
        if (!double.IsFinite(raw) || raw <= 0) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double multiple in new[] { 1d, 2d, 5d, 10d })
            if (multiple * power >= raw) return multiple * power;
        return 10 * power;
    }
}
