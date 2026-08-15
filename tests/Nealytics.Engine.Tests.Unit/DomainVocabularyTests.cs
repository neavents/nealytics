using System.Text.RegularExpressions;
using FluentAssertions;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// The engine must not know any one product's words.
///
/// This was a manual grep in the plan that introduced declared dimensions, and a check nobody runs
/// is a check that has already stopped working. The failure it guards is not hypothetical: three
/// columns of one customer's vocabulary — a menu, a section, a table — were compiled into the
/// engine, so anyone cloning it for a storefront or a social app inherited <c>menu_id</c>.
///
/// The rule is about identifiers, not prose. A doc comment may use a name as an example, because
/// explaining the mechanism needs one; what it may not do is put that name in a column, a property
/// or a string the engine emits.
/// </summary>
public class DomainVocabularyTests
{
    /// <summary>
    /// Words that belong to a product rather than to analytics. Deliberately several verticals, so
    /// the test says something about the property rather than about this estate.
    /// </summary>
    private static readonly string[] Vertical =
    [
        "menu", "restaurant", "venue", "dish", "cuisine", "diner", "waiter", "allergen",
        "product", "sku", "basket", "checkout", "storefront",
        // "post" is deliberately absent: MapPost and the HTTP verb make it a word this cannot
        // distinguish, and a guard with a known false positive gets suppressed rather than fixed.
        "tweet", "follower", "playlist", "podcast",
        "patient", "invoice", "flight", "booking",
    ];

    private static readonly HashSet<string> VerticalSet =
        new(Vertical, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits an identifier into its words, so a match is a whole word rather than a substring.
    ///
    /// Without this, <c>SetPreflightMaxAge</c> trips on "flight" and <c>Description</c> would trip
    /// on anything ending in "script". A guard that cries wolf is turned off within a week.
    /// </summary>
    private static IEnumerable<string> Segments(string identifier) =>
        Regex.Split(identifier, @"_|(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")
            .Where(part => part.Length > 0);

    private static readonly Regex CommentOrString = new(
        @"//.*?$|/\*.*?\*/|""(?:[^""\\]|\\.)*""",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    private static IEnumerable<string> SourceFiles()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Nealytics.Engine");
            if (Directory.Exists(candidate))
            {
                return Directory.EnumerateFiles(candidate, "*.cs", SearchOption.AllDirectories)
                    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                   && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/Nealytics.Engine not found walking up from the test output.");
    }

    [Fact]
    public void NoProductVocabularyAppearsInEngineCode()
    {
        List<string> violations = [];

        foreach (string path in SourceFiles())
        {
            // Comments and string literals are stripped first. A doc comment saying "a deployment
            // declares menu_id" is the mechanism being explained, not the engine knowing it — and
            // the plan explicitly permits a name as example config in a comment.
            string code = CommentOrString.Replace(File.ReadAllText(path), " ");

            foreach (Match identifier in Regex.Matches(code, @"\b[A-Za-z_][A-Za-z0-9_]*\b"))
            {
                foreach (string segment in Segments(identifier.Value))
                {
                    if (!VerticalSet.Contains(segment)) continue;

                    // 'table' is the one word that is also ordinary database English — DataTable,
                    // TableName, ToTable. Excluded outright rather than pattern-matched: a rule
                    // with exceptions is a rule nobody can predict.
                    if (string.Equals(segment, "table", StringComparison.OrdinalIgnoreCase)) continue;

                    violations.Add($"{Path.GetFileName(path)}: {identifier.Value}");
                }
            }
        }

        violations.Should().BeEmpty(
            "the engine holds dimensions it was told about and never knows their names at compile "
            + "time; a product word in an identifier means a clone inherits someone else's schema");
    }

    [Theory]
    [InlineData("SetPreflightMaxAge")]
    [InlineData("Description")]
    [InlineData("DataTable")]
    [InlineData("TableName")]
    public void OrdinaryEnglishIsNotAViolation(string identifier)
    {
        Segments(identifier)
            .Where(segment => VerticalSet.Contains(segment))
            .Where(segment => !string.Equals(segment, "table", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("a guard that cries wolf is turned off within a week");
    }

    [Fact]
    public void TheGuardWouldActuallyFail()
    {
        // A guard nobody has seen fail is a guard nobody has written.
        Segments("MenuId").Should().Contain("Menu");
        Segments("menu_id").Should().Contain("menu");
        VerticalSet.Contains("Menu").Should().BeTrue();
    }

    [Fact]
    public void ACommentMayUseANameAsAnExample()
    {
        const string permitted = "// A deployment declares menu_id here; the engine never sees it.";

        CommentOrString.Replace(permitted, " ").Trim().Should().BeEmpty(
            "explaining the mechanism needs an example, and the plan permits one in a comment");
    }
}
