using Avalonia;
using Vcm.Core;
using Velopack;

namespace Vcm.App;

internal static class Program
{
    public static bool ShotMode;
    public static string ShotDir = "shots";

    [STAThread]
    public static void Main(string[] args)
    {
        // Before anything else that can fail: this is a Windows GUI executable, so without a log
        // file a crash leaves no evidence whatsoever on the user's machine.
        Log.UseDirectory(System.IO.Path.GetDirectoryName(AppPaths.LibraryDbPath)!);
        Log.CatchUnhandled();

        // Must run before anything else. On first launch after an update Velopack uses this to
        // finish installing and then re-launches, so no UI may be created ahead of it. Outside a
        // Velopack deployment it is a no-op, which is the normal case during development.
        try { VelopackApp.Build().Run(); }
        catch (Exception ex)
        {
            // Normal for a dev run. But Run() is also what FINISHES an update, so a failure here
            // on a real deployment must leave a trace rather than looking like a cold start.
            Log.Write("VelopackApp.Run failed (expected outside a Velopack deployment)", ex);
        }

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
