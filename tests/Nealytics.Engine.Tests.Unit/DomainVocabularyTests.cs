using System.Text.RegularExpressions;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// The engine must not know any one product's words: not in an identifier, a string, a comment, a
/// SQL file or a configuration file. A deployment declares its own vocabulary at runtime.
/// </summary>
public class DomainVocabularyTests
{
    /// <summary>
    /// Words that belong to a product rather than to analytics, from several verticals so the test
    /// says something about the property rather than about one deployment. "post" is absent
    /// because MapPost and the HTTP verb make it indistinguishable from ordinary engine code.
    /// </summary>
    private static readonly string[] Vertical =
    [
        "menu", "restaurant", "venue", "dish", "cuisine", "diner", "waiter", "allergen", "qr",
        "product", "sku", "basket", "checkout", "storefront",
        "tweet", "follower", "playlist", "podcast",
        "patient", "invoice", "flight", "booking",
        "neavents", "smartmenu", "cloudflare",
    ];

    private static readonly HashSet<string> VerticalSet = BuildForms(Vertical);

    private static HashSet<string> BuildForms(IEnumerable<string> words)
    {
        HashSet<string> forms = new(StringComparer.OrdinalIgnoreCase);
        foreach (string word in words)
        {
            forms.Add(word);
            forms.Add(word + "s");
            forms.Add(word + "es");
        }

        return forms;
    }

    private static readonly Regex Word = new(@"[A-Za-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Splits text into words and each word into its camelCase and snake_case parts, so a match is
    /// a whole word: "revenue" is not "venue", and SetPreflightMaxAge is not "flight".
    /// </summary>
    private static IEnumerable<string> Segments(string text) =>
        Word.Matches(text)
            .SelectMany(match => Regex.Split(match.Value, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])"))
            .Where(part => part.Length > 0);

    internal static IReadOnlyList<string> Violations(string text) =>
        Segments(text).Where(VerticalSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> EngineTextFiles() =>
        [
            .. RepositoryFiles.EngineFiles().Select(RepositoryFiles.Relative),
            .. RepositoryFiles.RootFiles("clickhouse-init.sql", "Dockerfile", "docker-compose*.yml").Select(RepositoryFiles.Relative),
        ];

    public static TheoryData<string> EngineText() => new(EngineTextFiles());

    [Fact]
    public void TheScanCoversTheEngine()
    {
        List<string> files = EngineTextFiles();

        files.Should().Contain(Path.Combine("src", "Nealytics.Engine", "Program.cs"));
        files.Should().Contain(Path.Combine("src", "Nealytics.Engine", "appsettings.json"));
        files.Should().Contain(Path.Combine("src", "Nealytics.Engine", "Nealytics.Engine.csproj"));
        files.Should().Contain("clickhouse-init.sql");
        files.Should().HaveCountGreaterThan(50);
    }

    [Theory]
    [MemberData(nameof(EngineText))]
    public void NoProductVocabularyAppearsInEngineText(string relativePath)
    {
        string text = File.ReadAllText(Path.Combine(RepositoryFiles.Root().FullName, relativePath));

        Violations(text).Should().BeEmpty(
            "the engine holds dimensions it was told about and never knows their names; a product "
            + "word anywhere in it, a doc string included, is a deployment leaking into every clone");
    }

    [Theory]
    [InlineData("""string label = "menu";""", "menu")]
    [InlineData("/// <summary>Counts each venue.</summary>", "venue")]
    [InlineData("int MenuId = 0;", "Menu")]
    [InlineData("SELECT menu_id FROM t", "menu")]
    [InlineData("""activity?.SetTag("neavents.project_id", id);""", "neavents")]
    [InlineData("-- scanned from a QR code", "QR")]
    [InlineData("""{ "Name": "menus" }""", "menus")]
    public void TheGuardWouldActuallyFail(string text, string expected)
    {
        Violations(text).Should().Contain(expected);
    }

    [Theory]
    [InlineData("SetPreflightMaxAge")]
    [InlineData("Description")]
    [InlineData("DataTable")]
    [InlineData("TableName")]
    [InlineData("revenue")]
    [InlineData("MapPost")]
    [InlineData("productivity")]
    public void OrdinaryEnglishIsNotAViolation(string text)
    {
        Violations(text).Should().BeEmpty("a guard that cries wolf is turned off within a week");
    }
}
