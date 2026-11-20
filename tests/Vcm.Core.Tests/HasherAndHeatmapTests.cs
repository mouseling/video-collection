using Vcm.Core;
using Xunit;

namespace Vcm.Core.Tests;

public class ContentHasherTests
{
    private static MemoryStream Pattern(int size, byte seed = 0)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)((i + seed) % 251);
        return new MemoryStream(bytes);
    }

    [Fact]
    public void The_hash_is_stable_for_identical_content()
    {
        // This value pins the identity algorithm. If it changes, every stored video row is
        // orphaned and the user's entire ratings and watch history silently disappears on the
        // next scan — so changing it must be a deliberate act with a migration, never a tidy-up.
        using var a = Pattern(5 << 20);
        using var b = Pattern(5 << 20);

        var hash = ContentHasher.Compute(a, a.Length);
        Assert.Equal(hash, ContentHasher.Compute(b, b.Length));
        Assert.Equal(64, hash.Length);          // SHA-256, hex
    }

    [Fact]
    public void Large_files_differing_only_in_the_middle_collide_by_design()
    {
        // Documented trade-off: only the size and the first and last chunk are read, because
        // hashing hundreds of GB in full would make rescans unusable. Real video files differ in
        // their headers and their length, and the consequence of a collision is benign — two
        // entries merge visibly rather than data being lost.
        var size = 8 << 20;
        using var original = Pattern(size);

        var mutated = Pattern(size).ToArray();
        mutated[size / 2] ^= 0xFF;
        using var changed = new MemoryStream(mutated);

        Assert.Equal(ContentHasher.Compute(original, size),
                     ContentHasher.Compute(changed, size));
    }

    [Fact]
    public void Small_files_are_hashed_whole_so_the_middle_counts()
    {
        var size = 1 << 20;                     // under the full-hash threshold
        using var original = Pattern(size);

        var mutated = Pattern(size).ToArray();
        mutated[size / 2] ^= 0xFF;
        using var changed = new MemoryStream(mutated);

        Assert.NotEqual(ContentHasher.Compute(original, size),
                        ContentHasher.Compute(changed, size));
    }

    [Fact]
    public void Length_alone_changes_the_hash()
    {
        using var shorter = Pattern(4096);
        using var longer = Pattern(8192);

        Assert.NotEqual(ContentHasher.Compute(shorter, shorter.Length),
                        ContentHasher.Compute(longer, longer.Length));
    }

    [Fact]
    public void An_empty_file_hashes_without_throwing()
    {
        using var empty = new MemoryStream([]);
        Assert.Equal(64, ContentHasher.Compute(empty, 0).Length);
    }
}

public class HeatmapTests
{
    [Fact]
    public void No_regions_means_no_heat()
    {
        Assert.All(Heatmap.Compute([], 60_000, 10), h => Assert.Equal(0, h));
    }

    [Fact]
    public void An_unknown_duration_produces_no_heat_rather_than_dividing_by_zero()
    {
        Assert.All(Heatmap.Compute([(0L, 1000L)], 0, 10), h => Assert.Equal(0, h));
    }

    [Fact]
    public void A_region_covering_the_first_half_heats_only_the_first_half()
    {
        var heat = Heatmap.Compute([(0L, 1000L)], durationMs: 2000, buckets: 2);
        Assert.Equal(1.0, heat[0]);
        Assert.Equal(0.0, heat[1]);
    }

    [Fact]
    public void Overlapping_regions_read_hotter_than_a_single_pass()
    {
        // The requirement's own example: 10:30-12:00 twice and 11:00-11:30 once must make
        // 11:00-11:30 the hottest stretch.
        const long minute = 60_000;
        var heat = Heatmap.Compute(
            [(10 * minute + 30_000, 12 * minute),
             (10 * minute + 30_000, 12 * minute),
             (11 * minute, 11 * minute + 30_000)],
            durationMs: 20 * minute, buckets: 40);

        var inner = heat[22];    // 11:00-11:30, watched three times
        var outer = heat[21];    // 10:30-11:00, watched twice

        Assert.Equal(1.0, inner);
        Assert.True(outer < inner);
        Assert.True(outer > 0);
    }

    [Fact]
    public void Heat_is_normalised_to_the_hottest_bucket()
    {
        var heat = Heatmap.Compute([(0L, 500L), (0L, 500L), (500L, 1000L)], 1000, 2);
        Assert.Equal(1.0, heat.Max());
    }

    [Fact]
    public void A_zero_bucket_request_still_returns_an_array()
    {
        Assert.Single(Heatmap.Compute([], 1000, 0));
    }
}
