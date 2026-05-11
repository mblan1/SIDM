using SIDM.Core.Models;
using SIDM.Core.Scheduling;

namespace SIDM.Core.Tests.Scheduling;

public class CategoryMatcherTests
{
    [Fact]
    public void No_categories_returns_null()
    {
        CategoryMatcher.Match(Array.Empty<Category>(), "movie.mp4").Should().BeNull();
    }

    [Fact]
    public void Matches_by_filename_extension()
    {
        var cats = new[]
        {
            Cat("Video", "mp4,mkv,webm"),
            Cat("Audio", "mp3,flac"),
        };

        CategoryMatcher.Match(cats, "vacation.MKV")!.Name.Should().Be("Video");
        CategoryMatcher.Match(cats, "song.mp3")!.Name.Should().Be("Audio");
    }

    [Fact]
    public void Matches_when_given_just_an_extension()
    {
        var cats = new[] { Cat("Video", "mp4,mkv") };

        CategoryMatcher.Match(cats, "mp4")!.Name.Should().Be("Video");
        CategoryMatcher.Match(cats, ".mp4")!.Name.Should().Be("Video");
        CategoryMatcher.Match(cats, "MP4")!.Name.Should().Be("Video");
    }

    [Fact]
    public void Tolerates_mixed_separators_and_whitespace()
    {
        var cats = new[] { Cat("Mix", " mp4 ; mkv, webm") };

        CategoryMatcher.Match(cats, "movie.webm")!.Name.Should().Be("Mix");
    }

    [Fact]
    public void Returns_null_when_no_extension_matches()
    {
        var cats = new[] { Cat("Video", "mp4,mkv") };

        CategoryMatcher.Match(cats, "doc.pdf").Should().BeNull();
        CategoryMatcher.Match(cats, "noext").Should().BeNull();
        CategoryMatcher.Match(cats, "trailing.").Should().BeNull();
    }

    [Fact]
    public void First_match_wins_when_two_categories_claim_the_same_extension()
    {
        var cats = new[]
        {
            Cat("Software", "iso,exe"),
            Cat("Images", "iso,img"),
        };

        CategoryMatcher.Match(cats, "ubuntu.iso")!.Name.Should().Be("Software");
    }

    [Fact]
    public void Categories_with_null_or_empty_extensions_are_skipped()
    {
        var cats = new[]
        {
            new Category { Name = "Empty", Extensions = null },
            Cat("Video", "mp4"),
        };

        CategoryMatcher.Match(cats, "x.mp4")!.Name.Should().Be("Video");
    }

    [Fact]
    public void Normalize_handles_paths_dots_and_case()
    {
        CategoryMatcher.Normalize("C:\\stuff\\thing.TXT").Should().Be("txt");
        CategoryMatcher.Normalize(".gz").Should().Be("gz");
        CategoryMatcher.Normalize("  ").Should().Be("");
        CategoryMatcher.Normalize("noext").Should().Be("noext");
    }

    private static Category Cat(string name, string extensions) =>
        new() { Name = name, Extensions = extensions };
}
