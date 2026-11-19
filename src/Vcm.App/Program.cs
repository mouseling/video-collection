using Avalonia;

namespace Vcm.App;

internal static class Program
{
    public static bool ShotMode;
    public static string ShotDir = "shots";

    [STAThread]
    public static void Main(string[] args)
    {
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
