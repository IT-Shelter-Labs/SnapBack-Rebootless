using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SnapBack.Core.Classification;
using SnapBack.Core.Execution;
using SnapBack.Core.Localization;
using SnapBack.Core.Memory;
using SnapBack.Core.Text;

namespace SnapBack.Core.Tests;

public sealed partial class LocalizationTests : IDisposable
{
    private static readonly string[] Cultures = ["ru", "zh-CN"];

    public LocalizationTests() => Loc.Culture = CultureInfo.GetCultureInfo("en");

    public void Dispose() => Loc.Culture = CultureInfo.GetCultureInfo("en");

    // ---- Resource files -----------------------------------------------------------------------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SnapBack.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    public static TheoryData<string> ResourceSets => new() { "src/SnapBack.Core/Localization", "src/SnapBack.App/Localization" };

    private static Dictionary<string, string> Read(string folder, string? culture)
    {
        var file = Path.Combine(RepoRoot(), folder, culture is null ? "Strings.resx" : $"Strings.{culture}.resx");
        return XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")?.Value ?? "");
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string[] Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void Every_string_is_translated_into_every_language(string folder)
    {
        var english = Read(folder, null);
        Assert.NotEmpty(english);
        foreach (var culture in Cultures)
        {
            var translated = Read(folder, culture);
            Assert.Empty(english.Keys.Except(translated.Keys));
            Assert.Empty(translated.Keys.Except(english.Keys));
            Assert.All(translated, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{culture}: {kv.Key} is empty"));
        }
    }

    [Theory]
    [MemberData(nameof(ResourceSets))]
    public void Translations_keep_the_same_placeholders(string folder)
    {
        var english = Read(folder, null);
        foreach (var culture in Cultures)
        {
            var translated = Read(folder, culture);
            foreach (var (key, value) in english)
            {
                Assert.True(Placeholders(value).SequenceEqual(Placeholders(translated[key])), $"{culture}: placeholders of {key} differ");
                _ = string.Format(CultureInfo.InvariantCulture, translated[key], new object[10]); // must be a valid format string
            }
        }
    }

    [Fact]
    public void Translations_are_actually_translated()
    {
        // Spot checks that the resource files are not copies of English.
        var en = Read("src/SnapBack.App/Localization", null);
        var ru = Read("src/SnapBack.App/Localization", "ru");
        var zh = Read("src/SnapBack.App/Localization", "zh-CN");
        Assert.Contains("baseline", en["Main_Restore"], StringComparison.OrdinalIgnoreCase);
        Assert.Matches("[А-Яа-яЁё]", ru["Main_Restore"]);
        Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", zh["Main_Restore"]);
        var different = en.Count(kv => kv.Value != ru[kv.Key]);
        Assert.True(different > en.Count * 0.9, "Most Russian strings should differ from English.");
    }

    [GeneratedRegex(@"(?:Loc|Tr)\.(?:Get|Format)\(""([A-Za-z0-9_]+)""|\{l:Tr ([A-Za-z0-9_]+)\}|Header\(""([A-Za-z0-9_]+)""")]
    private static partial Regex KeyUsage();

    [Fact]
    public void Every_key_used_in_the_code_exists()
    {
        var core = Read("src/SnapBack.Core/Localization", null);
        var app = Read("src/SnapBack.App/Localization", null);
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var missing = new List<string>();
        foreach (var file in files)
        {
            var inCore = file.Contains("SnapBack.Core", StringComparison.Ordinal) || file.Contains("SnapBack.Windows", StringComparison.Ordinal);
            foreach (Match m in KeyUsage().Matches(File.ReadAllText(file)))
            {
                var key = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
                if (key.EndsWith('_'))
                {
                    continue; // prefix of a dynamic key; covered by Dynamic_keys_exist_for_every_enum_value
                }

                var exists = core.ContainsKey(key) || (!inCore && app.ContainsKey(key));
                if (!exists)
                {
                    missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Dynamic_keys_exist_for_every_enum_value()
    {
        var core = Read("src/SnapBack.Core/Localization", null);
        foreach (var reason in Enum.GetValues<RiskReason>())
        {
            Assert.Contains($"Reason_{reason}_Label", core.Keys);
            Assert.Contains($"Reason_{reason}_Explain", core.Keys);
        }

        foreach (var outcome in Enum.GetValues<ItemOutcome>().Where(o => o != ItemOutcome.StillRunning))
        {
            Assert.Contains($"Outcome_{outcome}", core.Keys);
        }

        foreach (var pressure in Enum.GetValues<MemoryPressure>())
        {
            Assert.Contains($"Advice_{pressure}_Title", core.Keys);
            Assert.Contains($"Advice_{pressure}_Text", core.Keys);
        }

        foreach (var mode in Enum.GetValues<DeepCleanupMode>())
        {
            Assert.Contains($"Deep_Mode_{mode}", core.Keys);
        }
    }

    // ---- Runtime lookup -----------------------------------------------------------------------

    [Theory]
    [InlineData("en", "Started after baseline")]
    [InlineData("ru", "Запущено после baseline")]
    [InlineData("zh-CN", "基线之后启动")]
    public void Strings_follow_the_selected_language(string culture, string expected)
    {
        Loc.Culture = CultureInfo.GetCultureInfo(culture);
        Assert.Equal(expected, Loc.Get("Plan_StartedAfter"));
    }

    [Fact]
    public void Unsupported_language_falls_back_to_English()
    {
        Loc.Culture = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("Started after baseline", Loc.Get("Plan_StartedAfter"));
    }

    [Fact]
    public void Missing_key_returns_the_key_instead_of_throwing() =>
        Assert.Equal("No_Such_Key", Loc.Get("No_Such_Key"));

    [Fact]
    public void Every_reason_has_a_label_in_every_language()
    {
        foreach (var culture in new[] { "en", "ru", "zh-CN" })
        {
            Loc.Culture = CultureInfo.GetCultureInfo(culture);
            foreach (var reason in Enum.GetValues<RiskReason>())
            {
                Assert.DoesNotContain("Reason_", RiskReasons.Label(reason), StringComparison.Ordinal);
                Assert.DoesNotContain("Reason_", RiskReasons.Explain(reason), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Culture_changes_raise_an_event_once()
    {
        var raised = 0;
        void Handler(object? s, EventArgs e) => raised++;
        Loc.CultureChanged += Handler;
        try
        {
            Loc.Culture = CultureInfo.GetCultureInfo("ru");
            Loc.Culture = CultureInfo.GetCultureInfo("ru");
            Assert.Equal(1, raised);
        }
        finally
        {
            Loc.CultureChanged -= Handler;
        }
    }

    [Theory]
    [InlineData(1503238553L, "en", "1.4 GB")]
    [InlineData(1503238553L, "ru", "1,4 ГБ")]
    [InlineData(1503238553L, "zh-CN", "1.4 GB")]
    [InlineData(681574400L, "ru", "650 МБ")]
    public void Sizes_are_formatted_for_the_language(long bytes, string culture, string expected) =>
        Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.GetCultureInfo(culture)));

    // ---- Language resolution ------------------------------------------------------------------

    [Theory]
    [InlineData("system", "ru-RU", "ru")]
    [InlineData("system", "ru", "ru")]
    [InlineData("system", "zh-CN", "zh-CN")]
    [InlineData("system", "zh-SG", "zh-CN")]
    [InlineData("system", "zh-Hans", "zh-CN")]
    [InlineData("system", "zh-TW", "en")]
    [InlineData("system", "de-DE", "en")]
    [InlineData("system", "en-GB", "en")]
    [InlineData(null, "ru-RU", "ru")]
    [InlineData("", "zh-CN", "zh-CN")]
    [InlineData("ru", "en-US", "ru")]
    [InlineData("zh-cn", "en-US", "zh-CN")]
    [InlineData("en", "ru-RU", "en")]
    [InlineData("fr", "ru-RU", "en")]
    public void Setting_and_system_language_resolve_to_a_supported_language(string? setting, string system, string expected) =>
        Assert.Equal(expected, Languages.Resolve(setting, CultureInfo.GetCultureInfo(system)).Name);

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("ZH-CN", "zh-CN")]
    [InlineData("klingon", "system")]
    [InlineData(null, "system")]
    public void Stored_language_settings_are_normalized(string? stored, string expected) =>
        Assert.Equal(expected, Languages.NormalizeSetting(stored));

    [Fact]
    public void Native_language_names_do_not_depend_on_the_ui_language()
    {
        Loc.Culture = CultureInfo.GetCultureInfo("zh-CN");
        Assert.Equal("Русский", Languages.NativeName("ru"));
        Assert.Equal("简体中文", Languages.NativeName("zh-CN"));
        Assert.Equal("English", Languages.NativeName("en"));
    }
}
