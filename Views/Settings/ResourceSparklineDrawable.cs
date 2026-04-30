using Microsoft.Maui.Graphics;

namespace VirtmaAi.Views.Settings;

public sealed class ResourceSparklineDrawable : IDrawable
{
    public IList<double> Values { get; set; } = new List<double>();
    public double Maximum { get; set; } = 100;
    public Color LineColor { get; set; } = Color.FromArgb("#E10600");
    public Color FillColor { get; set; } = Color.FromArgb("#33E10600");
    public Color GridColor { get; set; } = Color.FromArgb("#2A2A2A");

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var w = dirtyRect.Width;
        var h = dirtyRect.Height;

        canvas.FillColor = Color.FromArgb("#0E0E0E");
        canvas.FillRectangle(dirtyRect);

        canvas.StrokeColor = GridColor;
        canvas.StrokeSize = 1;
        for (int i = 1; i < 4; i++)
        {
            var y = h * (i / 4f);
            canvas.DrawLine(0, y, w, y);
        }

        if (Values.Count < 2 || Maximum <= 0) return;

        var max = Maximum <= 0 ? 1 : Maximum;
        var step = w / Math.Max(Values.Count - 1, 1);

        var path = new PathF();
        path.MoveTo(0, h);
        for (int i = 0; i < Values.Count; i++)
        {
            var v = Math.Clamp(Values[i] / max, 0, 1);
            var x = i * step;
            var y = h - (float)(v * h);
            if (i == 0) path.LineTo((float)x, (float)y);
            else path.LineTo((float)x, (float)y);
        }
        path.LineTo(w, h);
        path.Close();

        canvas.FillColor = FillColor;
        canvas.FillPath(path);

        var line = new PathF();
        for (int i = 0; i < Values.Count; i++)
        {
            var v = Math.Clamp(Values[i] / max, 0, 1);
            var x = i * step;
            var y = h - (float)(v * h);
            if (i == 0) line.MoveTo((float)x, (float)y);
            else line.LineTo((float)x, (float)y);
        }
        canvas.StrokeColor = LineColor;
        canvas.StrokeSize = 2;
        canvas.DrawPath(line);
    }
}
