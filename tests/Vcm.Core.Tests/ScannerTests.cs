using Vcm.Core;
using Xunit;

namespace Vcm.Core.Tests;

public class ScannerTests
{
    [Fact]
    public void Rescanning_unchanged_files_rehashes_nothing()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("a.mp4");
        lib.MakeVideo("sub/b.mkv");

        var first = lib.Scan();
        Assert.Equal(2, first.Added);

        var second = lib.Scan();
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Removed);
        Assert.Equal(2, second.Unchanged);
    }

    [Fact]
    public void Renaming_a_file_keeps_its_identity_and_history()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        var original = lib.MakeVideo("talk.mp4", seed: 7);
        lib.Scan();

        var videoId = lib.Library.GetAllVideos().Single().VideoId;
        lib.Library.StartSession(videoId).Finish();
        lib.Library.AddTag(videoId, "keepme");

        File.Move(original, Path.Combine(lib.Root, "renamed.mp4"));
        lib.Scan();

        // Same content, so the same video row — statistics and tags ride along.
        var after = lib.Library.GetAllVideos().Single();
        Assert.Equal(videoId, after.VideoId);
        Assert.Equal("renamed.mp4", after.DisplayName);
        Assert.Equal(1, lib.Count("videos"));
        Assert.Equal(1, lib.Count("files"));
        Assert.Contains("keepme", after.Tags);
    }

    [Fact]
    public void Moving_a_file_between_roots_keeps_one_video()
    {
        using var lib = new TempLibrary();
        var a = lib.AddRoot("rootA");
        var b = lib.AddRoot("rootB");
        var file = lib.MakeVideo("rootA/clip.mp4", seed: 11);
        lib.Scan();

        var videoId = lib.Library.GetAllVideos().Single().VideoId;

        File.Move(file, Path.Combine(b, "clip.mp4"));
        lib.Scan();

        Assert.Equal(videoId, lib.Library.GetAllVideos().Single().VideoId);
        Assert.Equal(1, lib.Count("files"));
    }

    [Fact]
    public void Deleting_a_file_drops_the_file_row_but_keeps_the_history()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        var file = lib.MakeVideo("gone.mp4");
        lib.Scan();

        var videoId = lib.Library.GetAllVideos().Single().VideoId;
        lib.Library.StartSession(videoId).Finish();

        File.Delete(file);
        var result = lib.Scan();

        Assert.Equal(1, result.Removed);
        Assert.Equal(0, lib.Count("files"));
        Assert.Equal(1, lib.Count("videos"));    // history survives in case it comes back
        Assert.Equal(1, lib.Count("sessions"));
    }

    [Fact]
    public void Identical_content_at_two_paths_is_one_video_with_two_files()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("one/same.mp4", seed: 42);
        lib.MakeVideo("two/copy.mp4", seed: 42);
        lib.Scan();

        Assert.Equal(1, lib.Count("videos"));
        Assert.Equal(2, lib.Count("files"));

        var (video, paths) = lib.Library.GetDuplicates().Single();
        Assert.Equal(2, video.FileCount);
        Assert.Equal(2, paths.Count);
    }

    [Fact]
    public void A_cancelled_scan_deletes_nothing()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("a.mp4");
        lib.MakeVideo("b.mp4");
        lib.Scan();
        Assert.Equal(2, lib.Count("files"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => lib.Scan(cts.Token));

        // The delete sweep must never run on a partial picture of the disk.
        Assert.Equal(2, lib.Count("files"));
    }

    [Fact]
    public void A_root_that_has_vanished_does_not_empty_the_library()
    {
        using var lib = new TempLibrary();
        var a = lib.AddRoot("rootA");
        var b = lib.AddRoot("rootB");
        lib.MakeVideo("rootA/keep.mp4", seed: 3);
        lib.MakeVideo("rootB/alsokeep.mp4", seed: 4);
        lib.Scan();
        Assert.Equal(2, lib.Count("files"));

        // An unmounted drive or a renamed folder. The scan can say nothing about what is still
        // under it, so its rows must survive — a library-wide delete sweep would wipe them.
        Directory.Move(a, a + "-renamed");
        var result = lib.Scan();

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, lib.Count("files"));
        Assert.True(result.Skipped > 0);
        Assert.NotNull(result.FirstSkipped);
    }

    [Fact]
    public void Editing_a_file_in_place_is_picked_up_as_changed()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        var file = lib.MakeVideo("edit.mp4", sizeBytes: 4096, seed: 5);
        lib.Scan();

        File.WriteAllBytes(file, Enumerable.Range(0, 8192).Select(i => (byte)(i % 97)).ToArray());
        var result = lib.Scan();

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, lib.Count("files"));
    }

    [Fact]
    public void Non_video_files_are_ignored()
    {
        using var lib = new TempLibrary();
        lib.AddRoot();
        lib.MakeVideo("real.mp4");
        File.WriteAllText(Path.Combine(lib.Root, "notes.txt"), "not a video");
        File.WriteAllText(Path.Combine(lib.Root, "cover.jpg"), "not a video");

        Assert.Equal(1, lib.Scan().Added);
    }

    [Fact]
    public void Roots_may_not_nest_in_either_direction()
    {
        using var lib = new TempLibrary();
        lib.AddRoot("outer");
        Directory.CreateDirectory(Path.Combine(lib.Root, "outer", "inner"));

        Assert.Null(lib.Library.AddRoot(Path.Combine(lib.Root, "outer", "inner"), out var inside));
        Assert.Contains("Already covered", inside);

        Assert.Null(lib.Library.AddRoot(lib.Root, out var contains));
        Assert.Contains("contains the existing root", contains);
    }
}
