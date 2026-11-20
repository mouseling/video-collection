namespace Vcm.Core;

/// <summary>A registered root folder. Scanned recursively.</summary>
public sealed record Root(long Id, string Path, DateTime AddedUtc, DateTime? LastScanUtc);

public sealed record Tag(long Id, string Name, int VideoCount);

/// <summary>A video plus its rolled-up statistics — what every grid in the UI binds to.</summary>
public sealed record VideoStats(
    long VideoId,
    string DisplayName,
    string Path,
    string Folder,
    long DurationMs,
    long TotalWatchedMs,
    int Views,
    int Likes,
    int Dislikes,
    int FileCount,
    string Tags)
{
    public int Score => Likes - Dislikes;
    public double Ratio => Likes + Dislikes == 0 ? 0 : (double)Likes / (Likes + Dislikes);
    public bool IsDuplicated => FileCount > 1;
}

/// <summary>A node in the Locations tree. Mirrors the real on-disk folder structure.</summary>
public sealed class FolderNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsRoot { get; init; }
    public long? RootId { get; init; }
    public int DirectCount { get; set; }
    public int TotalCount { get; set; }
    public List<FolderNode> Children { get; } = new();
}

public sealed record ScanProgress(string CurrentPath, int FilesSeen, int FilesHashed);

/// <summary>
/// Outcome of a scan. <paramref name="Skipped"/> counts files and directories that could not be
/// read; they are NOT treated as deleted, but the user should be told the picture is incomplete
/// rather than shown a silent "0 added".
/// </summary>
public sealed record ScanResult(int Added, int Updated, int Removed, int Unchanged,
                                int Skipped, string? FirstSkipped, TimeSpan Elapsed);

public enum RatingSort { NetScore, Ratio, MostLiked, MostDisliked }

public enum ViewedSort { TotalTime, ViewCount }
