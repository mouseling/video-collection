using System.Collections.ObjectModel;

namespace Vcm.Prototype;

/// <summary>A video as the library sees it. Mock values stand in for the real DB rows.</summary>
public sealed class VideoItem
{
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public int DurationSec { get; init; }
    public int Likes { get; init; }
    public int Dislikes { get; init; }
    public int Views { get; init; }
    public int WatchedSec { get; init; }
    public string Tags { get; init; } = "";

    public int Score => Likes - Dislikes;
    public double Ratio => Likes + Dislikes == 0 ? 0 : (double)Likes / (Likes + Dislikes);

    public string Duration => Fmt(DurationSec);
    public string Watched => Fmt(WatchedSec);
    public string RatioText => Likes + Dislikes == 0 ? "—" : $"{Ratio * 100:0}%";
    public string ScoreText => Score > 0 ? $"+{Score}" : Score.ToString();
    public string ViewsText => Views == 1 ? "1 view" : $"{Views} views";

    /// <summary>Shown on hover in the Most Viewed grid — the per-session breakdown.</summary>
    public string WatchTooltip =>
        $"{Views} views · {Fmt(WatchedSec)} total\n" +
        $"average {Fmt(Views == 0 ? 0 : WatchedSec / Views)} per view";

    public static string Fmt(int sec)
    {
        var h = sec / 3600;
        var m = sec % 3600 / 60;
        var s = sec % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}

public sealed class FolderNode
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsRoot { get; init; }
    public bool IsExpanded { get; init; }
    public ObservableCollection<FolderNode> Children { get; init; } = new();
}

public sealed class TagItem
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public string Label => $"{Name}  {Count}";
}

public sealed class DuplicateGroup
{
    public string Name { get; init; } = "";
    public string Size { get; init; } = "";
    public ObservableCollection<string> Paths { get; init; } = new();
    public string Header => $"{Name}   ·   {Size}   ·   {Paths.Count} copies";
}

public static class MockData
{
    public static ObservableCollection<FolderNode> Roots() => new()
    {
        new FolderNode
        {
            Name = "D:\\Videos", Detail = "412 files", IsRoot = true, IsExpanded = true,
            Children =
            {
                new FolderNode { Name = "Conference talks", Detail = "88" },
                new FolderNode
                {
                    Name = "Tutorials", Detail = "140", IsExpanded = true,
                    Children =
                    {
                        new FolderNode { Name = "Reverse engineering", Detail = "47" },
                        new FolderNode { Name = "Graphics", Detail = "61" },
                        new FolderNode { Name = "Networking", Detail = "32" },
                    }
                },
                new FolderNode { Name = "Clips", Detail = "184" },
            }
        },
        new FolderNode
        {
            Name = "E:\\Archive\\video", Detail = "263 files", IsRoot = true, IsExpanded = true,
            Children =
            {
                new FolderNode { Name = "2024", Detail = "121" },
                new FolderNode { Name = "2025", Detail = "142" },
            }
        },
        new FolderNode
        {
            Name = "F:\\Downloads", Detail = "97 files", IsRoot = true,
            Children = { new FolderNode { Name = "unsorted", Detail = "97" } }
        },
    };

    public static ObservableCollection<VideoItem> Videos() => new()
    {
        new VideoItem { Name = "IL2CPP internals deep dive.mkv",          Folder = "D:\\Videos\\Tutorials\\Reverse engineering", DurationSec = 5340, Likes = 8, Dislikes = 3, Views = 7, WatchedSec = 11280, Tags = "reverse-eng, deep-dive" },
        new VideoItem { Name = "Writing a DMA radar from scratch.mp4",    Folder = "D:\\Videos\\Tutorials\\Reverse engineering", DurationSec = 7212, Likes = 12, Dislikes = 1, Views = 11, WatchedSec = 19800, Tags = "reverse-eng, dma" },
        new VideoItem { Name = "Vulkan render graph explained.webm",      Folder = "D:\\Videos\\Tutorials\\Graphics",            DurationSec = 3918, Likes = 5, Dislikes = 0, Views = 4, WatchedSec = 7440, Tags = "graphics" },
        new VideoItem { Name = "SIMD tricks nobody tells you.mkv",        Folder = "D:\\Videos\\Conference talks",               DurationSec = 2706, Likes = 9, Dislikes = 2, Views = 6, WatchedSec = 9120, Tags = "perf, deep-dive" },
        new VideoItem { Name = "TCP congestion control walkthrough.avi",  Folder = "D:\\Videos\\Tutorials\\Networking",          DurationSec = 4455, Likes = 3, Dislikes = 4, Views = 3, WatchedSec = 3300, Tags = "networking" },
        new VideoItem { Name = "Ghidra scripting for beginners.mp4",      Folder = "D:\\Videos\\Tutorials\\Reverse engineering", DurationSec = 3180, Likes = 6, Dislikes = 1, Views = 5, WatchedSec = 8700, Tags = "reverse-eng, tooling" },
        new VideoItem { Name = "Compute shaders in 20 minutes.mkv",       Folder = "D:\\Videos\\Tutorials\\Graphics",            DurationSec = 1245, Likes = 4, Dislikes = 0, Views = 9, WatchedSec = 6600, Tags = "graphics, short" },
        new VideoItem { Name = "Kernel driver signing explained.mp4",     Folder = "E:\\Archive\\video\\2025",                   DurationSec = 2988, Likes = 2, Dislikes = 5, Views = 2, WatchedSec = 1560, Tags = "windows" },
        new VideoItem { Name = "Memory forensics case study.mkv",         Folder = "E:\\Archive\\video\\2024",                   DurationSec = 6120, Likes = 7, Dislikes = 2, Views = 4, WatchedSec = 10320, Tags = "forensics, deep-dive" },
        new VideoItem { Name = "Avalonia custom controls.mp4",            Folder = "F:\\Downloads\\unsorted",                    DurationSec = 2415, Likes = 5, Dislikes = 1, Views = 8, WatchedSec = 12060, Tags = "dotnet, ui" },
        new VideoItem { Name = "PE format from zero.mkv",                 Folder = "D:\\Videos\\Tutorials\\Reverse engineering", DurationSec = 5805, Likes = 10, Dislikes = 2, Views = 6, WatchedSec = 14400, Tags = "reverse-eng" },
        new VideoItem { Name = "Why your cache misses.webm",              Folder = "D:\\Videos\\Conference talks",               DurationSec = 2160, Likes = 11, Dislikes = 0, Views = 13, WatchedSec = 21300, Tags = "perf" },
        new VideoItem { Name = "Debugging release builds.mp4",            Folder = "D:\\Videos\\Conference talks",               DurationSec = 3345, Likes = 1, Dislikes = 3, Views = 2, WatchedSec = 1980, Tags = "tooling" },
        new VideoItem { Name = "Unity asset pipeline teardown.mkv",       Folder = "F:\\Downloads\\unsorted",                    DurationSec = 4680, Likes = 6, Dislikes = 3, Views = 5, WatchedSec = 9000, Tags = "reverse-eng, unity" },
    };

    public static ObservableCollection<TagItem> Tags() => new()
    {
        new TagItem { Name = "reverse-eng", Count = 47 },
        new TagItem { Name = "deep-dive", Count = 23 },
        new TagItem { Name = "graphics", Count = 31 },
        new TagItem { Name = "perf", Count = 18 },
        new TagItem { Name = "networking", Count = 12 },
        new TagItem { Name = "tooling", Count = 26 },
        new TagItem { Name = "dotnet", Count = 14 },
        new TagItem { Name = "windows", Count = 9 },
        new TagItem { Name = "forensics", Count = 6 },
        new TagItem { Name = "unity", Count = 11 },
        new TagItem { Name = "short", Count = 38 },
        new TagItem { Name = "ui", Count = 7 },
    };

    public static ObservableCollection<DuplicateGroup> Duplicates() => new()
    {
        new DuplicateGroup
        {
            Name = "SIMD tricks nobody tells you.mkv", Size = "1.4 GB",
            Paths = { "D:\\Videos\\Conference talks\\SIMD tricks nobody tells you.mkv",
                      "F:\\Downloads\\unsorted\\SIMD tricks nobody tells you.mkv" }
        },
        new DuplicateGroup
        {
            Name = "PE format from zero.mkv", Size = "2.1 GB",
            Paths = { "D:\\Videos\\Tutorials\\Reverse engineering\\PE format from zero.mkv",
                      "E:\\Archive\\video\\2024\\PE format from zero.mkv",
                      "F:\\Downloads\\unsorted\\PE format from zero (1).mkv" }
        },
    };

    /// <summary>Per-bucket replay counts for the seek bar heatmap. Index = slice of the video.</summary>
    public static double[] Heat()
    {
        var h = new double[160];
        for (var i = 0; i < h.Length; i++)
        {
            var x = i / (double)h.Length;
            // A couple of re-watched stretches plus a low baseline of first-pass viewing.
            h[i] = 0.18
                 + 0.95 * Math.Exp(-Math.Pow((x - 0.21) / 0.045, 2))
                 + 0.62 * Math.Exp(-Math.Pow((x - 0.38) / 0.028, 2))
                 + 0.35 * Math.Exp(-Math.Pow((x - 0.55) / 0.06, 2))
                 + 0.80 * Math.Exp(-Math.Pow((x - 0.73) / 0.035, 2))
                 + 0.22 * Math.Exp(-Math.Pow((x - 0.90) / 0.05, 2));
        }
        var max = h.Max();
        return h.Select(v => v / max).ToArray();
    }
}
