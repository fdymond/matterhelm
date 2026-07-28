using HtpcMatterBridge.Ui;
using Xunit;

namespace HtpcMatterBridge.Tests;

/// <summary>
/// Behaviour tests for <see cref="SettingsSearch"/> (ADR-004 §4 search
/// semantics): case-insensitive substring over label + description + category
/// title, category-title hits keeping the whole category, blank queries
/// filtering nothing, and the category-match set the nav grays out from.
/// Runs against a small fixture schema (semantics don't depend on the real
/// one) plus a couple of real-schema smoke checks.
/// </summary>
public sealed class SettingsSearchTests
{
    private static readonly IReadOnlyList<SettingsCategory> Fixture =
    [
        new SettingsCategory
        {
            Id = "general",
            Title = "General",
            Settings =
            [
                new SettingDescriptor { Id = "ipc-port", Label = "IPC port", Description = "Loopback TCP port.", Kind = SettingKind.Port },
                new SettingDescriptor { Id = "log-level", Label = "Log level", Description = "Sidecar log detail.", Kind = SettingKind.Choice },
            ],
        },
        new SettingsCategory
        {
            Id = "overlay",
            Title = "Overlay",
            Settings =
            [
                new SettingDescriptor { Id = "overlay-enabled", Label = "Overlay pop-ups", Description = "Flash on commands.", Kind = SettingKind.Toggle },
            ],
        },
    ];

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankQueryMatchesEverythingAndReportsNotFiltering(string query)
    {
        SettingsSearchResult result = SettingsSearch.Filter(Fixture, query);

        Assert.False(result.IsFiltering);
        Assert.Equal(["ipc-port", "log-level", "overlay-enabled"], result.MatchingSettingIds.Order());
        Assert.Equal(["general", "overlay"], result.MatchingCategoryIds.Order());
        Assert.True(result.Matches("ipc-port"));
        Assert.True(result.CategoryMatches("overlay"));
    }

    [Fact]
    public void LabelSubstringMatchIsCaseInsensitive()
    {
        SettingsSearchResult result = SettingsSearch.Filter(Fixture, "iPc PoRt");

        Assert.True(result.IsFiltering);
        Assert.Equal(["ipc-port"], result.MatchingSettingIds);
        Assert.Equal(["general"], result.MatchingCategoryIds);
    }

    [Fact]
    public void DescriptionSubstringAlsoMatches()
    {
        SettingsSearchResult result = SettingsSearch.Filter(Fixture, "loopback");

        Assert.Equal(["ipc-port"], result.MatchingSettingIds);
    }

    [Fact]
    public void CategoryTitleMatchKeepsEveryRowOfThatCategory()
    {
        SettingsSearchResult result = SettingsSearch.Filter(Fixture, "general");

        Assert.Equal(["ipc-port", "log-level"], result.MatchingSettingIds.Order());
        Assert.Equal(["general"], result.MatchingCategoryIds);
    }

    [Fact]
    public void QueryWithNoMatchesReturnsEmptySetsButStillFilters()
    {
        SettingsSearchResult result = SettingsSearch.Filter(Fixture, "zzz-nothing");

        Assert.True(result.IsFiltering);
        Assert.Empty(result.MatchingSettingIds);
        Assert.Empty(result.MatchingCategoryIds);
        Assert.False(result.Matches("ipc-port"));
        Assert.False(result.CategoryMatches("general"));
    }

    [Fact]
    public void SurroundingWhitespaceInTheQueryIsIgnored()
    {
        SettingsSearchResult result = SettingsSearch.Filter(Fixture, "  port  ");

        Assert.Equal(["ipc-port"], result.MatchingSettingIds);
    }

    [Fact]
    public void RealSchemaPortQueryFindsTheIpcPortAndDropsUnrelatedRows()
    {
        SettingsSearchResult result = SettingsSearch.Filter(SettingsViewModel.Categories, "port");

        Assert.Contains("ipc-port", result.MatchingSettingIds);
        Assert.DoesNotContain("speaker-name", result.MatchingSettingIds);
        Assert.DoesNotContain("log-level", result.MatchingSettingIds);
    }

    [Fact]
    public void RealSchemaEveryRowIsReachableByItsOwnLabel()
    {
        foreach (SettingsCategory category in SettingsViewModel.Categories)
        {
            foreach (SettingDescriptor setting in category.Settings)
            {
                SettingsSearchResult result = SettingsSearch.Filter(SettingsViewModel.Categories, setting.Label);
                Assert.Contains(setting.Id, result.MatchingSettingIds);
                Assert.Contains(category.Id, result.MatchingCategoryIds);
            }
        }
    }
}
