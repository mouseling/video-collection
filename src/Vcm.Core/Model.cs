namespace Vcm.Core;

/// <summary>A registered root folder. Scanned recursively.</summary>
public sealed record Root(long Id, string Path, DateTime AddedUtc, DateTime? LastScanUtc);

/// <summary>
/// A video, identified by content rather than by path. One row per distinct piece of content;
/// the same content living at several paths produces several <see cref="VideoFile"/> rows,
/// which is exactly what makes duplicates visible and lets statistics survive a move or rename.
/// </summary>
public sealed record Video(
    long Id,
    string ContentHash,
    long DurationMs,
    int Width,
    int Height,
    string? VideoCodec,
    string? AudioCodec,
    DateTime FirstSeenUtc);

/// <summary>One path on disk pointing at a <see cref="Video"/>.</summary>
public sealed record VideoFile(
    long Id,
    long VideoId,
    long? RootId,
    string Path,
    long SizeBytes,
    DateTime MTimeUtc,
    bool Present);

public sealed record Tag(long Id, string Name, int VideoCount);

/// <summary>
/// A single playback of a video. Watch time excludes paused time. A session only counts toward
/// view statistics once it passes the 10-second threshold, and carries at most one vote.
/// </summary>
public sealed record Session(
    long Id,
    long VideoId,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long WatchedMs,
    bool Counted,
    int Vote);

/// <summary>A stretch of a video that was actually watched. Only stored if long enough.</summary>
public sealed record Region(long Id, long VideoId, long SessionId, long StartMs, long EndMs);

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

public sealed record ScanResult(int Added, int Updated, int Removed, int Unchanged, TimeSpan Elapsed);

public enum RatingSort { NetScore, Ratio, MostLiked, MostDisliked }

public enum ViewedSort { TotalTime, ViewCount }
