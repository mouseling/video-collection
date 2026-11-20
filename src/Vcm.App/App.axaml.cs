using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Vcm.Core;

namespace Vcm.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Database db;
            try
            {
                db = new Database(AppPaths.LibraryDbPath);
                db.BackupIfStale(TimeSpan.FromDays(7));
            }
            catch (Exception ex)
            {
                // Opening the library used to happen inside the window constructor, so a locked,
                // corrupt or unreachable database killed the process before any window existed —
                // the user saw the application simply not start. Say what failed instead.
                Log.Write("could not open the library", ex);
                desktop.MainWindow = FailureWindow(ex);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var window = new MainWindow(db);
            desktop.MainWindow = window;

            // Teardown is driven from here rather than only from Window.Closing, so the open
            // viewing session is persisted on every exit path.
            desktop.ShutdownRequested += (_, _) => window.ShutDown();

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

    /// <summary>Shown when the library cannot be opened at all.</summary>
    private static Window FailureWindow(Exception error) => new()
    {
        Title = "Video Collection Manager",
        Width = 620,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x14)),
        FontFamily = new FontFamily("Inter, Segoe UI, sans-serif"),
        Content = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 26),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "The video library could not be opened",
                    FontSize = 17,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE9, 0xEF)),
                },
                new TextBlock
                {
                    Text = error.Message,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0x6E, 0x6E)),
                },
                new TextBlock
                {
                    Text = AppPaths.LibraryDbPath,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA6)),
                },
                new TextBlock
                {
                    Text = "If another copy of the application is running, close it and try again. "
                         + "A recent backup may be available alongside the file above, named "
                         + "library.db.bak.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA6)),
                },
            },
        },
    };
}
