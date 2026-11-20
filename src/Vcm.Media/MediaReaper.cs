using LibVLCSharp.Shared;

using VlcMedia = LibVLCSharp.Shared.Media;

namespace Vcm.Media;

/// <summary>
/// Disposes <see cref="VlcMedia"/> objects a short while after we are finished with them.
///
/// Disposing a media immediately after its parse completes segfaults the process: libvlc's
/// preparser thread is still holding the object even though the managed parse task has already
/// resolved. Letting the finalizer handle it is safe but unbounded, so retired media are parked
/// here and released once the preparser has certainly let go.
/// </summary>
public static class MediaReaper
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);
    private static readonly object Gate = new();
    private static readonly Queue<(VlcMedia Media, DateTime Due)> Pending = new();
    private static Timer? _timer;

    public static void Retire(VlcMedia media)
    {
        lock (Gate)
        {
            Pending.Enqueue((media, DateTime.UtcNow + Grace));
            _timer ??= new Timer(_ => Sweep(), null, Grace, Grace);
        }
    }

    private static void Sweep()
    {
        var due = new List<VlcMedia>();

        lock (Gate)
        {
            while (Pending.Count > 0 && Pending.Peek().Due <= DateTime.UtcNow)
                due.Add(Pending.Dequeue().Media);
        }

        foreach (var media in due)
        {
            try { media.Dispose(); }
            catch { /* shutting down, or libvlc already tore it down */ }
        }
    }
}
