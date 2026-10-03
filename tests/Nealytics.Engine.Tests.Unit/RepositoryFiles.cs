namespace Nealytics.Engine.Tests.Unit;

internal static class RepositoryFiles
{
    public static DirectoryInfo Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nealytics.slnx"))) return directory;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Nealytics.slnx not found walking up from the test output.");
    }

    public static string EngineDirectory() => Path.Combine(Root().FullName, "src", "Nealytics.Engine");

    public static IEnumerable<string> EngineFiles(string pattern = "*") =>
        Directory.EnumerateFiles(EngineDirectory(), pattern, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

    public static IEnumerable<string> RootFiles(params string[] patterns) =>
        patterns.SelectMany(pattern => Directory.EnumerateFiles(Root().FullName, pattern, SearchOption.TopDirectoryOnly));

    public static string Relative(string path) => Path.GetRelativePath(Root().FullName, path);

    private static bool IsBuildOutput(string path)
    {
        char separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}obj{separator}") || path.Contains($"{separator}bin{separator}");
    }
}
