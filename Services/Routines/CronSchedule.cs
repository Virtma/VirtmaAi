namespace VirtmaAi.Services.Routines;

/// <summary>
/// Minimal 5-field cron parser (minute hour day month day-of-week) with * and comma lists.
/// Step values and ranges are supported. Used for Phase 10 routines without pulling a full cron lib.
/// </summary>
public sealed class CronSchedule
{
    private readonly int[][] _fields = new int[5][];
    private static readonly int[][] Ranges =
    {
        new[] { 0, 59 },
        new[] { 0, 23 },
        new[] { 1, 31 },
        new[] { 1, 12 },
        new[] { 0, 6 }
    };

    public CronSchedule(string expression)
    {
        var parts = expression.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) throw new FormatException("cron expression must have 5 fields: " + expression);
        for (int i = 0; i < 5; i++) _fields[i] = ParseField(parts[i], Ranges[i][0], Ranges[i][1]);
    }

    public DateTime? NextAfter(DateTime fromUtc)
    {
        var probe = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, fromUtc.Minute, 0, DateTimeKind.Utc)
            .AddMinutes(1);
        for (int i = 0; i < 60 * 24 * 366; i++)
        {
            if (Matches(probe)) return probe;
            probe = probe.AddMinutes(1);
        }
        return null;
    }

    public bool Matches(DateTime utc)
    {
        if (!_fields[0].Contains(utc.Minute)) return false;
        if (!_fields[1].Contains(utc.Hour)) return false;
        if (!_fields[2].Contains(utc.Day)) return false;
        if (!_fields[3].Contains(utc.Month)) return false;
        if (!_fields[4].Contains((int)utc.DayOfWeek)) return false;
        return true;
    }

    private static int[] ParseField(string field, int min, int max)
    {
        var values = new HashSet<int>();
        foreach (var piece in field.Split(','))
        {
            int step = 1;
            var body = piece;
            if (body.Contains('/'))
            {
                var split = body.Split('/');
                body = split[0];
                step = int.Parse(split[1]);
            }
            int lo, hi;
            if (body == "*") { lo = min; hi = max; }
            else if (body.Contains('-'))
            {
                var split = body.Split('-');
                lo = int.Parse(split[0]);
                hi = int.Parse(split[1]);
            }
            else
            {
                lo = hi = int.Parse(body);
            }
            for (int v = lo; v <= hi; v += step) values.Add(v);
        }
        return values.OrderBy(x => x).ToArray();
    }
}
