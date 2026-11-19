using Avalonia;
using Velopack;

namespace Vcm.App;

internal static class Program
{
    public static bool ShotMode;
    public static string ShotDir = "shots";

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before anything else. On first launch after an update Velopack uses this to
        // finish installing and then re-launches, so no UI may be created ahead of it. Outside a
        // Velopack deployment it is a no-op, which is the normal case during development.
        try { VelopackApp.Build().Run(); }
        catch { /* not deployed by Velopack */ }

        var i = Array.IndexOf(args, "--shot");
        if (i >= 0)
        {
            ShotMode = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) ShotDir = args[i + 1];
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
