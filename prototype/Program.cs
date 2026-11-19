using Avalonia;

namespace Vcm.Prototype;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--shot"))
        {
            App.ShotMode = true;
            var i = Array.IndexOf(args, "--shot");
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) App.ShotDir = args[i + 1];
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
