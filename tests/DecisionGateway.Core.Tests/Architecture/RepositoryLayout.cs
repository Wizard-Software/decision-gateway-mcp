using System.Xml;
using System.Xml.Linq;

namespace DecisionGateway.Core.Tests.Architecture;

/// <summary>
/// Locates the repository root and the files architecture tests inspect. The root is found only by walking up
/// from the test output directory; there is deliberately no environment override.
/// </summary>
internal static class RepositoryLayout
{
    private static readonly Lazy<string> RootValue = new(() => FindRoot());

    private static readonly string[] SkippedDirectoryNames = ["bin", "obj", "node_modules"];

    private static readonly string[] ProjectFileExtensions = [".csproj", ".fsproj", ".vbproj", ".proj"];

    public static string Root => RootValue.Value;

    public static string CoreProject => Path.Combine(Root, "src", "DecisionGateway.Core", "DecisionGateway.Core.csproj");

    public static string ProvidersProject => Path.Combine(Root, "src", "DecisionGateway.Providers", "DecisionGateway.Providers.csproj");

    public static string HostProject => Path.Combine(Root, "src", "DecisionGateway.Host", "DecisionGateway.Host.csproj");

    public static IReadOnlyList<string> ProductionProjects => [CoreProject, ProvidersProject, HostProject];

    public static IReadOnlyList<string> AotProjects => [CoreProject, ProvidersProject];

    public static string DirectoryBuildProps => Path.Combine(Root, "Directory.Build.props");

    public static string DirectoryPackagesProps => Path.Combine(Root, "Directory.Packages.props");

    /// <summary>Path of the restore output for the given project file.</summary>
    public static string AssetsFile(string projectPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing {path}: run restore first.", path);
        }

        return path;
    }

    /// <summary>
    /// Root-relative paths ('/' separated, sorted ordinally) of every MSBuild props/targets file
    /// (Directory.* files and any *.props / *.targets) in the repository.
    /// </summary>
    public static IReadOnlyList<string> MsBuildFiles()
    {
        var found = new List<string>();
        Walk(Root, file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(file);
            var isDirectoryFile =
                name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Directory.Solution.props", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Directory.Solution.targets", StringComparison.OrdinalIgnoreCase);
            if (isDirectoryFile
                || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(Relative(file));
            }
        });
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Root-relative paths of every project file found anywhere under the src directory.</summary>
    public static IReadOnlyList<string> ProjectFilesUnderSrc()
    {
        var found = new List<string>();
        var src = Path.Combine(Root, "src");
        if (Directory.Exists(src))
        {
            Walk(src, file =>
            {
                if (ProjectFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(Relative(file));
                }
            });
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Full paths of every *.cs file under the project directory, sorted ordinally.</summary>
    public static IReadOnlyList<string> SourceFiles(string projectPath)
    {
        var found = new List<string>();
        Walk(Path.GetDirectoryName(projectPath)!, file =>
        {
            if (Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(file);
            }
        });
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    public static string Relative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    public static string OutputAssembly(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing build output {path}.", path);
        }

        return path;
    }

    /// <summary>Loads an XML file with DTD processing prohibited and no external resolver.</summary>
    public static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(path, settings);
        return XDocument.Load(reader);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DecisionGateway.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("DecisionGateway.slnx not found above " + AppContext.BaseDirectory);
    }

    // Prunes at directory level: bin, obj, node_modules and every dot-directory are never entered.
    private static void Walk(string directory, Action<string> onFile)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            onFile(file);
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            Walk(child, onFile);
        }
    }
}
