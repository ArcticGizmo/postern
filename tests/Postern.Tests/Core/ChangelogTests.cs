using Postern.Core.Changelog;

namespace Postern.Tests.Core;

public class ChangelogTests
{
    const string Sample = """
        # Changelog

        Some preamble.

        ---

        ## [Unreleased]

        - work in progress

        ---

        ## [v0.2.11] - 2026-07-21

        - Stopped the window hopping to the front

        ---

        ## [v0.2.10] - 2026-07-20

        - Named connections
        - Templates for Jira and Octopus

        ---

        ## [v0.2.9] - 2026-07-19

        - An audit log

        ---
        """;

    [Fact]
    public void Parse_returns_a_section_per_h2_heading()
    {
        var sections = ChangelogParser.Parse(Sample);

        Assert.Equal(4, sections.Count);
        Assert.Null(sections[0].Version);
        Assert.Equal("v0.2.11", sections[1].Display);
        Assert.Equal(new Version(0, 2, 11), sections[1].Version);
    }

    [Fact]
    public void Parse_drops_the_trailing_rule_and_blank_lines()
    {
        var section = ChangelogParser.Parse(Sample)[1];

        Assert.Equal("## [v0.2.11] - 2026-07-21", section.Block[0]);
        Assert.DoesNotContain("---", section.Block);
        Assert.NotEqual("", section.Block[^1].Trim());
    }

    [Fact]
    public void Parse_handles_CRLF() =>
        Assert.Equal("## [v0.2.11] - 2026-07-21", ChangelogParser.Parse(Sample.Replace("\n", "\r\n"))[1].Block[0]);

    [Fact]
    public void One_version_up_shows_one_section() =>
        Assert.Equal(["v0.2.11"], ChangelogParser.UnseenSince(Sample, "v0.2.10", "v0.2.11").Select(s => s.Display));

    [Fact]
    public void Skipped_versions_all_show_newest_first_without_unreleased() =>
        Assert.Equal(["v0.2.11", "v0.2.10", "v0.2.9"], ChangelogParser.UnseenSince(Sample, "v0.2.8", "v0.2.11").Select(s => s.Display));

    [Fact]
    public void Nothing_newer_than_the_running_version_shows() =>
        Assert.Equal(["v0.2.10", "v0.2.9"], ChangelogParser.UnseenSince(Sample, "v0.2.8", "v0.2.10").Select(s => s.Display));

    [Fact]
    public void Same_version_shows_nothing() => Assert.Empty(ChangelogParser.UnseenSince(Sample, "v0.2.11", "v0.2.11"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void No_previous_version_shows_nothing(string? lastSeen) => Assert.Empty(ChangelogParser.UnseenSince(Sample, lastSeen, "v0.2.11"));

    [Fact]
    public void The_leading_v_is_optional() =>
        Assert.Equal(["v0.2.11"], ChangelogParser.UnseenSince(Sample, "0.2.10", "0.2.11").Select(s => s.Display));
}
