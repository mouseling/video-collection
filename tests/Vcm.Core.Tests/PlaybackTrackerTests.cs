using Vcm.Core;
using Xunit;

namespace Vcm.Core.Tests;

/// <summary>
/// The statistics rules, driven against a virtual clock so the thresholds can be probed exactly
/// and instantly rather than by waiting in real time.
/// </summary>
public class PlaybackTrackerTests
{
    /// <summary>Plays for <paramref name="seconds"/>, ticking once a second as the player does.</summary>
    private static PlaybackTracker Watch(TempLibrary lib, long videoId, int seconds,
                                         int startSec = 0, bool playing = true)
    {
        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracker = lib.Library.StartSession(videoId, startSec * 1000L, () => clock);

        var position = startSec * 1000L;
        for (var i = 0; i < seconds; i++)
        {
            clock = clock.AddSeconds(1);
            if (playing) position += 1000;
            tracker.Update(position, playing);
        }
        return tracker;
    }

    private static long SeedVideo(TempLibrary lib)
    {
        lib.AddRoot();
        lib.MakeVideo("v.mp4");
        lib.Scan();
        return lib.Library.GetAllVideos().Single().VideoId;
    }

    [Theory]
    [InlineData(9, false)]    // under the threshold: an abandoned start
    [InlineData(10, true)]    // exactly the threshold counts
    [InlineData(30, true)]
    public void The_ten_second_rule_decides_whether_a_session_counts(int seconds, bool expected)
    {
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        var tracker = Watch(lib, id, seconds);
        tracker.Finish();

        Assert.Equal(expected, tracker.Counted);
        Assert.Equal(expected ? 1 : 0, lib.Library.GetMostViewed(ViewedSort.ViewCount).Count);
    }

    [Fact]
    public void Paused_time_does_not_count_as_watch_time()
    {
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        var tracker = Watch(lib, id, seconds: 60, playing: false);
        tracker.Finish();

        Assert.Equal(0, tracker.WatchedMs);
        Assert.False(tracker.Counted);
    }

    [Fact]
    public void Watch_time_accumulates_across_sessions()
    {
        // The user's own worked example: 10 + 15 + 8 minutes is 33 minutes over three views,
        // and a five-second abandoned start adds nothing.
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        Watch(lib, id, 5).Finish();
        Watch(lib, id, 600).Finish();
        Watch(lib, id, 900, startSec: 600).Finish();
        Watch(lib, id, 480).Finish();

        var stats = lib.Library.GetMostViewed(ViewedSort.TotalTime).Single();
        Assert.Equal(3, stats.Views);
        Assert.Equal(TimeSpan.FromMinutes(33).TotalMilliseconds, stats.TotalWatchedMs);
    }

    [Fact]
    public void Only_one_vote_is_accepted_per_session()
    {
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        var tracker = Watch(lib, id, 30);
        Assert.True(tracker.CastVote(1));
        Assert.False(tracker.CastVote(1));     // pressing again changes nothing
        Assert.False(tracker.CastVote(-1));    // and cannot be flipped either
        tracker.Finish();

        var stats = lib.Library.GetRating(RatingSort.NetScore).Single();
        Assert.Equal(1, stats.Likes);
        Assert.Equal(0, stats.Dislikes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(-2)]
    public void Only_like_and_dislike_are_valid_votes(int vote)
    {
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        var tracker = Watch(lib, id, 30);
        Assert.False(tracker.CastVote(vote));
        Assert.Equal(0, tracker.Vote);
    }

    [Fact]
    public void Votes_accumulate_across_sessions()
    {
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        foreach (var vote in new[] { 1, 1, -1 })
        {
            var tracker = Watch(lib, id, 30);
            tracker.CastVote(vote);
            tracker.Finish();
        }

        var stats = lib.Library.GetRating(RatingSort.NetScore).Single();
        Assert.Equal(2, stats.Likes);
        Assert.Equal(1, stats.Dislikes);
        Assert.Equal(1, stats.Score);
    }

    [Fact]
    public void A_short_session_leaves_no_region_even_if_the_position_travelled_far()
    {
        // Watch time and media position are different quantities, and position reporting is
        // allowed to drift. Gating regions on the position span would let a nine-second
        // abandoned start paint heat on the seek bar.
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracker = lib.Library.StartSession(id, 0, () => clock);

        for (var i = 0; i < 9; i++)
        {
            clock = clock.AddSeconds(1);
            tracker.Update((i + 1) * 1200, isPlaying: true);   // position runs ahead of the clock
        }
        tracker.Finish();

        Assert.False(tracker.Counted);
        Assert.Equal(0, lib.Count("regions"));
    }

    [Fact]
    public void A_watched_stretch_is_recorded_as_a_region()
    {
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        Watch(lib, id, 60).Finish();

        var region = lib.Library.GetRegions(id).Single();
        Assert.Equal(0, region.StartMs);
        Assert.True(region.EndMs >= 59_000);
    }

    [Fact]
    public void Seeking_splits_one_region_into_two()
    {
        using var lib = new TempLibrary();
        var id = SeedVideo(lib);

        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracker = lib.Library.StartSession(id, 0, () => clock);

        long position = 0;
        for (var i = 0; i < 20; i++)
        {
            clock = clock.AddSeconds(1);
            position += 1000;
            tracker.Update(position, true);
        }

        position = 600_000;                      // the user drags the seek bar forward
        for (var i = 0; i < 20; i++)
        {
            clock = clock.AddSeconds(1);
            position += 1000;
            tracker.Update(position, true);
        }
        tracker.Finish();

        var regions = lib.Library.GetRegions(id);
        Assert.Equal(2, regions.Count);
        Assert.True(regions[0].EndMs < regions[1].StartMs);
    }
}
