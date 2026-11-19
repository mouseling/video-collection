using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Vcm.Core;

namespace Vcm.App;

/// <summary>
/// Adds a root folder. Offers both a text box and a browse button, because pasting a path is
/// usually faster than clicking through a picker when the path is already known.
/// </summary>
public partial class AddPathDialog : Window
{
    private readonly Library? _library;

    /// <summary>The root that was added, or null if the dialog was cancelled.</summary>
    public Root? Result { get; private set; }

    // Parameterless ctor for the XAML loader.
    public AddPathDialog() : this(null) { }

    public AddPathDialog(Library? library)
    {
        _library = library;
        AvaloniaXamlLoader.Load(this);

        var pathBox = this.FindControl<TextBox>("PathBox")!;
        var error = this.FindControl<TextBlock>("ErrorText")!;

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();

        this.FindControl<Button>("BrowseButton")!.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a folder containing videos",
                AllowMultiple = false,
            });

            var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path)) pathBox.Text = path;
        };

        this.FindControl<Button>("AddButton")!.Click += (_, _) => TryAdd(pathBox, error);
        pathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) TryAdd(pathBox, error);
        };

        Opened += (_, _) => pathBox.Focus();
    }

    private void TryAdd(TextBox pathBox, TextBlock error)
    {
        var text = pathBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            Show(error, "Enter a folder path.");
            return;
        }

        // Paths pasted from a file manager often arrive wrapped in quotes.
        text = text.Trim('"', '\'');

        if (_library is null) { Close(); return; }

        var root = _library.AddRoot(text, out var failure);
        if (root is null)
        {
            Show(error, failure ?? "Could not add that folder.");
            return;
        }

        Result = root;
        Close();
    }

    private static void Show(TextBlock error, string message)
    {
        error.Text = message;
        error.IsVisible = true;
    }
}
