#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length != 1 || args[0] is not ("major" or "minor" or "patch"))
{
    PrintUsage();
    return 1;
}

var bumpKind = args[0];
var repoRoot = GetRepoRoot();
var current = ParseLatestTag(GetLatestTag(repoRoot));
var next = Bump(current, bumpKind);
var currentText = current.ToString();
var nextText = next.ToString();

Console.WriteLine($"Latest tag: v{currentText}");
Console.WriteLine($"Next version: {nextText} ({bumpKind})");
Console.WriteLine();

var changedFiles = new List<string>();
foreach (var path in EnumerateTargetFiles(repoRoot))
{
    var original = File.ReadAllText(path);
    if (!original.Contains(currentText, StringComparison.Ordinal))
        continue;

    var updated = original.Replace(currentText, nextText, StringComparison.Ordinal);
    if (updated == original)
        continue;

    File.WriteAllText(path, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    changedFiles.Add(path);
    Console.WriteLine(Path.GetRelativePath(repoRoot, path));
}

Console.WriteLine();
Console.WriteLine(changedFiles.Count == 0
    ? "No files updated."
    : $"{changedFiles.Count} file(s) updated.");

return 0;

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: dotnet ./sandbox/scripts/bump_version.cs <major|minor|patch>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Reads the latest vX.Y.Z git tag, bumps the version, and replaces it in");
    Console.Error.WriteLine("  .props, .md, .sh, and .yml files (excluding references/ and *.rb).");
}

static string GetRepoRoot()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new InvalidOperationException("Repository root not found (.git missing). Run from the seiton repo.");
}

static string GetLatestTag(string repoRoot)
{
    var output = RunGit(repoRoot, "tag", "--sort=-v:refname");
    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (IsSemverTag(line, out _))
            return line;
    }

    throw new InvalidOperationException("No semver git tag found (expected vX.Y.Z).");
}

static Version ParseLatestTag(string tag)
{
    if (!IsSemverTag(tag, out var version))
        throw new InvalidOperationException($"Failed to parse tag: {tag}");

    return version;
}

static bool IsSemverTag(string tag, out Version version)
{
    var match = Regex.Match(tag, @"^v(?<ver>\d+\.\d+\.\d+)$");
    if (match.Success && Version.TryParse(match.Groups["ver"].Value, out version!))
        return true;

    version = default!;
    return false;
}

static Version Bump(Version current, string kind) => kind switch
{
    "major" => new Version(current.Major + 1, 0, 0),
    "minor" => new Version(current.Major, current.Minor + 1, 0),
    "patch" => new Version(current.Major, current.Minor, current.Build + 1),
    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
};

static IEnumerable<string> EnumerateTargetFiles(string repoRoot)
{
    var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".props", ".md", ".sh", ".yml",
    };

    foreach (var path in Directory.EnumerateFiles(repoRoot, "*.*", SearchOption.AllDirectories))
    {
        if (!extensions.Contains(Path.GetExtension(path)))
            continue;

        if (ShouldSkip(path, repoRoot))
            continue;

        yield return path;
    }
}

static bool ShouldSkip(string path, string repoRoot)
{
    var relative = Path.GetRelativePath(repoRoot, path);
    foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    {
        if (segment is ".git" or "bin" or "obj" or "node_modules")
            return true;

        if (string.Equals(segment, "references", StringComparison.OrdinalIgnoreCase))
            return true;
    }

    return string.Equals(Path.GetExtension(path), ".rb", StringComparison.OrdinalIgnoreCase);
}

static string RunGit(string repoRoot, params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        },
    };

    foreach (var argument in arguments)
        process.StartInfo.ArgumentList.Add(argument);

    process.Start();
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();

    if (process.ExitCode != 0)
        throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr.Trim()}");

    return stdout;
}
