using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Vcm.Prototype;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<VideoItem> _rating = new();
    private readonly ObservableCollection<VideoItem> _viewed = new();

    private Grid[] _pages = Array.Empty<Grid>();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Wire();
    }

    private void Wire()
    {
        _pages = new[]
        {
            this.FindControl<Grid>("PageNowPlaying")!,
            this.FindControl<Grid>("PageLocations")!,
            this.FindControl<Grid>("PageTags")!,
            this.FindControl<Grid>("PageRating")!,
            this.FindControl<Grid>("PageViewed")!,
            this.FindControl<Grid>("PageDupes")!,
        };

        var videos = MockData.Videos();

        this.FindControl<TreeView>("Tree")!.ItemsSource = MockData.Roots();

        this.FindControl<ListBox>("LocList")!.ItemsSource =
            new ObservableCollection<VideoItem>(videos.Where(v => v.Folder.Contains("Reverse")));

        var chips = this.FindControl<ListBox>("TagChips")!;
        chips.ItemsSource = MockData.Tags();
        chips.SelectionChanged += (_, _) =>
        {
            if (chips.SelectedItem is TagItem t) ApplyTag(videos, t);
        };
        chips.SelectedIndex = 0;

        foreach (var v in videos) { _rating.Add(v); _viewed.Add(v); }
        this.FindControl<ListBox>("RateList")!.ItemsSource = _rating;
        this.FindControl<ListBox>("ViewList")!.ItemsSource = _viewed;
        SortRating(v => v.Score);
        SortViewed(v => v.WatchedSec);

        this.FindControl<ItemsControl>("DupeList")!.ItemsSource = MockData.Duplicates();

        var seek = this.FindControl<HeatSeekBar>("Seek")!;
        seek.Heat = MockData.Heat();
        seek.Position = 37.0 * 60 / (89 * 60);

        // nav
        var nav = this.FindControl<ListBox>("Nav")!;
        nav.SelectionChanged += (_, _) => ShowPage(nav.SelectedIndex);
        ShowPage(1);

        // sort switches
        Hook("SortNet",   () => SortRating(v => v.Score));
        Hook("SortRatio", () => SortRating(v => v.Ratio));
        Hook("SortLiked", () => SortRating(v => v.Likes));
        Hook("SortHated", () => SortRating(v => v.Dislikes));
        Hook("SortTime",  () => SortViewed(v => v.WatchedSec));
        Hook("SortViews", () => SortViewed(v => v.Views));
    }

    /// <summary>Tags are single-select: picking one replaces the previous filter.</summary>
    private void ApplyTag(IEnumerable<VideoItem> all, TagItem tag)
    {
        this.FindControl<ListBox>("TagList")!.ItemsSource =
            new ObservableCollection<VideoItem>(all.Where(v => v.Tags.Contains(tag.Name)));
        this.FindControl<TextBlock>("TagHeader")!.Text = $"NAME · tagged “{tag.Name}”";
    }

    private void Hook(string name, Action onChecked)
    {
        var rb = this.FindControl<RadioButton>(name);
        if (rb is not null) rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) onChecked(); };
    }

    private void SortRating<TKey>(Func<VideoItem, TKey> key) => Resort(_rating, key);
    private void SortViewed<TKey>(Func<VideoItem, TKey> key) => Resort(_viewed, key);

    private static void Resort<TKey>(ObservableCollection<VideoItem> list, Func<VideoItem, TKey> key)
    {
        var sorted = list.OrderByDescending(key).ToList();
        list.Clear();
        foreach (var v in sorted) list.Add(v);
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].IsVisible = i == index;
    }

    /// <summary>Renders every page to a PNG so the UI can be reviewed without a live session.</summary>
    public async Task CaptureAllAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var names = new[] { "now-playing", "locations", "tags", "rating", "most-viewed", "duplicates" };
        var nav = this.FindControl<ListBox>("Nav")!;

        for (var i = 0; i < names.Length; i++)
        {
            nav.SelectedIndex = i;
            ShowPage(i);
            await Task.Delay(350);
            Dispatcher.UIThread.RunJobs();

            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(this);
            var path = Path.Combine(dir, $"{i + 1:00}-{names[i]}.png");
            rtb.Save(path);
            Console.WriteLine($"wrote {path}");
        }
    }
}
