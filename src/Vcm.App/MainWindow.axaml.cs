using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using Vcm.Core;
using Vcm.Media;

namespace Vcm.App;

public partial class MainWindow : Window
{
    private readonly Database _db;
    private readonly Library _library;
    private PlayerEngine? _engine;

    private readonly ObservableCollection<FolderRow> _tree = new();
    private readonly ObservableCollection<VideoRow> _locItems = new();
    private readonly ObservableCollection<VideoRow> _tagItems = new();
    private readonly ObservableCollection<VideoRow> _rateItems = new();
    private readonly ObservableCollection<VideoRow> _viewItems = new();
    private readonly ObservableCollection<TagRow> _tagChips = new();
    private readonly ObservableCollection<DuplicateRow> _dupes = new();

    private Grid[] _pages = [];
    private FolderRow? _selectedFolder;
    private CancellationTokenSource? _scanCts;
    private Updater? _updater;
    private const int HeatBuckets = 240;

    public MainWindow()
    {
        _db = new Database(AppPaths.LibraryDbPath);
        _library = new Library(_db);

        AvaloniaXamlLoader.Load(this);
        Wire();
        StartEngine();
        RefreshAll();
        StartUpdateCheck();
    }

    // ------------------------------------------------------------------ setup

    private void Wire()
    {
        _pages =
        [
            C<Grid>("PageNowPlaying"), C<Grid>("PageLocations"), C<Grid>("PageTags"),
            C<Grid>("PageRating"), C<Grid>("PageViewed"), C<Grid>("PageDupes"),
        ];

        var nav = C<ListBox>("Nav");
        nav.SelectionChanged += (_, _) => ShowPage(nav.SelectedIndex);
        ShowPage(1);

        C<TreeView>("Tree").ItemsSource = _tree;
        C<ListBox>("LocList").ItemsSource = _locItems;
        C<ListBox>("TagList").ItemsSource = _tagItems;
        C<ListBox>("RateList").ItemsSource = _rateItems;
        C<ListBox>("ViewList").ItemsSource = _viewItems;
        C<ListBox>("TagChips").ItemsSource = _tagChips;
        C<ItemsControl>("DupeList").ItemsSource = _dupes;

        C<Button>("UpdateButton").Click += (_, _) => ApplyUpdate();
        C<Button>("AddPathButton").Click += OnAddPath;
        C<Button>("RescanButton").Click += (_, _) => StartScan();
        C<Button>("RemoveRootButton").Click += OnRemoveRoot;
        C<Button>("PlayFolderButton").Click += (_, _) => PlayFromList(_locItems, 0, QueueSource.Folder,
                                                                     _selectedFolder?.Name ?? "folder");

        var tree = C<TreeView>("Tree");
        tree.SelectionChanged += (_, _) =>
        {
            _selectedFolder = tree.SelectedItem as FolderRow;
            LoadFolder();
        };

        C<CheckBox>("RecursiveToggle").IsCheckedChanged += (_, _) => LoadFolder();

        var chips = C<ListBox>("TagChips");
        chips.SelectionChanged += (_, _) => LoadTag();

        HookList("LocList", _locItems, QueueSource.Folder);
        HookList("TagList", _tagItems, QueueSource.Tag);
        HookList("RateList", _rateItems, QueueSource.Rating);
        HookList("ViewList", _viewItems, QueueSource.MostViewed);

        HookSort("SortNet", () => LoadRating(RatingSort.NetScore));
        HookSort("SortRatio", () => LoadRating(RatingSort.Ratio));
        HookSort("SortLiked", () => LoadRating(RatingSort.MostLiked));
        HookSort("SortHated", () => LoadRating(RatingSort.MostDisliked));
        HookSort("SortTime", () => LoadViewed(ViewedSort.TotalTime));
        HookSort("SortViews", () => LoadViewed(ViewedSort.ViewCount));

        C<Button>("PlayButton").Click += (_, _) => _engine?.TogglePause();
        C<Button>("NextButton").Click += (_, _) => _engine?.Next();
        C<Button>("PrevButton").Click += (_, _) => _engine?.Previous();
        C<Button>("LikeButton").Click += (_, _) => Vote(1);
        C<Button>("DislikeButton").Click += (_, _) => Vote(-1);

        C<HeatSeekBar>("Seek").Seeked += f => _engine?.SeekFraction(f);

        var tagInput = C<TextBox>("TagInput");
        tagInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var name = tagInput.Text?.Trim();
            if (string.IsNullOrEmpty(name) || _engine?.Current is null) return;

            _engine.TagCurrent(name);
            tagInput.Text = "";
            SetStatus($"tagged '{name}'");
            RefreshTagsAndGrids();
        };

        Closing += (_, _) => Teardown();
    }

    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void StartEngine()
    {
        var vlc = VlcRuntime.Instance;
        if (vlc is null)
        {
            SetStatus($"playback unavailable — {VlcRuntime.Failure}");
            C<TextBlock>("VideoPlaceholder").Text = "libvlc could not be loaded";
            return;
        }

        _engine = new PlayerEngine(vlc, _library);
        C<VideoView>("Video").MediaPlayer = _engine.Player;

        // Engine events arrive on libvlc threads; everything below touches the UI.
        _engine.Tick += () => Dispatcher.UIThread.Post(UpdatePlayerBar, DispatcherPriority.Background);
        _engine.TrackChanged += v => Dispatcher.UIThread.Post(() => OnTrackChanged(v));
        _engine.QueueExhausted += () => Dispatcher.UIThread.Post(() =>
        {
            SetStatus("queue finished");
            RefreshGrids();
        });
    }

    // ------------------------------------------------------------------ loading

    private void RefreshAll()
    {
        LoadTree();
        RefreshTagsAndGrids();
        UpdateSummary();
    }

    private void RefreshTagsAndGrids()
    {
        LoadTagChips();
        RefreshGrids();
    }

    private void RefreshGrids()
    {
        LoadRating(CurrentRatingSort());
        LoadViewed(CurrentViewedSort());
        LoadDupes();
        LoadFolder();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var (videos, files, bytes) = _library.GetLibrarySize();
        C<TextBlock>("LibrarySummary").Text = videos == 0
            ? "no videos yet — add a path"
            : $"{videos} videos · {Fmt.Size(bytes)}" + (files > videos ? $" · {files} files" : "");
    }

    private void LoadTree()
    {
        var expanded = _tree.Count > 0;
        _tree.Clear();
        foreach (var node in _library.GetFolderTree())
            _tree.Add(new FolderRow(node) { IsExpanded = !expanded || node.IsRoot });
    }

    private void LoadFolder()
    {
        _locItems.Clear();
        if (_selectedFolder is null)
        {
            C<TextBlock>("FolderTitle").Text = "Select a folder";
            C<TextBlock>("FolderSubtitle").Text = "";
            return;
        }

        var recursive = C<CheckBox>("RecursiveToggle").IsChecked == true;
        foreach (var v in _library.GetVideosInFolder(_selectedFolder.FullPath, recursive))
            _locItems.Add(new VideoRow(v));

        C<TextBlock>("FolderTitle").Text = _selectedFolder.IsRoot
            ? _selectedFolder.FullPath
            : _selectedFolder.Name;
        C<TextBlock>("FolderSubtitle").Text =
            $"{_selectedFolder.FullPath}   ·   {_locItems.Count} videos" +
            (recursive ? " (including subfolders)" : "");
        C<Button>("RemoveRootButton").IsVisible = _selectedFolder.IsRoot;
    }

    private void LoadTagChips()
    {
        var chips = C<ListBox>("TagChips");
        var previous = (chips.SelectedItem as TagRow)?.Id;

        _tagChips.Clear();
        foreach (var t in _library.GetTags()) _tagChips.Add(new TagRow(t));

        if (_tagChips.Count == 0) { _tagItems.Clear(); return; }

        var restore = _tagChips.FirstOrDefault(c => c.Id == previous) ?? _tagChips[0];
        chips.SelectedItem = restore;
        LoadTag();
    }

    private void LoadTag()
    {
        _tagItems.Clear();
        if (C<ListBox>("TagChips").SelectedItem is not TagRow tag)
        {
            C<TextBlock>("TagHeader").Text = "NAME";
            return;
        }

        foreach (var v in _library.GetVideosByTag(tag.Id)) _tagItems.Add(new VideoRow(v));
        C<TextBlock>("TagHeader").Text = $"NAME · tagged “{tag.Name}”";
    }

    private void LoadRating(RatingSort sort)
    {
        _rateItems.Clear();
        foreach (var v in _library.GetRating(sort)) _rateItems.Add(new VideoRow(v));
    }

    private void LoadViewed(ViewedSort sort)
    {
        _viewItems.Clear();
        foreach (var v in _library.GetMostViewed(sort)) _viewItems.Add(new VideoRow(v));
    }

    private void LoadDupes()
    {
        _dupes.Clear();
        foreach (var (video, paths) in _library.GetDuplicates())
            _dupes.Add(new DuplicateRow(video, paths));
    }

    private RatingSort CurrentRatingSort() =>
        C<RadioButton>("SortRatio").IsChecked == true ? RatingSort.Ratio :
        C<RadioButton>("SortLiked").IsChecked == true ? RatingSort.MostLiked :
        C<RadioButton>("SortHated").IsChecked == true ? RatingSort.MostDisliked :
        RatingSort.NetScore;

    private ViewedSort CurrentViewedSort() =>
        C<RadioButton>("SortViews").IsChecked == true ? ViewedSort.ViewCount : ViewedSort.TotalTime;

    // ------------------------------------------------------------------ actions

    private async void OnAddPath(object? sender, RoutedEventArgs e)
    {
        var dialog = new AddPathDialog(_library);
        await dialog.ShowDialog(this);

        if (dialog.Result is null) return;
        SetStatus($"added {dialog.Result.Path}");
        LoadTree();
        StartScan();
    }

    private void OnRemoveRoot(object? sender, RoutedEventArgs e)
    {
        if (_selectedFolder is not { IsRoot: true, RootId: { } id }) return;

        _library.RemoveRoot(id);
        _selectedFolder = null;
        SetStatus("root removed — ratings and history kept");
        LoadTree();
        RefreshGrids();
    }

    /// <summary>Scans on a background thread, then reads metadata for anything new.</summary>
    private void StartScan()
    {
        if (_scanCts is not null) { SetStatus("a scan is already running"); return; }

        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        SetStatus("scanning…");

        Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<ScanProgress>(p =>
                    Dispatcher.UIThread.Post(() => SetStatus($"scanning… {p.FilesSeen} files"),
                                             DispatcherPriority.Background));

                var result = new Scanner(_db).Scan(progress, ct);
                Dispatcher.UIThread.Post(() =>
                {
                    SetStatus($"{result.Added} added, {result.Updated} updated, " +
                              $"{result.Removed} gone ({result.Elapsed.TotalSeconds:0.0}s)");
                    RefreshAll();
                });

                // Durations and codecs come from libvlc, one file at a time.
                var vlc = VlcRuntime.Instance;
                if (vlc is not null)
                {
                    var probe = new MediaProbe(vlc);
                    var done = await probe.ProbePendingAsync(_library,
                        new Progress<string>(name => Dispatcher.UIThread.Post(
                            () => SetStatus($"reading {name}"), DispatcherPriority.Background)),
                        ct);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (done > 0) SetStatus($"read metadata for {done} videos");
                        RefreshAll();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => SetStatus("scan cancelled"));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => SetStatus($"scan failed: {ex.Message}"));
            }
            finally
            {
                _scanCts?.Dispose();
                _scanCts = null;
            }
        }, ct);
    }

    /// <summary>Double-click plays, building a queue from the rest of that list.</summary>
    private void HookList(string name, ObservableCollection<VideoRow> items, QueueSource source)
    {
        var list = C<ListBox>(name);
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is not VideoRow row) return;
            PlayFromList(items, items.IndexOf(row), source, SourceLabel(source));
        };
    }

    private string SourceLabel(QueueSource source) => source switch
    {
        QueueSource.Folder => _selectedFolder?.Name ?? "folder",
        QueueSource.Tag => (C<ListBox>("TagChips").SelectedItem as TagRow)?.Name ?? "tag",
        QueueSource.Rating => "rating",
        QueueSource.MostViewed => "most viewed",
        _ => "",
    };

    private void PlayFromList(ObservableCollection<VideoRow> items, int index,
                              QueueSource source, string label)
    {
        if (_engine is null) { SetStatus("playback unavailable"); return; }
        if (items.Count == 0) return;

        var queue = new PlayQueue(items.Select(i => i.Stats), index, source, label);
        var first = queue.Current;
        if (first is null) return;

        _engine.Play(first, queue);
        C<ListBox>("Nav").SelectedIndex = 0;
    }

    private void Vote(int vote)
    {
        if (_engine?.Current is null) { SetStatus("nothing playing"); return; }

        if (!_engine.CastVote(vote))
        {
            SetStatus("already voted this session");
            return;
        }

        SetStatus(vote > 0 ? "liked" : "disliked");
        RefreshGrids();
        UpdateVoteButtons();
    }

    private void HookSort(string name, Action onChecked)
    {
        var rb = C<RadioButton>(name);
        rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) onChecked(); };
    }

    // ------------------------------------------------------------------ updates

    /// <summary>
    /// Asks GitHub for a newer release in the background. Silent when there is none, when the
    /// machine is offline, or when this is not a Velopack deployment (a dev run).
    /// </summary>
    private void StartUpdateCheck()
    {
        _updater = Updater.TryCreate();
        if (_updater is null) return;

        Task.Run(async () =>
        {
            var version = await _updater.CheckAsync();
            if (version is null) return;

            Dispatcher.UIThread.Post(() =>
            {
                var button = C<Button>("UpdateButton");
                button.Content = $"Update to {version}";
                button.IsVisible = true;
                SetStatus($"version {version} is available");
            });
        });
    }

    private void ApplyUpdate()
    {
        if (_updater is not { HasPendingUpdate: true }) return;

        var button = C<Button>("UpdateButton");
        button.IsEnabled = false;
        button.Content = "Downloading…";
        SetStatus("downloading update…");

        Task.Run(async () =>
        {
            // Teardown persists the open viewing session; ApplyAsync then replaces the process.
            var ok = await _updater.ApplyAsync(() => Dispatcher.UIThread.Invoke(Teardown));
            if (ok) return;

            Dispatcher.UIThread.Post(() =>
            {
                button.IsEnabled = true;
                button.Content = $"Update to {_updater.PendingVersion}";
                SetStatus("update download failed — will retry next launch");
            });
        });
    }

    // ------------------------------------------------------------------ player bar

    private void OnTrackChanged(VideoStats? video)
    {
        var seek = C<HeatSeekBar>("Seek");

        if (video is null)
        {
            C<TextBlock>("NowTitle").Text = "Nothing playing";
            C<TextBlock>("NowSubtitle").Text = "Pick a video from Locations or Tags";
            C<TextBlock>("BarTitle").Text = "Nothing playing";
            C<TextBlock>("BarSubtitle").Text = "";
            seek.Heat = null;
            seek.Position = 0;
            return;
        }

        C<TextBlock>("NowTitle").Text = video.DisplayName;
        C<TextBlock>("NowSubtitle").Text = video.Folder;
        C<TextBlock>("BarTitle").Text = video.DisplayName;
        C<TextBlock>("VideoPlaceholder").IsVisible = false;

        // The heatmap is built from everything watched before this session.
        seek.Heat = _engine?.CurrentHeat(HeatBuckets);
        seek.Position = 0;

        UpdateVoteButtons();
        RefreshGrids();
    }

    private void UpdatePlayerBar()
    {
        if (_engine?.Current is null) return;

        var position = _engine.PositionMs;
        var duration = _engine.DurationMs;

        C<TextBlock>("TimeText").Text = duration > 0
            ? $"{Fmt.Time(position)} / {Fmt.Time(duration)}"
            : Fmt.Time(position);

        if (duration > 0) C<HeatSeekBar>("Seek").Position = Math.Clamp((double)position / duration, 0, 1);

        C<Button>("PlayButton").Content = _engine.IsPlaying ? "▮▮" : "▶";

        var queue = _engine.Queue;
        var next = queue.Peek(1);
        C<TextBlock>("BarSubtitle").Text = next is null
            ? queue.PositionText
            : $"next: {next.DisplayName}   ·   {queue.PositionText}";
    }

    /// <summary>Both buttons grey out once this session has used its single vote.</summary>
    private void UpdateVoteButtons()
    {
        var video = _engine?.Current;
        var voted = (_engine?.CurrentVote ?? 0) != 0;

        C<Button>("LikeButton").IsEnabled = video is not null && !voted;
        C<Button>("DislikeButton").IsEnabled = video is not null && !voted;

        if (video is null) return;
        var fresh = _library.GetVideo(video.VideoId);
        C<TextBlock>("LikeCount").Text = (fresh?.Likes ?? 0).ToString();
        C<TextBlock>("DislikeCount").Text = (fresh?.Dislikes ?? 0).ToString();
    }

    private void ShowPage(int index)
    {
        for (var i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == index;
    }

    private void SetStatus(string text) => C<TextBlock>("StatusText").Text = text;

    /// <summary>Renders every page to a PNG so the UI can be reviewed without a live session.</summary>
    public async Task CaptureAllAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        string[] names = ["now-playing", "locations", "tags", "rating", "most-viewed", "duplicates"];
        var nav = C<ListBox>("Nav");

        // Give Locations something selected so the grid is not empty in the shot.
        if (_tree.Count > 0)
        {
            var first = _tree[0];
            _selectedFolder = FindWithVideos(first) ?? first;
            LoadFolder();
        }

        for (var i = 0; i < names.Length; i++)
        {
            nav.SelectedIndex = i;
            ShowPage(i);
            await Task.Delay(400);
            Dispatcher.UIThread.RunJobs();

            var size = new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
            rtb.Render(this);
            var path = System.IO.Path.Combine(dir, $"{i + 1:00}-{names[i]}.png");
            rtb.Save(path);
            Console.WriteLine($"wrote {path}");
        }
    }

    private static FolderRow? FindWithVideos(FolderRow node)
    {
        if (node.Node.DirectCount > 0) return node;
        foreach (var child in node.Children)
            if (FindWithVideos(child) is { } found) return found;
        return null;
    }

    private bool _torndown;

    private void Teardown()
    {
        if (_torndown) return;
        _torndown = true;

        _scanCts?.Cancel();
        _engine?.ShutdownForExit();   // finalises the open session so its watch time is kept
        _db.Dispose();
    }
}
