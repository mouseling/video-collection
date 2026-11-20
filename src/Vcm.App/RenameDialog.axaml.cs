using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Vcm.App;

/// <summary>
/// Asks for a new file name. Pre-selects the stem rather than the whole name, so the extension
/// is not wiped by the first keystroke.
/// </summary>
public partial class RenameDialog : Window
{
    /// <summary>The name entered, or null if the dialog was cancelled.</summary>
    public string? NewName { get; private set; }

    // Parameterless ctor for the XAML loader.
    public RenameDialog() : this("") { }

    public RenameDialog(string currentName)
    {
        AvaloniaXamlLoader.Load(this);

        var box = this.FindControl<TextBox>("NameBox")!;
        box.Text = currentName;

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("OkButton")!.Click += (_, _) => Accept(box);

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(box); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };

        Opened += (_, _) =>
        {
            box.Focus();

            // Select the name without the extension — renaming rarely means changing the format.
            var stem = System.IO.Path.GetFileNameWithoutExtension(currentName);
            box.SelectionStart = 0;
            box.SelectionEnd = stem.Length > 0 ? stem.Length : currentName.Length;
        };
    }

    private void Accept(TextBox box)
    {
        var text = box.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        NewName = text;
        Close();
    }
}
