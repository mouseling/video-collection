namespace Vcm.Core;

/// <summary>Where a queue came from, so the player can say what it is playing through.</summary>
public enum QueueSource { Folder, Tag, Rating, MostViewed, Single }

/// <summary>
/// The list of videos playing through after the current one. Built from whatever context the
/// user started playback in — a folder or a tag — and played in that context's own order.
/// Playback always starts a video from the beginning, so the queue holds no positions.
/// </summary>
public sealed class PlayQueue
{
    private readonly List<VideoStats> _items;

    public QueueSource Source { get; }
    public string Label { get; }
    public int Index { get; private set; }

    public PlayQueue(IEnumerable<VideoStats> items, int startIndex, QueueSource source, string label)
    {
        _items = items.ToList();
        Index = Math.Clamp(startIndex, 0, Math.Max(0, _items.Count - 1));
        Source = source;
        Label = label;
    }

    public static PlayQueue Empty => new([], 0, QueueSource.Single, "");

    public IReadOnlyList<VideoStats> Items => _items;
    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    public VideoStats? Current => _items.Count == 0 ? null : _items[Index];

    public VideoStats? Peek(int offset)
    {
        var i = Index + offset;
        return i >= 0 && i < _items.Count ? _items[i] : null;
    }

    public bool HasNext => Index + 1 < _items.Count;
    public bool HasPrevious => Index > 0;

    public VideoStats? MoveNext() => HasNext ? _items[++Index] : null;
    public VideoStats? MovePrevious() => HasPrevious ? _items[--Index] : null;

    /// <summary>Jumps to a specific entry, e.g. when the user double-clicks further down the list.</summary>
    public VideoStats? MoveTo(int index)
    {
        if (index < 0 || index >= _items.Count) return null;
        Index = index;
        return _items[index];
    }

    public int IndexOfVideo(long videoId) => _items.FindIndex(v => v.VideoId == videoId);

    public string PositionText => IsEmpty ? "" : $"queue {Index + 1} of {_items.Count}";
}
