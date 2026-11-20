namespace Vcm.Core;

/// <summary>
/// Durations and sizes as the product displays them.
///
/// In Core rather than the UI project because two different front ends render the same values —
/// the Avalonia grids and the CLI that exists to verify them. When these were written twice they
/// had already drifted: the UI showed an unprobed video as "—" while the verification tool showed
/// the same row as "0:00".
/// </summary>
public static class Fmt
{
    /// <summary>h:mm:ss above an hour, m:ss below. Zero means "not known yet", not "zero long".</summary>
    public static string Time(long ms)
    {
        if (ms <= 0) return "—";

        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
