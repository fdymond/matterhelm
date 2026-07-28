namespace HtpcMatterBridge.Ui;

/// <summary>Result of one <see cref="SettingsSearch.Filter"/> pass.</summary>
public sealed class SettingsSearchResult
{
    /// <summary>Creates a result.</summary>
    public SettingsSearchResult(bool isFiltering, IReadOnlySet<string> matchingSettingIds, IReadOnlySet<string> matchingCategoryIds)
    {
        IsFiltering = isFiltering;
        MatchingSettingIds = matchingSettingIds;
        MatchingCategoryIds = matchingCategoryIds;
    }

    /// <summary>False = the query was empty, everything is visible.</summary>
    public bool IsFiltering { get; }

    /// <summary>Ids of the settings that match (all of them when not filtering).</summary>
    public IReadOnlySet<string> MatchingSettingIds { get; }

    /// <summary>Ids of the categories with at least one matching setting (the nav grays out the rest).</summary>
    public IReadOnlySet<string> MatchingCategoryIds { get; }

    /// <summary>True iff the setting with <paramref name="settingId"/> should stay visible.</summary>
    public bool Matches(string settingId) => !IsFiltering || MatchingSettingIds.Contains(settingId);

    /// <summary>True iff the category with <paramref name="categoryId"/> has at least one visible setting.</summary>
    public bool CategoryMatches(string categoryId) => !IsFiltering || MatchingCategoryIds.Contains(categoryId);
}

/// <summary>
/// The settings window's search semantics (ADR-004 §4), as a pure function:
/// case-insensitive substring match of the query against each setting's label
/// and description plus its category title (a title hit keeps the whole
/// category visible). A blank/whitespace query filters nothing.
/// </summary>
public static class SettingsSearch
{
    /// <summary>Filters <paramref name="categories"/> by <paramref name="query"/>.</summary>
    public static SettingsSearchResult Filter(IReadOnlyList<SettingsCategory> categories, string query)
    {
        string needle = query.Trim();
        var settingIds = new HashSet<string>(StringComparer.Ordinal);
        var categoryIds = new HashSet<string>(StringComparer.Ordinal);

        if (needle.Length == 0)
        {
            foreach (SettingsCategory category in categories)
            {
                categoryIds.Add(category.Id);
                foreach (SettingDescriptor setting in category.Settings)
                {
                    settingIds.Add(setting.Id);
                }
            }

            return new SettingsSearchResult(isFiltering: false, settingIds, categoryIds);
        }

        foreach (SettingsCategory category in categories)
        {
            bool titleMatches = Contains(category.Title, needle);
            foreach (SettingDescriptor setting in category.Settings)
            {
                if (titleMatches || Contains(setting.Label, needle) || Contains(setting.Description, needle))
                {
                    settingIds.Add(setting.Id);
                    categoryIds.Add(category.Id);
                }
            }
        }

        return new SettingsSearchResult(isFiltering: true, settingIds, categoryIds);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
