using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// CONTRIBUTING.md: no comments in engine source, SQL or configuration. Reasoning that names cannot
/// carry belongs in docs/design-notes.md, where it is read, rather than beside code it drifts from.
/// </summary>
public class EngineSourceStyleTests
{
    private static readonly SyntaxKind[] CommentKinds =
    [
        SyntaxKind.SingleLineCommentTrivia,
        SyntaxKind.MultiLineCommentTrivia,
        SyntaxKind.SingleLineDocumentationCommentTrivia,
        SyntaxKind.MultiLineDocumentationCommentTrivia,
    ];

    internal static IReadOnlyList<int> CSharpCommentLines(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse));

        return tree.GetRoot()
            .DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => CommentKinds.Contains(trivia.Kind()))
            .Select(trivia => trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .Distinct()
            .ToList();
    }

    private static readonly Regex SqlStringLiteral = new(@"'(?:[^'\\]|\\.|'')*'", RegexOptions.Compiled);

    internal static bool SqlHasComment(string sql)
    {
        string code = SqlStringLiteral.Replace(sql, "''");
        return code.Contains("--", StringComparison.Ordinal) || code.Contains("/*", StringComparison.Ordinal);
    }

    internal static IReadOnlyList<string> JsonCommentKeys(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
        });

        List<string> keys = [];
        Collect(document.RootElement, keys);
        return keys;
    }

    private static void Collect(JsonElement element, List<string> keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.StartsWith("//", StringComparison.Ordinal)) keys.Add(property.Name);
                Collect(property.Value, keys);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) Collect(item, keys);
        }
    }

    internal static IReadOnlyList<int> HashCommentLines(string text) =>
        text.Split('\n')
            .Select((line, index) => (line, index))
            .Where(pair => pair.line.TrimStart().StartsWith('#'))
            .Select(pair => pair.index + 1)
            .ToList();

    private static List<string> EngineCSharpFiles() =>
        [.. RepositoryFiles.EngineFiles("*.cs").Select(RepositoryFiles.Relative)];

    private static List<string> JsonConfigurationFiles() =>
        [
            .. RepositoryFiles.EngineFiles("*.json").Select(RepositoryFiles.Relative),
            .. RepositoryFiles.RootFiles("*schema*.json").Select(RepositoryFiles.Relative),
            .. Directory.EnumerateFiles(Path.Combine(RepositoryFiles.Root().FullName, "bench"), "*.json").Select(RepositoryFiles.Relative),
        ];

    private static List<string> HashCommentedConfigurationFiles() =>
        [
            .. RepositoryFiles.RootFiles("Dockerfile", "docker-compose*.yml").Select(RepositoryFiles.Relative),
            .. Directory.EnumerateFiles(Path.Combine(RepositoryFiles.Root().FullName, ".github", "workflows"), "*.yml").Select(RepositoryFiles.Relative),
        ];

    public static TheoryData<string> EngineCSharp() => new(EngineCSharpFiles());

    public static TheoryData<string> JsonConfiguration() => new(JsonConfigurationFiles());

    public static TheoryData<string> HashCommentedConfiguration() => new(HashCommentedConfigurationFiles());

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryFiles.Root().FullName, relativePath));

    [Fact]
    public void EveryKindOfFileIsScanned()
    {
        EngineCSharpFiles().Should().HaveCountGreaterThan(50);
        JsonConfigurationFiles().Should().Contain(
            [Path.Combine("src", "Nealytics.Engine", "appsettings.json"), "example-schema.json"]);
        HashCommentedConfigurationFiles().Should().Contain(
            ["Dockerfile", "docker-compose.yml", Path.Combine(".github", "workflows", "ci.yml")]);
    }

    [Theory]
    [MemberData(nameof(EngineCSharp))]
    public void EngineSourceHasNoComments(string relativePath)
    {
        CSharpCommentLines(Read(relativePath)).Should().BeEmpty($"{relativePath} is engine source");
    }

    [Fact]
    public void ProjectFileHasNoComments()
    {
        Read(Path.Combine("src", "Nealytics.Engine", "Nealytics.Engine.csproj"))
            .Should().NotContain("<!--");
    }

    [Fact]
    public void InitScriptHasNoComments()
    {
        SqlHasComment(Read("clickhouse-init.sql")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(JsonConfiguration))]
    public void JsonConfigurationHasNoComments(string relativePath)
    {
        JsonCommentKeys(Read(relativePath)).Should().BeEmpty($"{relativePath} is configuration");
    }

    [Theory]
    [MemberData(nameof(HashCommentedConfiguration))]
    public void YamlAndDockerConfigurationHasNoComments(string relativePath)
    {
        HashCommentLines(Read(relativePath)).Should().BeEmpty($"{relativePath} is configuration");
    }

    [Theory]
    [InlineData("int x = 1; // trailing")]
    [InlineData("/// <summary>doc</summary>\nclass C { }")]
    [InlineData("/* block */ class C { }")]
    [InlineData("class C\n{\n    // inside\n    void M() { }\n}")]
    public void TheCSharpGuardWouldActuallyFail(string source)
    {
        CSharpCommentLines(source).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("""string url = "http://localhost:4318";""")]
    [InlineData("""string sql = "SELECT 1 -- not a comment here";""")]
    [InlineData("""string verbatim = @"a // b";""")]
    [InlineData("string raw = \"\"\"\n    // inside a raw string\n    \"\"\";")]
    public void ACommentMarkerInsideAStringIsNotAComment(string source)
    {
        CSharpCommentLines(source).Should().BeEmpty();
    }

    [Theory]
    [InlineData("-- why\nSELECT 1;", true)]
    [InlineData("SELECT 1; /* why */", true)]
    [InlineData("SELECT '--' AS dashes;", false)]
    [InlineData("SELECT 'it''s -- fine';", false)]
    public void TheSqlGuardTellsCommentsFromStrings(string sql, bool expected)
    {
        SqlHasComment(sql).Should().Be(expected);
    }

    [Fact]
    public void TheJsonGuardFindsCommentKeysAtAnyDepth()
    {
        JsonCommentKeys("""{ "a": [ { "//": "why", "b": 1 } ] }""").Should().Equal("//");
    }

    [Fact]
    public void TheHashGuardFindsIndentedComments()
    {
        HashCommentLines("services:\n  api:\n    # why\n    image: x").Should().Equal(3);
    }
}
