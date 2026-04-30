using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using VirtmaAi.Services.Media;

namespace VirtmaAi.Views.Chat;

public sealed class ChartView : SKCanvasView
{
    public static readonly BindableProperty ChartProperty = BindableProperty.Create(
        nameof(Chart), typeof(ChartDefinition), typeof(ChartView), propertyChanged: OnChartChanged);

    public ChartDefinition? Chart
    {
        get => (ChartDefinition?)GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    public ChartView()
    {
        HeightRequest = 240;
        BackgroundColor = Colors.Transparent;
    }

    private static void OnChartChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is ChartView v) v.InvalidateSurface();
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var info = e.Info;
        if (Chart is null || Chart.Data.Count == 0)
        {
            DrawPlaceholder(canvas, info);
            return;
        }

        var padding = new SKRect(56, 28, info.Width - 16, info.Height - 40);
        DrawTitle(canvas, info);
        DrawAxesAndGrid(canvas, padding, out var min, out var max, out var categories);

        switch (Chart.Type?.ToLowerInvariant())
        {
            case "line": DrawLine(canvas, padding, min, max, categories); break;
            case "scatter": DrawScatter(canvas, padding, min, max, categories); break;
            default: DrawBar(canvas, padding, min, max, categories); break;
        }
    }

    private void DrawPlaceholder(SKCanvas canvas, SKImageInfo info)
    {
        using var font = new SKFont { Size = 14 };
        using var paint = new SKPaint { Color = SKColors.Gray, IsAntialias = true };
        canvas.DrawText("(no chart data)", info.Width / 2f, info.Height / 2f, SKTextAlign.Center, font, paint);
    }

    private void DrawTitle(SKCanvas canvas, SKImageInfo info)
    {
        if (string.IsNullOrWhiteSpace(Chart!.Title)) return;
        using var font = new SKFont { Size = 14 };
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(Chart.Title, info.Width / 2f, 20, SKTextAlign.Center, font, paint);
    }

    private void DrawAxesAndGrid(SKCanvas canvas, SKRect r, out double min, out double max, out List<string> categories)
    {
        var allValues = Chart!.Data.SelectMany(s => s.Values).ToList();
        min = allValues.Count > 0 ? Math.Min(0, allValues.Min()) : 0;
        max = allValues.Count > 0 ? allValues.Max() : 1;
        if (Math.Abs(max - min) < 1e-9) max = min + 1;

        categories = Chart.Data.FirstOrDefault()?.Categories ?? new List<string>();
        var maxCount = Chart.Data.Max(s => s.Values.Count);
        while (categories.Count < maxCount) categories.Add((categories.Count + 1).ToString());

        using var axis = new SKPaint { Color = new SKColor(0x6E, 0x6E, 0x6E), StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(r.Left, r.Top, r.Left, r.Bottom, axis);
        canvas.DrawLine(r.Left, r.Bottom, r.Right, r.Bottom, axis);

        using var grid = new SKPaint { Color = new SKColor(0x2E, 0x2E, 0x2E), StrokeWidth = 1, IsAntialias = false };
        using var label = new SKPaint { Color = new SKColor(0xAA, 0xAA, 0xAA), IsAntialias = true };
        using var font = new SKFont { Size = 10 };
        int ticks = 4;
        for (int i = 0; i <= ticks; i++)
        {
            var t = i / (double)ticks;
            var y = (float)(r.Bottom - t * r.Height);
            canvas.DrawLine(r.Left, y, r.Right, y, grid);
            var v = min + t * (max - min);
            canvas.DrawText(v.ToString("0.##"), r.Left - 6, y + 4, SKTextAlign.Right, font, label);
        }
    }

    private void DrawBar(SKCanvas canvas, SKRect r, double min, double max, List<string> categories)
    {
        var series = Chart!.Data;
        var groups = categories.Count;
        if (groups == 0) return;
        var groupWidth = r.Width / groups;
        var barWidth = groupWidth / (series.Count + 1);

        using var font = new SKFont { Size = 10 };
        using var labelPaint = new SKPaint { Color = new SKColor(0xAA, 0xAA, 0xAA), IsAntialias = true };

        for (int g = 0; g < groups; g++)
        {
            for (int s = 0; s < series.Count; s++)
            {
                var ser = series[s];
                if (g >= ser.Values.Count) continue;
                var v = ser.Values[g];
                var normalized = (v - min) / (max - min);
                var barHeight = (float)(normalized * r.Height);
                var x = r.Left + g * groupWidth + s * barWidth + barWidth * 0.25f;
                var y = r.Bottom - barHeight;
                using var paint = new SKPaint { Color = ParseColor(ser.Color, s), IsAntialias = true };
                canvas.DrawRect(new SKRect(x, y, x + barWidth * 0.75f, r.Bottom), paint);
            }
            var cx = r.Left + (g + 0.5f) * groupWidth;
            canvas.DrawText(categories[g], cx, r.Bottom + 14, SKTextAlign.Center, font, labelPaint);
        }
    }

    private void DrawLine(SKCanvas canvas, SKRect r, double min, double max, List<string> categories)
    {
        for (int s = 0; s < Chart!.Data.Count; s++)
        {
            var ser = Chart.Data[s];
            if (ser.Values.Count < 2) continue;
            using var paint = new SKPaint { Color = ParseColor(ser.Color, s), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var path = new SKPath();
            for (int i = 0; i < ser.Values.Count; i++)
            {
                var x = r.Left + (i / (float)(ser.Values.Count - 1)) * r.Width;
                var y = (float)(r.Bottom - ((ser.Values[i] - min) / (max - min)) * r.Height);
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            canvas.DrawPath(path, paint);
        }
        DrawCategoryAxis(canvas, r, categories);
    }

    private void DrawScatter(SKCanvas canvas, SKRect r, double min, double max, List<string> categories)
    {
        for (int s = 0; s < Chart!.Data.Count; s++)
        {
            var ser = Chart.Data[s];
            using var paint = new SKPaint { Color = ParseColor(ser.Color, s), IsAntialias = true };
            for (int i = 0; i < ser.Values.Count; i++)
            {
                var count = Math.Max(ser.Values.Count, 1);
                var x = r.Left + (i / (float)count) * r.Width + r.Width / (2f * count);
                var y = (float)(r.Bottom - ((ser.Values[i] - min) / (max - min)) * r.Height);
                canvas.DrawCircle(x, y, 4, paint);
            }
        }
        DrawCategoryAxis(canvas, r, categories);
    }

    private static void DrawCategoryAxis(SKCanvas canvas, SKRect r, List<string> categories)
    {
        if (categories.Count == 0) return;
        using var font = new SKFont { Size = 10 };
        using var paint = new SKPaint { Color = new SKColor(0xAA, 0xAA, 0xAA), IsAntialias = true };
        int step = Math.Max(1, categories.Count / 8);
        for (int i = 0; i < categories.Count; i += step)
        {
            var count = Math.Max(categories.Count - 1, 1);
            var x = r.Left + (i / (float)count) * r.Width;
            canvas.DrawText(categories[i], x, r.Bottom + 14, SKTextAlign.Center, font, paint);
        }
    }

    private static SKColor ParseColor(string? hex, int seriesIndex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var c)) return c;
        var palette = new[]
        {
            new SKColor(0xE1, 0x06, 0x00),
            new SKColor(0x35, 0xB4, 0xF0),
            new SKColor(0x69, 0xDC, 0x7C),
            new SKColor(0xF3, 0xBA, 0x2F),
            new SKColor(0xA9, 0x66, 0xFF),
            new SKColor(0xFF, 0x8A, 0x5B)
        };
        return palette[seriesIndex % palette.Length];
    }
}
