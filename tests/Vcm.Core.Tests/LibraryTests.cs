using Vcm.Core;
using Xunit;

namespace Vcm.Core.Tests;

public class LibraryTests
{
    private static long SeedWithCopies(TempLibrary lib, int copies)
    {
        lib.AddRoot();
        for (var i = 0; i < copies; i++) lib.MakeVideo($"copy{i}/same.mp4", seed: 99);
        lib.Scan();
        return lib.Library.GetAllVideos().Single().VideoId;
    }

    [Fact]
    public void Statistics_do_not_fan_out_when_a_video_has_several_copies()
    {
        // The aggregate subqueries are pre-grouped by video before joining; if that ever changed,
        // a video with two files on disk would report double its watch time.
        using var lib = new TempLibrary();
        var id = SeedWithCopies(lib, copies: 2);

        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var session = 0; session < 3; session++)
        {
            var tracker = lib.Library.StartSession(id, 0, () => clock);
            for (var i = 0; i < 30; i++)
            {
                clock = clock.AddSeconds(1);
                tracker.Update((i + 1) * 1000, true);
            }
            tracker.CastVote(1);
            tracker.Finish();
        }

        var viewed = lib.Library.GetMostViewed(ViewedSort.TotalTime);
        var stats = Assert.Single(viewed);
        Assert.Equal(3, stats.Views);
        Assert.Equal(90_000, stats.TotalWatchedMs);
        Assert.Equal(3, stats.Likes);
        Assert.Equal(2, stats.FileCount);
    }

    [Fact]
    public void A_folder_listing_shows_every_copy_on_disk()
    {
        // Deliberately different from the video-level grids: Locations mirrors the filesystem.
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("folder/a.mp4", seed: 50);
        lib.MakeVideo("folder/b.mp4", seed: 50);    // identical content, same folder
        lib.Scan();

        Assert.Equal(1, lib.Count("videos"));
        Assert.Equal(2, lib.Library.GetVideosInFolder(Path.Combine(lib.Root, "folder")).Count);
    }

    [Fact]
    public void A_folder_listing_can_include_subfolders()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("top/a.mp4", seed: 1);
        lib.MakeVideo("top/deep/b.mp4", seed: 2);
        lib.Scan();

        var top = Path.Combine(lib.Root, "top");
        Assert.Single(lib.Library.GetVideosInFolder(top));
        Assert.Equal(2, lib.Library.GetVideosInFolder(top, recursive: true).Count);
    }

    [Fact]
    public void A_sibling_folder_with_a_shared_prefix_is_not_included()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("Videos/a.mp4", seed: 1);
        lib.MakeVideo("VideosOld/b.mp4", seed: 2);
        lib.Scan();

        Assert.Single(lib.Library.GetVideosInFolder(Path.Combine(lib.Root, "Videos"), recursive: true));
    }

    [Fact]
    public void The_folder_tree_rolls_counts_up_through_ancestors()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("a/x.mp4", seed: 1);
        lib.MakeVideo("a/b/y.mp4", seed: 2);
        lib.MakeVideo("a/b/z.mp4", seed: 3);
        lib.Scan();

        var root = lib.Library.GetFolderTree().Single();
        Assert.Equal(3, root.TotalCount);

        var a = root.Children.Single(c => c.Name == "a");
        Assert.Equal(1, a.DirectCount);
        Assert.Equal(3, a.TotalCount);

        var b = a.Children.Single(c => c.Name == "b");
        Assert.Equal(2, b.DirectCount);
    }

    [Fact]
    public void Removing_a_root_keeps_ratings_and_history()
    {
        using var lib = new TempLibrary();
        var path = lib.AddRoot("films");
        lib.MakeVideo("films/f.mp4");
        lib.Scan();

        var id = lib.Library.GetAllVideos().Single().VideoId;
        var tracker = lib.Library.StartSession(id);
        tracker.CastVote(1);
        tracker.Finish();

        var rootId = lib.Library.GetRoots().Single().Id;
        lib.Library.RemoveRoot(rootId);

        Assert.Empty(lib.Library.GetAllVideos());          // nothing to show...
        Assert.Equal(1, lib.Count("videos"));              // ...but the history is still there
        Assert.Equal(1, lib.Count("sessions"));

        // Re-registering the folder brings it back attached to the same history.
        lib.Library.AddRoot(path, out _);
        lib.Scan();
        Assert.Equal(id, lib.Library.GetAllVideos().Single().VideoId);
        Assert.Equal(1, lib.Library.GetRating(RatingSort.NetScore).Single().Likes);
    }

    [Fact]
    public void The_summary_does_not_count_videos_whose_files_have_gone()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        var file = lib.MakeVideo("x.mp4");
        lib.Scan();

        File.Delete(file);
        lib.Scan();

        var (videos, files, bytes) = lib.Library.GetLibrarySize();
        Assert.Equal(0, videos);     // reporting "1 video · 0 B" above an empty library is a lie
        Assert.Equal(0, files);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Tags_are_created_once_and_reused()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("a.mp4", seed: 1);
        lib.MakeVideo("b.mp4", seed: 2);
        lib.Scan();

        var videos = lib.Library.GetAllVideos();
        lib.Library.AddTag(videos[0].VideoId, "docs");
        lib.Library.AddTag(videos[1].VideoId, "docs");
        lib.Library.AddTag(videos[0].VideoId, "docs");    // idempotent

        var tag = Assert.Single(lib.Library.GetTags());
        Assert.Equal("docs", tag.Name);
        Assert.Equal(2, tag.VideoCount);
        Assert.Equal(2, lib.Library.GetVideosByTag(tag.Id).Count);
    }

    [Fact]
    public void Rating_sorts_rank_differently_where_it_matters()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("many.mp4", seed: 1);
        lib.MakeVideo("perfect.mp4", seed: 2);
        lib.Scan();

        var videos = lib.Library.GetAllVideos();
        var many = videos.Single(v => v.DisplayName == "many.mp4").VideoId;
        var perfect = videos.Single(v => v.DisplayName == "perfect.mp4").VideoId;

        void Vote(long id, int vote)
        {
            var t = lib.Library.StartSession(id);
            t.CastVote(vote);
            t.Finish();
        }

        for (var i = 0; i < 8; i++) Vote(many, 1);
        for (var i = 0; i < 3; i++) Vote(many, -1);
        for (var i = 0; i < 2; i++) Vote(perfect, 1);

        // 8↑/3↓ wins on net score (+5 vs +2); 2↑/0↓ wins on ratio (100% vs 73%).
        Assert.Equal("many.mp4", lib.Library.GetRating(RatingSort.NetScore)[0].DisplayName);
        Assert.Equal("perfect.mp4", lib.Library.GetRating(RatingSort.Ratio)[0].DisplayName);
    }

    [Fact]
    public void Opening_a_database_from_a_newer_schema_is_refused()
    {
        using var lib = new TempLibrary();
        var path = Path.Combine(lib.Root, "future.db");

        using (var db = new Database(path))
            db.Exec($"PRAGMA user_version={Database.SchemaVersion + 1};");

        var error = Assert.Throws<InvalidOperationException>(() => new Database(path));
        Assert.Contains("newer version", error.Message);
    }

    [Fact]
    public void A_backup_is_written_and_is_itself_a_readable_library()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("a.mp4");
        lib.Scan();

        lib.Db.BackupIfStale(TimeSpan.Zero);

        var backup = Path.Combine(lib.Root, "library.db.bak");
        Assert.True(File.Exists(backup));

        using var restored = new Database(backup);
        Assert.Equal(1, Convert.ToInt64(restored.Scalar("SELECT COUNT(*) FROM files")));
    }
}

public class SearchAndRenameTests
{
    private static TempLibrary Seeded()
    {
        var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("talks/Vulkan render graph.mp4", seed: 1);
        lib.MakeVideo("talks/SIMD tricks.mkv", seed: 2);
        lib.MakeVideo("archive/holiday.mp4", seed: 3);
        lib.Scan();
        return lib;
    }

    [Fact]
    public void Search_matches_part_of_a_file_name()
    {
        using var lib = Seeded();
        var hit = Assert.Single(lib.Library.Search("SIMD"));
        Assert.Equal("SIMD tricks.mkv", hit.DisplayName);
    }

    [Fact]
    public void Search_is_case_insensitive_and_matches_folders_too()
    {
        using var lib = Seeded();
        Assert.Equal(2, lib.Library.Search("TALKS").Count);
        Assert.Single(lib.Library.Search("holiday"));
    }

    [Fact]
    public void Search_for_nothing_returns_nothing()
    {
        using var lib = Seeded();
        Assert.Empty(lib.Library.Search("   "));
        Assert.Empty(lib.Library.Search("no such video"));
    }

    [Fact]
    public void Renaming_moves_the_file_and_keeps_its_history()
    {
        using var lib = Seeded();
        var video = lib.Library.Search("holiday").Single();

        var tracker = lib.Library.StartSession(video.VideoId);
        tracker.CastVote(1);
        tracker.Finish();
        lib.Library.AddTag(video.VideoId, "summer");

        Assert.True(lib.Library.RenameFile(video.Path, "Holiday 2026.mp4", out var error), error);

        Assert.False(File.Exists(video.Path));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(video.Path)!, "Holiday 2026.mp4")));

        var after = lib.Library.Search("Holiday 2026").Single();
        Assert.Equal(video.VideoId, after.VideoId);     // same content, same history
        Assert.Equal(1, after.Likes);
        Assert.Contains("summer", after.Tags);

        // And a rescan must not treat it as a new file or delete anything.
        var result = lib.Scan();
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Renaming_onto_an_existing_name_is_refused()
    {
        using var lib = Seeded();
        var one = lib.Library.Search("Vulkan").Single();

        Assert.False(lib.Library.RenameFile(one.Path, "SIMD tricks.mkv", out var error));
        Assert.Contains("already exists", error);
        Assert.True(File.Exists(one.Path));          // untouched
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Renaming_to_an_empty_name_is_refused(string name)
    {
        using var lib = Seeded();
        var one = lib.Library.Search("Vulkan").Single();
        Assert.False(lib.Library.RenameFile(one.Path, name, out var error));
        Assert.Contains("empty", error);
    }

    [Fact]
    public void Renaming_a_file_that_is_gone_is_refused()
    {
        using var lib = Seeded();
        var one = lib.Library.Search("Vulkan").Single();
        File.Delete(one.Path);

        Assert.False(lib.Library.RenameFile(one.Path, "whatever.mp4", out var error));
        Assert.Contains("no longer on disk", error);
    }
}
