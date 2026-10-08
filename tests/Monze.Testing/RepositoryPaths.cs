namespace Monze.Testing;

/// <summary>Locates the Monze repository from a test output directory.</summary>
public static class RepositoryPaths
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The directory that contains Monze.slnx.</summary>
    public static string Root => RootPath.Value;

    /// <summary>The build configuration of the running test assembly (bin/&lt;configuration&gt;/&lt;tfm&gt;).</summary>
    public static string Configuration
        => new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent?.Name
           ?? throw new InvalidOperationException("Test output directory has no configuration segment.");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Monze.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Monze.slnx was not found above the test output directory.");
    }
}
