using System.Collections.ObjectModel;
using Vcm.Core;

namespace Vcm.App;

/// <summary>A grid row. Wraps <see cref="VideoStats"/> with display formatting for binding.</summary>
public sealed class VideoRow
{
    public VideoStats Stats { get; }

    public VideoRow(VideoStats stats) => Stats = stats;

    public long VideoId => Stats.VideoId;
    public string Name => Stats.DisplayName;
    public string Path => Stats.Path;
    public string Folder => Stats.Folder;
    public string Tags => Stats.Tags;
    public int Views => Stats.Views;
    public int Likes => Stats.Likes;
    public int Dislikes => Stats.Dislikes;

    public string Duration => Fmt.Time(Stats.DurationMs);
    public string Watched => Fmt.Time(Stats.TotalWatchedMs);
    public string ScoreText => Stats.Score > 0 ? $"+{Stats.Score}" : Stats.Score.ToString();
    public string RatioText => Stats.Likes + Stats.Dislikes == 0 ? "—" : $"{Stats.Ratio * 100:0}%";
    public string CopiesText => Stats.FileCount > 1 ? $"{Stats.FileCount} copies" : "";

    /// <summary>Per-view breakdown, shown on hover in the Most viewed grid.</summary>
    public string WatchTooltip => Stats.Views == 0
        ? "never watched"
        : $"{Stats.Views} {(Stats.Views == 1 ? "view" : "views")} · {Fmt.Time(Stats.TotalWatchedMs)} total\n" +
          $"average {Fmt.Time(Stats.TotalWatchedMs / Stats.Views)} per view";
}

public sealed class TagRow
{
    public Tag Tag { get; }
    public TagRow(Tag tag) => Tag = tag;

    public long Id => Tag.Id;
    public string Name => Tag.Name;
    public string Label => $"{Tag.Name}  {Tag.VideoCount}";
}

/// <summary>A node in the Locations tree, wrapping the core model for binding.</summary>
public sealed class FolderRow
{
    public Vcm.Core.FolderNode Node { get; }
    public ObservableCollection<FolderRow> Children { get; } = new();

    public FolderRow(Vcm.Core.FolderNode node)
    {
        Node = node;
        foreach (var child in node.Children) Children.Add(new FolderRow(child));
    }

    /// <summary>
    /// Roots are stored under their full path, which is unreadable in a narrow tree once it gets
    /// long. Show the tail of deep paths and keep the whole thing in the tooltip.
    /// </summary>
    public string Name
    {
        get
        {
            if (!Node.IsRoot || Node.Name.Length <= 30) return Node.Name;
            var parts = Node.Name.Split(System.IO.Path.DirectorySeparatorChar,
                                        StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 2 ? Node.Name : "…/" + string.Join('/', parts[^2..]);
        }
    }

    public string FullPath => Node.FullPath;
    public bool IsRoot => Node.IsRoot;
    public long? RootId => Node.RootId;

    /// <summary>Direct count where it differs from the total, so the tree reads honestly.</summary>
    public string CountText => Node.TotalCount == Node.DirectCount
        ? Node.TotalCount.ToString()
        : $"{Node.DirectCount} / {Node.TotalCount}";

    public bool IsExpanded { get; set; }
}

public sealed class DuplicateRow
{
    public VideoStats Video { get; }
    public ObservableCollection<string> Paths { get; } = new();

    public DuplicateRow(VideoStats video, IEnumerable<string> paths)
    {
        Video = video;
        foreach (var p in paths) Paths.Add(p);
    }

    public string Header => $"{Video.DisplayName}   ·   {Fmt.Time(Video.DurationMs)}   ·   {Paths.Count} copies";
}

public static class Fmt
{
    /// <summary>h:mm:ss above an hour, m:ss below. Used everywhere a duration is shown.</summary>
    public static string Time(long ms)
    {
        if (ms <= 0) return "—";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
