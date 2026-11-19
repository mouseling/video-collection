using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Vcm.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            if (Program.ShotMode)
            {
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(900);      // let the first frame and the DB load settle
                    await window.CaptureAllAsync(Program.ShotDir);
                    desktop.Shutdown();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
