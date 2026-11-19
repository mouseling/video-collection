using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Vcm.Prototype;

public partial class App : Application
{
    public static bool ShotMode;
    public static string ShotDir = "shots";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new MainWindow();
            desktop.MainWindow = w;

            if (ShotMode)
            {
                w.Opened += async (_, _) =>
                {
                    await Task.Delay(700);          // let the first frame settle
                    await w.CaptureAllAsync(ShotDir);
                    desktop.Shutdown();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
