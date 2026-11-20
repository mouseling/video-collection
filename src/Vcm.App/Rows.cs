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
            var sep = System.IO.Path.DirectorySeparatorChar;
            var parts = Node.Name.Split(sep, StringSplitOptions.RemoveEmptyEntries);

            // Rejoined with the platform separator: splitting on '\' and rejoining with '/'
            // would show a Windows root as a mix of both.
            return parts.Length <= 2 ? Node.Name : "…" + sep + string.Join(sep, parts[^2..]);
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

/// <summary>One copy on disk. A row object rather than a bare string so the reveal button can
/// find its own path.</summary>
public sealed class PathRow
{
    public required string Path { get; init; }
}

public sealed class DuplicateRow
{
    public VideoStats Video { get; }
    public ObservableCollection<PathRow> Paths { get; } = new();

    public DuplicateRow(VideoStats video, IEnumerable<string> paths)
    {
        Video = video;
        foreach (var p in paths) Paths.Add(new PathRow { Path = p });
    }

    public string Header => $"{Video.DisplayName}   ·   {Fmt.Time(Video.DurationMs)}   ·   {Paths.Count} copies";
}
