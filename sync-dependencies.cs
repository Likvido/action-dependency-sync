#!/usr/bin/dotnet run

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// Parse command-line arguments
var arguments = new Arguments(Environment.GetCommandLineArgs().Skip(1).ToArray());

if (arguments.Help)
{
    PrintHelp();
    return 0;
}

var repoRoot = arguments.RepoRoot ?? Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
repoRoot = Path.GetFullPath(repoRoot);

Console.WriteLine($"Repository Root: {repoRoot}");

// Step 1: Find all solutions in the repository
var solutions = FindSolutions(repoRoot);
if (solutions.Count == 0)
{
    Console.WriteLine("::error::No .sln or .slnx files found in repository");
    return 1;
}

Console.WriteLine($"\nFound {solutions.Count} solution(s):");
foreach (var sln in solutions)
{
    Console.WriteLine($"  - {GetRelativePath(repoRoot, sln)}");
}

// Step 2: Build complete project graph from all solutions
Console.WriteLine("\nBuilding project dependency graph...");
var graphBuilder = new RepositoryGraphBuilder(repoRoot, solutions);
var graphResult = graphBuilder.Build();

if (!graphResult.Success)
{
    Console.WriteLine($"::error::{graphResult.ErrorMessage}");
    return 1;
}

Console.WriteLine($"Found {graphResult.AllProjects.Count} projects in repository");

// Step 3: Find all deployable projects (projects with Dockerfiles)
var testProjects = graphResult.AllProjects.Where(IsTestProject).ToHashSet(StringComparer.OrdinalIgnoreCase);
var deployableProjects = FindDeployableProjects(graphResult.AllProjects.Where(p => !testProjects.Contains(p)).ToList(), repoRoot);
Console.WriteLine($"Found {deployableProjects.Count} deployable project(s) with Dockerfiles");

if (deployableProjects.Count == 0)
{
    Console.WriteLine("::warning::No projects with Dockerfiles found. Nothing to update.");
    return 0;
}

// Step 4: Determine which projects were modified
var modifiedProjects = DetermineModifiedProjects(arguments.ModifiedFiles, repoRoot, graphResult.AllProjects);

if (modifiedProjects.Count == 0)
{
    Console.WriteLine("\nNo modified project files detected. Checking all deployable projects...");
    // If no specific modifications provided, update all deployable projects
    modifiedProjects = graphResult.AllProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
}
else
{
    Console.WriteLine($"\nModified projects ({modifiedProjects.Count}):");
    foreach (var proj in modifiedProjects)
    {
        Console.WriteLine($"  - {GetRelativePath(repoRoot, proj)}");
    }
}

// Step 5: Find all deployable projects affected by the modifications
var affectedDeployables = FindAffectedDeployables(deployableProjects, modifiedProjects, graphResult.DependencyGraph);

Console.WriteLine($"\nAffected deployable projects ({affectedDeployables.Count}):");
foreach (var proj in affectedDeployables)
{
    Console.WriteLine($"  - {GetRelativePath(repoRoot, proj)}");
}

if (affectedDeployables.Count == 0)
{
    Console.WriteLine("\n::notice::No deployable projects affected by the changes.");
    SetGitHubOutputs(0, 0, 0);
    return 0;
}

// Path filter entries this action generates for any workflow: the directories of every project except the
// test side of the solution, whose entries are written by hand. Together with the deployable's own props files
// and stale entries (see IsGeneratedPathEntry), anything else in a paths list is left alone.
var testSide = FindTestSide(graphResult.AllProjects, testProjects, deployableProjects, graphResult.DependencyGraph, repoRoot);
var generatedEntries = graphResult.AllProjects
    .Where(p => !testSide.Contains(p))
    .Select(p => GetRelativePath(repoRoot, Path.GetDirectoryName(p)!).Replace("\\", "/") + "/**")
    .ToHashSet(StringComparer.Ordinal);

var deployableDockerfiles = deployableProjects
    .ToDictionary(p => p, p => FindDockerfile(Path.GetDirectoryName(p)!, repoRoot)!, StringComparer.OrdinalIgnoreCase);
var workflowDockerfiles = FindWorkflowDockerfiles(repoRoot);
var workflowOwners = AssignWorkflowOwners(workflowDockerfiles, deployableDockerfiles, repoRoot);
foreach (var (workflow, dockerfile) in workflowDockerfiles)
{
    if (!deployableDockerfiles.ContainsValue(dockerfile) &&
        Regex.IsMatch(File.ReadAllText(dockerfile), @"\bdotnet\s+(restore|build|publish)\b", RegexOptions.IgnoreCase))
    {
        Console.WriteLine($"::warning::{GetRelativePath(repoRoot, workflow)} builds {GetRelativePath(repoRoot, dockerfile)}, but no project it publishes could be identified");
    }
}

// Step 6: Update Dockerfiles and workflows for affected projects
var dockerfilesUpdated = 0;
var workflowsUpdated = 0;
var totalDependencies = 0;

foreach (var deployableProject in affectedDeployables)
{
    Console.WriteLine($"\n{"=",-60}");
    Console.WriteLine($"Processing: {Path.GetFileNameWithoutExtension(deployableProject)}");
    Console.WriteLine($"{"=",-60}");

    // Get all transitive dependencies for this project
    var dependencies = GetTransitiveDependencies(deployableProject, graphResult.DependencyGraph);
    totalDependencies += dependencies.Count;

    Console.WriteLine($"Dependencies ({dependencies.Count}):");
    foreach (var dep in dependencies.OrderBy(d => d))
    {
        Console.WriteLine($"  - {GetRelativePath(repoRoot, dep)}");
    }

    // Find Directory.Build.props and Directory.Packages.props
    var projectDir = Path.GetDirectoryName(deployableProject)!;
    var directoryBuildProps = FindFileInHierarchy(projectDir, "Directory.Build.props", repoRoot);
    var directoryPackagesProps = FindFileInHierarchy(projectDir, "Directory.Packages.props", repoRoot);

    if (directoryBuildProps != null)
    {
        Console.WriteLine($"Found Directory.Build.props: {GetRelativePath(repoRoot, directoryBuildProps)}");
    }
    if (directoryPackagesProps != null)
    {
        Console.WriteLine($"Found Directory.Packages.props: {GetRelativePath(repoRoot, directoryPackagesProps)}");
    }

    var dockerfilePath = FindDockerfile(projectDir, repoRoot);

    // Find workflow files first (needed for docker context detection)
    var workflowPaths = FindWorkflowFiles(deployableProject, workflowOwners, repoRoot);
    if (workflowPaths.Count == 0)
    {
        Console.WriteLine($"  ::warning::No workflow found for {Path.GetFileNameWithoutExtension(deployableProject)}; its path filters are not updated");
    }
    var workflowPath = workflowPaths.FirstOrDefault();

    // Update Dockerfile (pass workflow path for docker context extraction)
    if (dockerfilePath != null && RestoresWholeSolution(dockerfilePath))
    {
        // A solution-wide restore needs every project the solution names, not just this project's dependencies.
        Console.WriteLine($"\nSkipping Dockerfile: {GetRelativePath(repoRoot, dockerfilePath)} restores a whole solution, so its COPY block is not generated");
    }
    else if (dockerfilePath != null)
    {
        Console.WriteLine($"\nUpdating Dockerfile: {GetRelativePath(repoRoot, dockerfilePath)}");
        var result = UpdateDockerfile(dockerfilePath, deployableProject, dependencies, directoryBuildProps, directoryPackagesProps, repoRoot, workflowPath);
        if (result.Success)
        {
            Console.WriteLine("  ✓ Dockerfile updated successfully");
            dockerfilesUpdated++;
        }
        else
        {
            Console.WriteLine($"  ::warning::Dockerfile update failed: {result.Message}");
        }
    }

    // Update workflow files
    foreach (var workflowFile in workflowPaths)
    {
        Console.WriteLine($"Updating workflow: {GetRelativePath(repoRoot, workflowFile)}");
        var result = UpdateWorkflow(workflowFile, deployableProject, dependencies, directoryBuildProps, directoryPackagesProps, repoRoot, generatedEntries);
        if (result.Success)
        {
            Console.WriteLine("  ✓ Workflow updated successfully");
            workflowsUpdated++;
        }
        else
        {
            Console.WriteLine($"  ::warning::Workflow update failed: {result.Message}");
        }
    }
}

// Set GitHub Actions outputs
SetGitHubOutputs(dockerfilesUpdated, workflowsUpdated, totalDependencies);

Console.WriteLine($"\n{"=",-60}");
Console.WriteLine($"✓ Sync completed successfully");
Console.WriteLine($"  Dockerfiles updated: {dockerfilesUpdated}");
Console.WriteLine($"  Workflows updated: {workflowsUpdated}");
Console.WriteLine($"  Total dependencies processed: {totalDependencies}");
return 0;

// ============================================================================
// Helper Functions
// ============================================================================

static List<string> FindSolutions(string repoRoot)
{
    var slnFiles = Directory.GetFiles(repoRoot, "*.sln", SearchOption.AllDirectories);
    var slnxFiles = Directory.GetFiles(repoRoot, "*.slnx", SearchOption.AllDirectories);

    return slnFiles.Concat(slnxFiles)
        .Where(s => !s.Contains("/bin/") && !s.Contains("/obj/") && !s.Contains("\\bin\\") && !s.Contains("\\obj\\"))
        .ToList();
}

static List<string> FindDeployableProjects(List<string> allProjects, string repoRoot)
{
    var deployable = new List<string>();
    foreach (var project in allProjects)
    {
        var projectDir = Path.GetDirectoryName(project)!;
        var dockerfile = FindDockerfile(projectDir, repoRoot);
        if (dockerfile == null)
        {
            continue;
        }

        // A Dockerfile in a parent directory belongs to every project below it only by accident of layout.
        // It makes this project deployable only when it publishes this project.
        if (Path.GetDirectoryName(dockerfile)!.Equals(projectDir, StringComparison.OrdinalIgnoreCase) ||
            DockerfilePublishesProject(dockerfile, project))
        {
            deployable.Add(project);
        }
    }
    return deployable;
}

static bool RestoresWholeSolution(string dockerfilePath)
{
    if (File.ReadAllText(dockerfilePath).Contains("# BEGIN AUTO-GENERATED PROJECT REFERENCES"))
    {
        return false;
    }
    // The solution has to be an argument of the restore command itself, not of a later chained one.
    return DockerfileInstructions(dockerfilePath)
        .Where(instruction => Regex.IsMatch(instruction, @"^RUN\b", RegexOptions.IgnoreCase))
        .Select(ShellCommandLine)
        .Any(command => Regex.IsMatch(command, @"\bdotnet\s+restore\b[^;&|\n]*\.slnx?\b", RegexOptions.IgnoreCase));
}

// Whether the image the Dockerfile builds publishes the project: a "dotnet publish" naming the .csproj in the
// final stage or a stage it is built FROM or copies --from. The deployment pipeline builds the final stage, so a
// publish in any other stage (a test or tooling stage) does not end up in the deployed image.
static bool DockerfilePublishesProject(string dockerfilePath, string projectPath)
{
    var projectFileName = Path.GetFileName(projectPath);
    var publishProjectPattern = new Regex(
        $@"\bdotnet\s+publish\b[^;&|\n]*?(?<![A-Za-z0-9_.-]){Regex.Escape(projectFileName)}(?![A-Za-z0-9_.-])",
        RegexOptions.IgnoreCase);

    return FinalImageInstructions(dockerfilePath)
        .Select(ShellCommandLine)
        .Any(instruction => publishProjectPattern.IsMatch(instruction));
}

// The command line an instruction runs, ready to be split at shell separators. An exec-form RUN ["dotnet",
// "publish", ...] is read as the command line it runs. A ; & or | inside quotes, as in -p:DefineConstants="A;B",
// does not end the shell command.
static string ShellCommandLine(string instruction)
{
    var commandLine = ExecFormArguments(instruction) is { } arguments ? ExecFormCommandLine(arguments) : instruction;
    return Regex.Replace(commandLine, @"""[^""]*""|'[^']*'", quoted => Regex.Replace(quoted.Value, "[;&|]", " "));
}

// An exec-form RUN has no shell, so ; & | inside an argument are literal. A shell started in exec form
// (["sh", "-c", "a && b"], or a flag cluster such as "-ec") runs its script, which is read as a shell command
// line, one line per newline in it.
static string ExecFormCommandLine(List<string> arguments)
{
    var isShell = arguments.Count > 1 && Path.GetFileName(arguments[0]) is "sh" or "bash" or "ash" or "dash" or "zsh";
    var scriptIndex = isShell ? arguments.FindIndex(1, argument => Regex.IsMatch(argument, "^-[A-Za-z]*c[A-Za-z]*$")) + 1 : 0;
    if (isShell && scriptIndex > 0 && scriptIndex < arguments.Count)
    {
        return arguments[scriptIndex];
    }
    return string.Join(" ", arguments.Select(argument => Regex.Replace(argument, "[;&|\r\n]", " ")));
}

// The arguments of an exec-form RUN, or null for shell form. As in Docker, a RUN is exec form only when its
// command is a JSON array of strings; anything else, such as RUN [ -f x ], is shell form.
static List<string>? ExecFormArguments(string instruction)
{
    var exec = Regex.Match(instruction, @"^RUN\s+(?:--\S+\s+)*(\[.*\])\s*$", RegexOptions.IgnoreCase);
    if (!exec.Success)
    {
        return null;
    }
    try
    {
        using var document = JsonDocument.Parse(exec.Groups[1].Value);
        var elements = document.RootElement.EnumerateArray().ToList();
        return elements.Count > 0 && elements.All(e => e.ValueKind == JsonValueKind.String)
            ? elements.Select(e => e.GetString()!).ToList()
            : null;
    }
    catch (Exception e) when (e is JsonException or InvalidOperationException)
    {
        // Not valid JSON, or a string .NET cannot decode (such as a lone surrogate): read it as shell form.
        return null;
    }
}

// The Dockerfile's instructions, one per line. Comment lines are dropped first, as Docker does, then
// continuation lines are joined so an instruction split over several lines is one line. The continuation
// character is "\", or the one a "# escape=" parser directive at the top of the file sets (often "`" on
// Windows). Heredoc bodies stay as separate lines.
static List<string> DockerfileInstructions(string dockerfilePath)
{
    var lines = File.ReadAllText(dockerfilePath).Split('\n');
    var escape = '\\';
    foreach (var line in lines)
    {
        // Parser directives are "# key=value" comments before the first instruction or other comment. As in
        // Docker, an unknown key or an empty value is a plain comment and ends the directives, and only ASCII
        // whitespace may separate the parts.
        var directive = Regex.Match(line, @"^\s*#[ \t\f\r]*([A-Za-z][A-Za-z0-9]*)[ \t\f\r]*=[ \t\f\r]*(.+?)[ \t\f\r]*$");
        if (!directive.Success || directive.Groups[1].Value.ToLowerInvariant() is not ("syntax" or "escape" or "check"))
        {
            break;
        }
        if (directive.Groups[1].Value.Equals("escape", StringComparison.OrdinalIgnoreCase) &&
            directive.Groups[2].Value is "`" or "\\")
        {
            escape = directive.Groups[2].Value[0];
        }
    }

    var withoutComments = string.Join("\n", lines.Where(line => !line.TrimStart().StartsWith("#")));
    return Regex.Replace(withoutComments, Regex.Escape(escape.ToString()) + @"[ \t]*\r?\n", " ")
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();
}

static List<string> FinalImageInstructions(string dockerfilePath)
{
    var instructions = DockerfileInstructions(dockerfilePath);

    var stages = new List<(string? Name, string Base, List<string> Instructions)>();
    foreach (var instruction in instructions)
    {
        var from = Regex.Match(instruction, @"^FROM\s+(?:--\S+\s+)*(\S+)(?:\s+AS\s+(\S+))?", RegexOptions.IgnoreCase);
        if (from.Success)
        {
            stages.Add((from.Groups[2].Success ? from.Groups[2].Value : null, from.Groups[1].Value, new List<string>()));
        }
        else if (stages.Count > 0)
        {
            stages[^1].Instructions.Add(instruction);
        }
    }
    if (stages.Count == 0)
    {
        return instructions;
    }

    int? FindStage(string reference) =>
        int.TryParse(reference, out var index) && index >= 0 && index < stages.Count ? index
        : stages.FindIndex(st => st.Name != null && st.Name.Equals(reference, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i
        : null;

    var included = new HashSet<int>();
    var queue = new Queue<int>();
    queue.Enqueue(stages.Count - 1);
    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        if (!included.Add(current))
        {
            continue;
        }
        var references = stages[current].Instructions
            .Select(i => Regex.Match(i, @"^(?:COPY\b.*?--from=|RUN\b.*?--mount=\S*?\bfrom=)([^\s,]+)", RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Prepend(stages[current].Base);
        foreach (var reference in references)
        {
            if (FindStage(reference) is int stage)
            {
                queue.Enqueue(stage);
            }
        }
    }

    return included.OrderBy(i => i).SelectMany(i => stages[i].Instructions).ToList();
}

static bool ReferencesTestPackage(XDocument doc) => doc.Descendants()
    .Where(e => e.Name.LocalName == "PackageReference")
    .Any(e => new[] { "Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest", "MSTest.TestFramework" }
        .Contains((string?)e.Attribute("Include") ?? "", StringComparer.OrdinalIgnoreCase));

static bool IsTestProject(string projectPath)
{
    try
    {
        var doc = XDocument.Load(projectPath);
        var sdk = (string?)doc.Root?.Attribute("Sdk") ?? "";
        var isTestProject = doc.Descendants()
            .Where(e => e.Name.LocalName == "IsTestProject" &&
                        e.AncestorsAndSelf().All(a => a.Attribute("Condition") == null && a.Name.LocalName is not ("When" or "Otherwise")))
            .Any(e => e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
        return isTestProject || ReferencesTestPackage(doc) || sdk.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase);
    }
    catch
    {
        return false;
    }
}

// The projects whose directories are not generated: test projects; projects nothing references that sit under
// a Directory.Build.props configuring tests (which may make them test projects in a way this action cannot
// evaluate); and projects that only those reference, such as shared test helpers. Deployables never are.
// A library that only its tests still reference looks the same as a test helper, so an entry left behind for it
// is kept on purpose: an extra trigger costs a build, while dropping a helper's entry would skip a test run.
static HashSet<string> FindTestSide(List<string> allProjects, HashSet<string> testProjects, List<string> deployableProjects,
    Dictionary<string, HashSet<string>> dependencyGraph, string repoRoot)
{
    var referencedBy = allProjects.ToDictionary(p => p, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
    foreach (var (project, references) in dependencyGraph)
    {
        foreach (var reference in references)
        {
            if (referencedBy.TryGetValue(reference, out var list))
            {
                list.Add(project);
            }
        }
    }

    bool PropsConfigureTests(string project)
    {
        try
        {
            var props = FindFileInHierarchy(Path.GetDirectoryName(project)!, "Directory.Build.props", repoRoot);
            if (props == null)
            {
                return false;
            }
            var doc = XDocument.Load(props);
            return ReferencesTestPackage(doc) || doc.Descendants().Any(e => e.Name.LocalName == "IsTestProject");
        }
        catch
        {
            return false;
        }
    }

    var deployables = deployableProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var testSide = allProjects
        .Where(p => !deployables.Contains(p) && (testProjects.Contains(p) || (referencedBy[p].Count == 0 && PropsConfigureTests(p))))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var changed = true;
    while (changed)
    {
        changed = false;
        foreach (var project in allProjects)
        {
            if (!deployables.Contains(project) && !testSide.Contains(project) &&
                referencedBy[project].Count > 0 && referencedBy[project].All(testSide.Contains))
            {
                testSide.Add(project);
                changed = true;
            }
        }
    }
    return testSide;
}

static HashSet<string> DetermineModifiedProjects(List<string> modifiedFiles, string repoRoot, List<string> allProjects)
{
    var modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (modifiedFiles.Count == 0)
    {
        return modified;
    }

    // Normalize all project paths for comparison
    var projectLookup = allProjects.ToDictionary(
        p => Path.GetFullPath(p).ToLowerInvariant(),
        p => p,
        StringComparer.OrdinalIgnoreCase);

    foreach (var file in modifiedFiles)
    {
        var fullPath = Path.IsPathRooted(file) ? file : Path.Combine(repoRoot, file);
        fullPath = Path.GetFullPath(fullPath);

        // Check if this is a .csproj file
        if (fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            if (projectLookup.TryGetValue(fullPath.ToLowerInvariant(), out var project))
            {
                modified.Add(project);
            }
            else if (File.Exists(fullPath))
            {
                modified.Add(fullPath);
            }
        }
        // Check if this is Directory.Build.props or Directory.Packages.props
        else if (Path.GetFileName(fullPath).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(fullPath).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
        {
            // These affect all projects in the directory tree below them
            var propsDir = Path.GetDirectoryName(fullPath)!;
            foreach (var project in allProjects)
            {
                if (project.StartsWith(propsDir, StringComparison.OrdinalIgnoreCase))
                {
                    modified.Add(project);
                }
            }
        }
    }

    return modified;
}

static List<string> FindAffectedDeployables(List<string> deployableProjects, HashSet<string> modifiedProjects, Dictionary<string, HashSet<string>> dependencyGraph)
{
    var affected = new List<string>();

    foreach (var deployable in deployableProjects)
    {
        // Check if the deployable itself was modified
        if (modifiedProjects.Contains(deployable))
        {
            affected.Add(deployable);
            continue;
        }

        // Check if any of its transitive dependencies were modified
        var dependencies = GetTransitiveDependencies(deployable, dependencyGraph);
        if (dependencies.Any(dep => modifiedProjects.Contains(dep)))
        {
            affected.Add(deployable);
        }
    }

    return affected;
}

static HashSet<string> GetTransitiveDependencies(string project, Dictionary<string, HashSet<string>> dependencyGraph)
{
    var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var queue = new Queue<string>();

    if (dependencyGraph.TryGetValue(project, out var directDeps))
    {
        foreach (var dep in directDeps)
        {
            queue.Enqueue(dep);
        }
    }

    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        if (dependencies.Add(current))
        {
            if (dependencyGraph.TryGetValue(current, out var transitiveDeps))
            {
                foreach (var dep in transitiveDeps)
                {
                    if (!dependencies.Contains(dep))
                    {
                        queue.Enqueue(dep);
                    }
                }
            }
        }
    }

    return dependencies;
}

static string? FindDockerfile(string startDir, string repoRoot)
{
    var current = startDir;
    while (current != null && (current.StartsWith(repoRoot) || current.Equals(repoRoot, StringComparison.OrdinalIgnoreCase)))
    {
        var dockerfile = Path.Combine(current, "Dockerfile");
        if (File.Exists(dockerfile))
        {
            return dockerfile;
        }
        var parent = Path.GetDirectoryName(current);
        if (parent == current || parent == null) break;
        current = parent;
    }
    return null;
}

// Maps each workflow to the Dockerfile it builds, from its DOCKER_WORKING_DIRECTORY and
// DOCKERFILE_RELATIVE_PATH (or the equivalent docker-working-directory/dockerfile-relative-path inputs).
// A workflow whose values cannot be read, or that names a Dockerfile that does not exist, is left out.
static Dictionary<string, string> FindWorkflowDockerfiles(string repoRoot)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var workflow in GetWorkflowFiles(repoRoot))
    {
        var content = StripYamlComments(File.ReadAllText(workflow));
        var (hasWorkingDirectory, workingDirectory) = ReadWorkflowValue(content, "DOCKER_WORKING_DIRECTORY", "docker-working-directory");
        var (_, dockerfileRelativePath) = ReadWorkflowValue(content, "DOCKERFILE_RELATIVE_PATH", "dockerfile-relative-path");
        if (dockerfileRelativePath == null || (hasWorkingDirectory && workingDirectory == null))
        {
            continue;
        }

        var dockerfile = Path.GetFullPath(Path.Combine(repoRoot, workingDirectory ?? ".", dockerfileRelativePath));
        if (File.Exists(dockerfile))
        {
            result[workflow] = dockerfile;
        }
    }
    return result;
}

// Whether the key is present, and its value when it is a plain literal (not an expression).
static (bool Present, string? Value) ReadWorkflowValue(string content, params string[] keys)
{
    foreach (var key in keys)
    {
        var match = Regex.Match(content, $@"^\s*{Regex.Escape(key)}:[ \t]*(.*)$", RegexOptions.Multiline);
        if (!match.Success)
        {
            continue;
        }

        var value = match.Groups[1].Value.Trim();
        if (value.StartsWith("\"") || value.StartsWith("'"))
        {
            var close = value.IndexOf(value[0], 1);
            value = close > 0 ? value.Substring(1, close - 1) : "";
        }
        else
        {
            var comment = Regex.Match(value, @"\s#");
            value = (comment.Success ? value.Substring(0, comment.Index) : value).Trim();
        }

        return (true, value.Length == 0 || value.Contains("${{") ? null : value);
    }
    return (false, null);
}

// Gives every workflow that builds a deployable's Dockerfile that deployable as its owner. A Dockerfile whose
// image publishes several projects leaves its workflows unowned, because which one a workflow is for cannot be
// told from the workflow.
static Dictionary<string, string?> AssignWorkflowOwners(Dictionary<string, string> workflowDockerfiles,
    Dictionary<string, string> deployableDockerfiles, string repoRoot)
{
    var owners = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var (workflow, dockerfile) in workflowDockerfiles)
    {
        var candidates = deployableDockerfiles
            .Where(d => d.Value.Equals(dockerfile, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Key)
            .ToList();
        if (candidates.Count == 1)
        {
            owners[workflow] = candidates[0];
        }
        else if (candidates.Count > 1)
        {
            owners[workflow] = null;
            Console.WriteLine($"::warning::{GetRelativePath(repoRoot, workflow)} builds a Dockerfile whose image publishes several projects; its path filters are not updated");
        }
    }
    return owners;
}

static IEnumerable<string> GetWorkflowFiles(string repoRoot)
{
    var workflowDir = Path.Combine(repoRoot, ".github", "workflows");
    if (!Directory.Exists(workflowDir))
    {
        return Array.Empty<string>();
    }
    return Directory.GetFiles(workflowDir, "*.yml").Concat(Directory.GetFiles(workflowDir, "*.yaml")).OrderBy(w => w);
}

static string StripYamlComments(string content) =>
    string.Join("\n", content.Split('\n').Where(line => !line.TrimStart().StartsWith("#")));

static List<string> FindWorkflowFiles(string projectPath, Dictionary<string, string?> workflowOwners, string repoRoot)
{
    // A workflow that builds a deployable's Dockerfile belongs to its owner alone.
    var owned = workflowOwners
        .Where(w => w.Value != null && w.Value.Equals(projectPath, StringComparison.OrdinalIgnoreCase))
        .Select(w => w.Key)
        .ToList();
    if (owned.Count > 0)
    {
        return owned;
    }

    // Otherwise fall back to the workflow that mentions the project most specifically.
    var best = GetWorkflowFiles(repoRoot)
        .Where(w => !workflowOwners.ContainsKey(w))
        .Select(w => (workflow: w, score: ScoreWorkflowMention(projectPath, StripYamlComments(File.ReadAllText(w)), repoRoot)))
        .Where(m => m.score > 0)
        .OrderByDescending(m => m.score)
        .FirstOrDefault()
        .workflow;
    return best == null ? new List<string>() : new List<string> { best };
}

static int ScoreWorkflowMention(string projectPath, string content, string repoRoot)
{
    var projectName = Path.GetFileNameWithoutExtension(projectPath);
    var projectRelativePath = GetRelativePath(repoRoot, projectPath).Replace("\\", "/");
    var projectDir = Path.GetDirectoryName(projectRelativePath)?.Replace("\\", "/") ?? "";

    // Highest priority: exact project path match
    if (content.Contains(projectRelativePath))
    {
        return 100;
    }
    // High priority: project directory path match (e.g., "src/Likvido.CampaignRunnerScheduler/")
    if (!string.IsNullOrEmpty(projectDir) && content.Contains(projectDir + "/"))
    {
        return 90;
    }
    // Medium priority: project name not followed by more name characters (so CampaignRunner does not
    // match CampaignRunnerScheduler)
    var exactNamePattern = new Regex($@"{Regex.Escape(projectName)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);
    return exactNamePattern.IsMatch(content) ? 50 : 0;
}

static string? FindFileInHierarchy(string startDir, string fileName, string repoRoot)
{
    var current = startDir;
    while (current != null && (current.StartsWith(repoRoot) || current.Equals(repoRoot, StringComparison.OrdinalIgnoreCase)))
    {
        var filePath = Path.Combine(current, fileName);
        if (File.Exists(filePath))
        {
            return filePath;
        }
        var parent = Path.GetDirectoryName(current);
        if (parent == current || parent == null) break;
        current = parent;
    }
    return null;
}

static string GetRelativePath(string fromPath, string toPath)
{
    if (string.IsNullOrEmpty(fromPath)) throw new ArgumentNullException(nameof(fromPath));
    if (string.IsNullOrEmpty(toPath)) throw new ArgumentNullException(nameof(toPath));

    var fromUri = new Uri(AppendDirectorySeparator(fromPath));
    var toUri = new Uri(toPath);

    if (fromUri.Scheme != toUri.Scheme) return toPath;

    var relativeUri = fromUri.MakeRelativeUri(toUri);
    var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

    if (toUri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
    {
        relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    return relativePath;
}

static string AppendDirectorySeparator(string path)
{
    if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()) && !path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
    {
        return path + Path.DirectorySeparatorChar;
    }
    return path;
}

static string? ExtractDockerContextFromWorkflow(string? workflowPath, string repoRoot)
{
    if (workflowPath == null || !File.Exists(workflowPath))
    {
        return null;
    }

    var content = File.ReadAllText(workflowPath);

    // Look for docker-working-directory in various formats:
    // docker-working-directory: src
    // docker-working-directory: 'src'
    // docker-working-directory: "src"
    var patterns = new[]
    {
        @"docker-working-directory:\s*['""]?([^'""#\n\r]+?)['""]?\s*(?:#|$|\n|\r)",
        @"DOCKER_WORKING_DIRECTORY:\s*['""]?([^'""#\n\r]+?)['""]?\s*(?:#|$|\n|\r)"
    };

    foreach (var pattern in patterns)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var match = regex.Match(content);
        if (match.Success)
        {
            var contextDir = match.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(contextDir))
            {
                var fullPath = Path.Combine(repoRoot, contextDir);
                if (Directory.Exists(fullPath))
                {
                    return Path.GetFullPath(fullPath);
                }
            }
        }
    }

    return null;
}

static (bool Success, string Message) UpdateDockerfile(string dockerfilePath, string projectPath, HashSet<string> dependencies,
    string? directoryBuildProps, string? directoryPackagesProps, string repoRoot, string? workflowPath = null)
{
    var content = File.ReadAllText(dockerfilePath);
    var beginMarker = "# BEGIN AUTO-GENERATED PROJECT REFERENCES";
    var endMarker = "# END AUTO-GENERATED PROJECT REFERENCES";

    var beginIndex = content.IndexOf(beginMarker);
    var endIndex = content.IndexOf(endMarker);

    // Determine docker context - the directory containing the Dockerfile
    // This is what docker build uses as the context when building
    var dockerContext = Path.GetDirectoryName(dockerfilePath)!;

    // Priority 1: Try to extract docker context from the workflow file (most reliable)
    var workflowContext = ExtractDockerContextFromWorkflow(workflowPath, repoRoot);
    if (workflowContext != null)
    {
        dockerContext = workflowContext;
    }
    else
    {
        // Priority 2: Detect from existing COPY paths in the Dockerfile
        var detectedContext = DetectDockerContext(content, dockerfilePath, repoRoot);
        if (detectedContext != null)
        {
            dockerContext = detectedContext;
        }
    }

    // Generate the new COPY statements
    var copyStatements = GenerateDockerfileCopyStatements(projectPath, dependencies, directoryBuildProps, directoryPackagesProps, repoRoot, dockerContext);

    // Strategy 1: Use markers if they exist
    if (beginIndex != -1 && endIndex != -1)
    {
        var sb = new StringBuilder();
        sb.AppendLine(beginMarker);
        sb.Append(copyStatements);
        sb.Append(endMarker);

        var newContent = content.Substring(0, beginIndex) + sb.ToString() + content.Substring(endIndex + endMarker.Length);
        File.WriteAllText(dockerfilePath, newContent);
        return (true, "Updated using markers");
    }

    // Strategy 2: Find and replace the csproj COPY block before RUN dotnet restore
    var result = UpdateDockerfileWithoutMarkers(dockerfilePath, content, copyStatements);
    if (result.Success)
    {
        return (true, "Updated using pattern detection (no markers)");
    }

    return (false, result.Message);
}

static string? DetectDockerContext(string dockerfileContent, string dockerfilePath, string repoRoot)
{
    // Look at existing COPY statements to infer the docker context
    // If we see paths like "Likvido.Accounting.Database/..." the context is the parent of those directories
    // If we see paths like "src/..." the context is the repo root

    var dockerfileDir = Path.GetDirectoryName(dockerfilePath)!;

    // Extract COPY paths from existing content
    var copyRegex = new Regex(@"COPY\s+\[?\s*[""']([^""']+\.csproj)[""']", RegexOptions.IgnoreCase);
    var matches = copyRegex.Matches(dockerfileContent);

    foreach (Match match in matches)
    {
        var copyPath = match.Groups[1].Value.Replace("\\", "/");

        // If the path doesn't contain directory separators, it's relative to dockerfile directory
        if (!copyPath.Contains("/"))
        {
            return dockerfileDir;
        }

        // Get the first directory component
        var firstDir = copyPath.Split('/')[0];

        // Check if this directory exists relative to dockerfile's parent directory
        var parentDir = Path.GetDirectoryName(dockerfileDir);
        if (parentDir != null)
        {
            var checkPath = Path.Combine(parentDir, firstDir);
            if (Directory.Exists(checkPath))
            {
                return parentDir;
            }
        }

        // Check if it's relative to repo root
        var repoPath = Path.Combine(repoRoot, firstDir);
        if (Directory.Exists(repoPath))
        {
            return repoRoot;
        }
    }

    // Default: use the directory containing the Dockerfile's parent
    // (common pattern: Dockerfile is in ProjectName/ and context is the solution directory)
    var dockerfileParent = Path.GetDirectoryName(dockerfileDir);
    if (dockerfileParent != null && Directory.Exists(dockerfileParent))
    {
        return dockerfileParent;
    }

    return null;
}

static string GenerateDockerfileCopyStatements(string projectPath, HashSet<string> dependencies,
    string? directoryBuildProps, string? directoryPackagesProps, string repoRoot, string dockerContext)
{
    var sb = new StringBuilder();

    // Add Directory.Build.props if exists (only if within docker context)
    if (directoryBuildProps != null && directoryBuildProps.StartsWith(dockerContext, StringComparison.OrdinalIgnoreCase))
    {
        var relativePath = GetRelativePath(dockerContext, directoryBuildProps).Replace("\\", "/");
        var dirName = Path.GetDirectoryName(relativePath)?.Replace("\\", "/");
        if (string.IsNullOrEmpty(dirName))
        {
            // File is at the root of docker context
            sb.AppendLine($"COPY [\"{relativePath}\", \"./\"]");
        }
        else
        {
            sb.AppendLine($"COPY [\"{relativePath}\", \"{relativePath}\"]");
        }
    }

    // Add Directory.Packages.props if exists (only if within docker context)
    if (directoryPackagesProps != null && directoryPackagesProps.StartsWith(dockerContext, StringComparison.OrdinalIgnoreCase))
    {
        var relativePath = GetRelativePath(dockerContext, directoryPackagesProps).Replace("\\", "/");
        var dirName = Path.GetDirectoryName(relativePath)?.Replace("\\", "/");
        if (string.IsNullOrEmpty(dirName))
        {
            sb.AppendLine($"COPY [\"{relativePath}\", \"./\"]");
        }
        else
        {
            sb.AppendLine($"COPY [\"{relativePath}\", \"{relativePath}\"]");
        }
    }

    // Add all dependencies (sorted for consistent output)
    var allProjects = dependencies.Append(projectPath).OrderBy(p => p).ToList();
    foreach (var dep in allProjects)
    {
        // Only include projects within the docker context
        if (!dep.StartsWith(dockerContext, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"    ::warning::Skipping dependency outside docker context: {dep}");
            continue;
        }

        var projectRelativePath = GetRelativePath(dockerContext, dep).Replace("\\", "/");
        var projectDir = Path.GetDirectoryName(projectRelativePath)!.Replace("\\", "/");
        if (string.IsNullOrEmpty(projectDir)) projectDir = ".";
        sb.AppendLine($"COPY [\"{projectRelativePath}\", \"{projectDir}/\"]");
    }

    return sb.ToString();
}

static (bool Success, string Message) UpdateDockerfileWithoutMarkers(string dockerfilePath, string content, string newCopyStatements)
{
    var lines = content.Split('\n').ToList();

    // Find the RUN dotnet restore line
    var restoreLineIndex = -1;
    for (int i = 0; i < lines.Count; i++)
    {
        var line = lines[i].Trim();
        if (line.StartsWith("RUN", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("dotnet", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("restore", StringComparison.OrdinalIgnoreCase))
        {
            restoreLineIndex = i;
            break;
        }
    }

    if (restoreLineIndex == -1)
    {
        return (false, "Could not find 'RUN dotnet restore' line in Dockerfile. Please add markers manually:\n" +
                      "  # BEGIN AUTO-GENERATED PROJECT REFERENCES\n" +
                      "  # END AUTO-GENERATED PROJECT REFERENCES");
    }

    // Look backwards from restore line to find consecutive COPY lines for .csproj files
    var copyBlockEnd = restoreLineIndex - 1;

    // Skip empty lines and comments between COPY block and RUN restore
    while (copyBlockEnd >= 0)
    {
        var line = lines[copyBlockEnd].Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
        {
            copyBlockEnd--;
        }
        else
        {
            break;
        }
    }

    if (copyBlockEnd < 0)
    {
        return (false, "Could not find COPY statements before 'RUN dotnet restore'. Please add markers manually.");
    }

    // Now find the start of the COPY block (consecutive COPY lines for .csproj or .props files)
    var copyBlockStart = copyBlockEnd;
    while (copyBlockStart >= 0)
    {
        var line = lines[copyBlockStart].Trim();

        // Check if this is a COPY line for .csproj or .props files
        if (line.StartsWith("COPY", StringComparison.OrdinalIgnoreCase) &&
            (line.Contains(".csproj", StringComparison.OrdinalIgnoreCase) ||
             line.Contains(".props", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("Directory.Build", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("Directory.Packages", StringComparison.OrdinalIgnoreCase)))
        {
            copyBlockStart--;
        }
        // Skip comments within the block
        else if (line.StartsWith("#") && copyBlockStart < copyBlockEnd)
        {
            copyBlockStart--;
        }
        // Skip empty lines within the block
        else if (string.IsNullOrEmpty(line) && copyBlockStart < copyBlockEnd)
        {
            copyBlockStart--;
        }
        else
        {
            break;
        }
    }
    copyBlockStart++; // Adjust back to first COPY line

    // Comments and blank lines above the first COPY line are not part of the block.
    while (copyBlockStart <= copyBlockEnd && !lines[copyBlockStart].Trim().StartsWith("COPY", StringComparison.OrdinalIgnoreCase))
    {
        copyBlockStart++;
    }

    if (copyBlockStart > copyBlockEnd)
    {
        return (false, "Could not identify COPY block for project files. Please add markers manually.");
    }

    // Remove the old COPY block and insert new one
    var newLines = new List<string>();

    // Add lines before the COPY block
    for (int i = 0; i < copyBlockStart; i++)
    {
        newLines.Add(lines[i]);
    }

    // Add new COPY statements (without trailing newline since we'll join with \n)
    var copyLines = newCopyStatements.TrimEnd('\n', '\r').Split('\n');
    foreach (var copyLine in copyLines)
    {
        newLines.Add(copyLine);
    }

    // Add lines after the COPY block, including any comments between it and the restore line
    for (int i = copyBlockEnd + 1; i < lines.Count; i++)
    {
        newLines.Add(lines[i]);
    }

    var newContent = string.Join("\n", newLines);
    File.WriteAllText(dockerfilePath, newContent);

    return (true, "");
}

static (bool Success, string Message) UpdateWorkflow(string workflowPath, string projectPath, HashSet<string> dependencies,
    string? directoryBuildProps, string? directoryPackagesProps, string repoRoot, HashSet<string> generatedEntries)
{
    var content = File.ReadAllText(workflowPath);
    var beginMarker = "# BEGIN AUTO-GENERATED PATHS";
    var endMarker = "# END AUTO-GENERATED PATHS";

    // Collect unique directories for paths
    var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Collect individual files that should be tracked (not as directories with /**)
    var individualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Add main project directory
    var projectDir = Path.GetDirectoryName(projectPath)!;
    var projectRelativePath = GetRelativePath(repoRoot, projectDir).Replace("\\", "/");
    uniquePaths.Add(projectRelativePath);

    // Add dependency directories
    foreach (var dep in dependencies)
    {
        var depDir = Path.GetDirectoryName(dep)!;
        var depRelativePath = GetRelativePath(repoRoot, depDir).Replace("\\", "/");
        uniquePaths.Add(depRelativePath);
    }

    // Add Directory.Build.props if exists
    if (directoryBuildProps != null)
    {
        var propsRelativePath = GetRelativePath(repoRoot, directoryBuildProps).Replace("\\", "/");
        individualFiles.Add(propsRelativePath);
    }

    // Add Directory.Packages.props if exists
    if (directoryPackagesProps != null)
    {
        var propsRelativePath = GetRelativePath(repoRoot, directoryPackagesProps).Replace("\\", "/");
        individualFiles.Add(propsRelativePath);
    }

    var workflowRelativePath = GetRelativePath(repoRoot, workflowPath).Replace("\\", "/");

    // Strategy 1: Use markers if they exist
    if (content.Contains(beginMarker))
    {
        var updatedContent = content;
        var searchStart = 0;

        while (true)
        {
            var beginIndex = updatedContent.IndexOf(beginMarker, searchStart);
            if (beginIndex == -1) break;

            var endIndex = updatedContent.IndexOf(endMarker, beginIndex);
            if (endIndex == -1) break;

            // Detect indentation
            var lineStart = updatedContent.LastIndexOf('\n', beginIndex) + 1;
            var markerLine = updatedContent.Substring(lineStart, beginIndex - lineStart);
            var indentation = markerLine.TakeWhile(char.IsWhiteSpace).Count();
            var indent = new string(' ', indentation);

            var sb = new StringBuilder();
            sb.AppendLine(beginMarker);

            // Write sorted directory paths (with /**)
            foreach (var path in uniquePaths.OrderBy(p => p))
            {
                sb.AppendLine($"{indent}- \"{path}/**\"");
            }

            // Write individual files (Directory.Build.props, Directory.Packages.props)
            foreach (var file in individualFiles.OrderBy(f => f))
            {
                sb.AppendLine($"{indent}- \"{file}\"");
            }

            // Add workflow file itself
            sb.AppendLine($"{indent}- \"{workflowRelativePath}\"");

            sb.Append($"{indent}{endMarker}");

            updatedContent = updatedContent.Substring(0, beginIndex) + sb.ToString() + updatedContent.Substring(endIndex + endMarker.Length);

            // Move search position past this replacement
            searchStart = beginIndex + sb.Length;
        }

        File.WriteAllText(workflowPath, updatedContent);
        return (true, "Updated using markers");
    }

    // Strategy 2: Find and replace paths: sections without markers
    var result = UpdateWorkflowWithoutMarkers(workflowPath, content, uniquePaths, individualFiles, workflowRelativePath, generatedEntries, repoRoot);
    if (result.Success)
    {
        return (true, "Updated using pattern detection (no markers)");
    }

    return (false, result.Message);
}

// Updates every paths: list under a push, pull_request or pull_request_target trigger. Only the entries this action generates are
// replaced: project directories, Directory.Build.props, Directory.Packages.props and the workflow file. Every
// other entry and comment was written by hand and is kept where it is.
static (bool Success, string Message) UpdateWorkflowWithoutMarkers(string workflowPath, string content, HashSet<string> uniquePaths,
    HashSet<string> individualFiles, string workflowRelativePath, HashSet<string> generatedEntries, string repoRoot)
{
    bool IsGenerated(string entry) => IsGeneratedPathEntry(entry, generatedEntries, individualFiles, workflowRelativePath, repoRoot);

    var lines = content.Split('\n').ToList();
    var found = false;

    for (int i = 0; i < lines.Count; i++)
    {
        var line = lines[i];
        var trimmed = line.Trim();
        if (!Regex.IsMatch(trimmed, @"^paths:\s*(#.*)?$"))
        {
            continue;
        }

        var pathsIndent = line.Length - line.TrimStart().Length;
        var trigger = FindParentKey(lines, i, pathsIndent);
        if (trigger is not ("push" or "pull_request" or "pull_request_target"))
        {
            continue;
        }

        // The list runs until the first non-blank, non-comment line that is not a list item.
        var listStart = i + 1;
        var listEnd = listStart;
        while (listEnd < lines.Count)
        {
            var next = lines[listEnd].TrimStart();
            if (next.Length == 0 || next.StartsWith("#") || next.StartsWith("-"))
            {
                listEnd++;
                continue;
            }
            break;
        }

        var items = lines.GetRange(listStart, listEnd - listStart);
        var firstItem = items.FirstOrDefault(l => l.TrimStart().StartsWith("-"));
        if (firstItem == null)
        {
            continue;
        }
        found = true;

        var itemIndent = new string(' ', firstItem.Length - firstItem.TrimStart().Length);
        var handWritten = items
            .Where(l => l.TrimStart().StartsWith("-"))
            .Select(ParsePathEntry)
            .Where(entry => !IsGenerated(entry))
            .ToList();

        // Directories the hand-written entries already include, such as "src/**", are not added.
        var wantedDirs = uniquePaths
            .Select(path => path + "/**")
            .Where(entry => !HandWrittenIncludes(handWritten, entry.Substring(0, entry.Length - 3)))
            .ToHashSet(StringComparer.Ordinal);
        var wantedFiles = individualFiles.Append(workflowRelativePath).ToHashSet(StringComparer.Ordinal);

        // Reconcile in place: generated entries still wanted keep their line, unwanted or duplicate ones are
        // dropped, and hand-written entries and comments are untouched.
        var newItems = new List<string>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        var firstGeneratedIndex = -1;
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.TrimStart().StartsWith("-"))
            {
                var entry = ParsePathEntry(item);
                if (IsGenerated(entry))
                {
                    if (firstGeneratedIndex == -1)
                    {
                        firstGeneratedIndex = newItems.Count;
                    }
                    if (!(wantedDirs.Contains(entry) || wantedFiles.Contains(entry)) ||
                        LastExcludingNegation(items, entry) > index ||
                        !present.Add(entry))
                    {
                        continue;
                    }
                }
            }
            newItems.Add(item);
        }

        // Missing directories go in sorted position among the generated directories, missing files after them.
        // Same order as the generated list: the directory names, compared without the trailing "/**".
        var missing = wantedDirs.OrderBy(d => d.Substring(0, d.Length - 3)).Where(d => !present.Contains(d)).ToList();
        foreach (var file in individualFiles.OrderBy(f => f).Append(workflowRelativePath))
        {
            if (!present.Contains(file))
            {
                missing.Add(file);
            }
        }
        foreach (var entry in missing)
        {
            var newLine = $"{itemIndent}- \"{entry}\"";
            var isDir = wantedDirs.Contains(entry);
            var generatedIndexes = Enumerable.Range(0, newItems.Count)
                .Where(k => newItems[k].TrimStart().StartsWith("-") &&
                            IsGenerated(ParsePathEntry(newItems[k])))
                .ToList();
            var dirIndexes = generatedIndexes.Where(k => !wantedFiles.Contains(ParsePathEntry(newItems[k]))).ToList();

            int insertAt;
            if (isDir && dirIndexes.Count > 0)
            {
                // Right after the last generated directory that sorts before it, or before the first one.
                var dirName = entry.Substring(0, entry.Length - 3);
                var before = dirIndexes.Where(k =>
                {
                    var existing = ParsePathEntry(newItems[k]);
                    return string.Compare(existing.Substring(0, existing.Length - 3), dirName) < 0;
                }).ToList();
                insertAt = before.Count > 0 ? before.Last() + 1 : dirIndexes.First();
            }
            else if (!isDir && generatedIndexes.Count > 0)
            {
                insertAt = generatedIndexes.Last() + 1;
            }
            else if (generatedIndexes.Count > 0)
            {
                insertAt = generatedIndexes.First();
            }
            else
            {
                insertAt = firstGeneratedIndex >= 0 ? Math.Min(firstGeneratedIndex, newItems.Count)
                    : newItems.FindIndex(l => l.TrimStart().StartsWith("-"));
            }
            // Below any hand-written "!" pattern that excludes it, as GitHub lets the last matching pattern decide.
            insertAt = Math.Max(insertAt, LastExcludingNegation(newItems, entry) + 1);
            newItems.Insert(insertAt, newLine);
        }

        lines.RemoveRange(listStart, items.Count);
        lines.InsertRange(listStart, newItems);
        i = listStart + newItems.Count - 1;
    }

    if (!found)
    {
        return (false, "Could not find 'paths:' sections in workflow file. Please add markers manually:\n" +
                      "    # BEGIN AUTO-GENERATED PATHS\n" +
                      "    # END AUTO-GENERATED PATHS");
    }

    var newContent = string.Join("\n", lines);
    if (newContent != content)
    {
        File.WriteAllText(workflowPath, newContent);
    }

    return (true, "");
}

// The key of the nearest enclosing mapping, e.g. "push" for a paths: list nested under push:.
static string? FindParentKey(List<string> lines, int index, int indent)
{
    for (int j = index - 1; j >= 0; j--)
    {
        var candidate = lines[j];
        var trimmed = candidate.TrimStart();
        if (trimmed.Length == 0 || trimmed.StartsWith("#"))
        {
            continue;
        }
        if (candidate.Length - trimmed.Length < indent)
        {
            var match = Regex.Match(trimmed, @"^['""]?([A-Za-z_][A-Za-z0-9_-]*)['""]?:");
            return match.Success ? match.Groups[1].Value : null;
        }
    }
    return null;
}

// The path in a "- \"src/Foo/**\"  # comment" list item.
static string ParsePathEntry(string line)
{
    var value = line.TrimStart().Substring(1).Trim();
    if (value.StartsWith("\"") || value.StartsWith("'"))
    {
        var quote = value[0];
        var close = value.IndexOf(quote, 1);
        return close > 0 ? value.Substring(1, close - 1) : value.Trim(quote);
    }
    var comment = value.IndexOf(" #");
    return (comment >= 0 ? value.Substring(0, comment) : value).Trim();
}

// An entry this action owns: the workflow file, a project directory, one of this deployable's props files, or a
// stale entry for a directory or props file that no longer exists (a renamed or deleted project). Paths are
// compared case-sensitively, as GitHub matches them.
static bool IsGeneratedPathEntry(string entry, HashSet<string> generatedEntries, HashSet<string> propsFiles,
    string workflowRelativePath, string repoRoot)
{
    if (entry == workflowRelativePath || generatedEntries.Contains(entry) || propsFiles.Contains(entry, StringComparer.Ordinal))
    {
        return true;
    }
    // Any other glob was written by hand.
    var isDirectoryEntry = entry.EndsWith("/**");
    var path = isDirectoryEntry ? entry.Substring(0, entry.Length - 3) : entry;
    if (path.IndexOfAny(new[] { '*', '?', '[', '!', '{' }) >= 0)
    {
        return false;
    }
    if (isDirectoryEntry)
    {
        return !PathExistsWithExactCase(repoRoot, path, directory: true);
    }
    var fileName = entry.Split('/').Last();
    return (fileName == "Directory.Build.props" || fileName == "Directory.Packages.props") &&
           !PathExistsWithExactCase(repoRoot, entry, directory: false);
}

static bool PathExistsWithExactCase(string repoRoot, string relativePath, bool directory)
{
    var current = repoRoot;
    var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < segments.Length; i++)
    {
        if (segments[i] == ".")
        {
            continue;
        }
        if (segments[i] == ".." || !Directory.Exists(current))
        {
            return true; // Outside what this check can see; treat as present so the entry is kept.
        }
        var isLast = i == segments.Length - 1;
        var candidates = isLast && !directory ? Directory.GetFiles(current) : Directory.GetDirectories(current);
        var match = candidates.FirstOrDefault(c => Path.GetFileName(c) == segments[i]);
        if (match == null)
        {
            return false;
        }
        current = match;
    }
    return true;
}

// Whether the hand-written patterns, applied in order as GitHub does, already include an ordinary file in the
// directory: the last pattern matching it decides, and a "!" pattern excludes. So "src/**" covers src/Api, while
// a later "!src/Tools/**" takes src/Tools/Shared out again. Patterns this check cannot read are ignored.
static bool HandWrittenIncludes(List<string> patterns, string directory)
{
    bool Includes(string file)
    {
        var included = false;
        foreach (var pattern in patterns)
        {
            var negated = pattern.StartsWith("!");
            if (GlobToRegex(negated ? pattern.Substring(1) : pattern) is Regex regex && regex.IsMatch(file))
            {
                included = !negated;
            }
        }
        return included;
    }
    return Includes(directory + "/file") && Includes(directory + "/sub/file");
}

// The index of the last "!" item in the list that excludes the file entry, or a file in the directory entry
// ("X/**"), or -1.
static int LastExcludingNegation(List<string> items, string pathEntry)
{
    var probes = pathEntry.EndsWith("/**")
        ? new[] { pathEntry.Substring(0, pathEntry.Length - 3) + "/file", pathEntry.Substring(0, pathEntry.Length - 3) + "/sub/file" }
        : new[] { pathEntry };
    for (int k = items.Count - 1; k >= 0; k--)
    {
        if (!items[k].TrimStart().StartsWith("-"))
        {
            continue;
        }
        var entry = ParsePathEntry(items[k]);
        if (entry.StartsWith("!") && GlobToRegex(entry.Substring(1)) is Regex regex && probes.Any(regex.IsMatch))
        {
            return k;
        }
    }
    return -1;
}

// GitHub's path filter glob: ** matches across directories, * within one. Returns null for syntax this
// check does not read ([ ] { } + ?, where GitHub's ? and + repeat the preceding character).
static Regex? GlobToRegex(string glob)
{
    if (glob.IndexOfAny(new[] { '[', ']', '{', '}', '+', '?' }) >= 0)
    {
        return null;
    }
    var sb = new StringBuilder("^");
    for (int i = 0; i < glob.Length; i++)
    {
        if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
        {
            var slashFollows = i + 2 < glob.Length && glob[i + 2] == '/';
            sb.Append(slashFollows ? "(?:.*/)?" : ".*");
            i += slashFollows ? 2 : 1;
        }
        else if (glob[i] == '*')
        {
            sb.Append("[^/]*");
        }
        else
        {
            sb.Append(Regex.Escape(glob[i].ToString()));
        }
    }
    return new Regex(sb.Append('$').ToString());
}

static void SetGitHubOutputs(int dockerfilesUpdated, int workflowsUpdated, int totalDependencies)
{
    var outputFile = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (outputFile != null)
    {
        File.AppendAllText(outputFile,
            $"dockerfiles-updated={dockerfilesUpdated}\n" +
            $"workflows-updated={workflowsUpdated}\n" +
            $"dependencies-count={totalDependencies}\n");
    }
}

static void PrintHelp()
{
    Console.WriteLine(@"
Sync .NET Dependencies to Dockerfile and GitHub Workflows

This tool analyzes your .NET project dependency graph and updates Dockerfiles
and GitHub workflow path filters to include all transitive dependencies.

It automatically detects which deployable projects (those with Dockerfiles)
are affected by changes to any project in the dependency tree.

Usage:
  dotnet run sync-dependencies.cs -- [options]

Options:
  --modified <files>   Comma-separated list of modified files (optional)
                       If not provided, all deployable projects are updated
  --repo-root <path>   Repository root directory (optional)
                       Defaults to GITHUB_WORKSPACE or current directory
  --help               Show this help message

Examples:
  # Update all deployable projects
  dotnet run sync-dependencies.cs

  # Update based on specific modified files
  dotnet run sync-dependencies.cs -- --modified ""src/Lib/Lib.csproj,src/Lib2/Lib2.csproj""

  # Specify repository root
  dotnet run sync-dependencies.cs -- --repo-root /path/to/repo

How it works:
  1. Discovers all .sln and .slnx files in the repository
  2. Builds a complete dependency graph by parsing .csproj files
  3. Finds all 'deployable' projects (those with Dockerfiles)
  4. Determines which projects were modified
  5. Finds all deployable projects affected by the modifications
     (including those that transitively depend on modified projects)
  6. Updates Dockerfiles and workflow files for affected projects

Detection Modes:
  The tool supports two modes for finding where to update files:

  1. MARKER-BASED (recommended for explicit control):
     Add these markers to your Dockerfile:
       # BEGIN AUTO-GENERATED PROJECT REFERENCES
       # END AUTO-GENERATED PROJECT REFERENCES

     Add these markers to your workflow files (under paths:):
       # BEGIN AUTO-GENERATED PATHS
       # END AUTO-GENERATED PATHS

  2. AUTOMATIC DETECTION (no markers needed):
     - Dockerfiles: Finds COPY statements for .csproj/.props files
       before 'RUN dotnet restore' and replaces them
     - Workflows: Finds 'paths:' sections and updates the generated entries, keeping hand-written ones

  The tool tries markers first, then falls back to automatic detection.
");
}

// ============================================================================
// Classes
// ============================================================================

class Arguments
{
    public List<string> ModifiedFiles { get; set; } = new();
    public string? RepoRoot { get; set; }
    public bool Help { get; set; }

    public Arguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--modified":
                    if (i + 1 < args.Length)
                    {
                        var files = args[++i];
                        ModifiedFiles = files.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(f => f.Trim())
                            .Where(f => !string.IsNullOrEmpty(f))
                            .ToList();
                    }
                    break;
                case "--repo-root":
                    if (i + 1 < args.Length) RepoRoot = args[++i];
                    break;
                case "--help":
                case "-h":
                    Help = true;
                    break;
            }
        }
    }
}

class RepositoryGraphBuilder
{
    private readonly string _repoRoot;
    private readonly List<string> _solutions;

    public RepositoryGraphBuilder(string repoRoot, List<string> solutions)
    {
        _repoRoot = repoRoot;
        _solutions = solutions;
    }

    public GraphBuildResult Build()
    {
        var result = new GraphBuildResult { Success = true };
        var allProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencyGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // First, discover all projects from solutions
        foreach (var solution in _solutions)
        {
            try
            {
                Console.WriteLine($"  Analyzing: {GetRelativePathHelper(_repoRoot, solution)}");
                var projectsInSolution = ParseSolutionFile(solution);

                foreach (var projectPath in projectsInSolution)
                {
                    if (!File.Exists(projectPath))
                    {
                        Console.WriteLine($"    ::warning::Project not found: {projectPath}");
                        continue;
                    }

                    // Check for legacy projects
                    if (IsLegacyFrameworkProject(projectPath))
                    {
                        Console.WriteLine($"    ::warning::Skipping legacy project: {Path.GetFileNameWithoutExtension(projectPath)}");
                        continue;
                    }

                    allProjects.Add(projectPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ::warning::Failed to parse solution {Path.GetFileName(solution)}: {ex.Message}");
            }
        }

        // Now build dependency graph by parsing each project file
        foreach (var projectPath in allProjects)
        {
            try
            {
                var references = GetProjectReferences(projectPath);

                if (!dependencyGraph.ContainsKey(projectPath))
                {
                    dependencyGraph[projectPath] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                foreach (var refPath in references)
                {
                    // Only add if it's a known project
                    if (allProjects.Contains(refPath))
                    {
                        dependencyGraph[projectPath].Add(refPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ::warning::Failed to parse project {Path.GetFileName(projectPath)}: {ex.Message}");
            }
        }

        result.AllProjects = allProjects.ToList();
        result.DependencyGraph = dependencyGraph;

        // Check for circular dependencies
        var circularCheck = DetectCircularDependencies(dependencyGraph);
        if (circularCheck != null)
        {
            result.Success = false;
            result.ErrorMessage = $"Circular dependency detected: {circularCheck}";
        }

        return result;
    }

    private List<string> ParseSolutionFile(string solutionPath)
    {
        var projects = new List<string>();
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        // Handle .slnx files (XML format)
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var doc = XDocument.Load(solutionPath);
            // Use LocalName to handle namespaced elements
            var projectElements = doc.Descendants()
                .Where(e => e.Name.LocalName == "Project")
                .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Path")?.Value)
                .Where(p => p != null && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

            foreach (var relativePath in projectElements)
            {
                var normalizedPath = relativePath!.Replace("\\", Path.DirectorySeparatorChar.ToString());
                var fullPath = Path.GetFullPath(Path.Combine(solutionDir, normalizedPath));
                projects.Add(fullPath);
            }

            return projects;
        }

        // Handle .sln files (text format)
        var content = File.ReadAllText(solutionPath);

        // Match project references in solution file
        // Format: Project("{GUID}") = "ProjectName", "RelativePath\Project.csproj", "{GUID}"
        var regex = new Regex(@"Project\(""\{[^}]+\}""\)\s*=\s*""[^""]+"",\s*""([^""]+\.csproj)""", RegexOptions.IgnoreCase);

        foreach (Match match in regex.Matches(content))
        {
            var relativePath = match.Groups[1].Value.Replace("\\", Path.DirectorySeparatorChar.ToString());
            var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
            projects.Add(fullPath);
        }

        return projects;
    }

    private List<string> GetProjectReferences(string projectPath)
    {
        var references = new List<string>();
        var projectDir = Path.GetDirectoryName(projectPath)!;

        try
        {
            var doc = XDocument.Load(projectPath);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            // Find all ProjectReference elements
            var projectRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null);

            foreach (var refPath in projectRefs)
            {
                var relativePath = refPath!.Replace("\\", Path.DirectorySeparatorChar.ToString());
                var fullPath = Path.GetFullPath(Path.Combine(projectDir, relativePath));
                references.Add(fullPath);
            }
        }
        catch
        {
            // Silently ignore parse errors for individual projects
        }

        return references;
    }

    private bool IsLegacyFrameworkProject(string projectPath)
    {
        try
        {
            var content = File.ReadAllText(projectPath);

            // Check if it's an SDK-style project
            if (!content.Contains("<Project Sdk=", StringComparison.OrdinalIgnoreCase))
            {
                // Old-style project format
                return true;
            }

            var doc = XDocument.Load(projectPath);

            var targetFramework = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value;
            var targetFrameworks = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;

            // If not found in project file, check Directory.Build.props
            if (string.IsNullOrEmpty(targetFramework) && string.IsNullOrEmpty(targetFrameworks))
            {
                var projectDir = Path.GetDirectoryName(projectPath)!;
                var directoryBuildProps = FindDirectoryBuildProps(projectDir);

                if (directoryBuildProps != null)
                {
                    var propsDoc = XDocument.Load(directoryBuildProps);
                    targetFramework = propsDoc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value;
                    targetFrameworks = propsDoc.Descendants()
                        .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;
                }
            }

            // If still not found, assume modern (SDK-style projects default to modern)
            if (string.IsNullOrEmpty(targetFramework) && string.IsNullOrEmpty(targetFrameworks))
            {
                return false; // SDK-style project without explicit TFM - assume modern
            }

            var frameworks = !string.IsNullOrEmpty(targetFrameworks)
                ? targetFrameworks.Split(';')
                : new[] { targetFramework! };

            bool hasModernFramework = false;
            bool hasOnlyLegacyFrameworks = true;

            foreach (var fw in frameworks)
            {
                if (string.IsNullOrEmpty(fw)) continue;

                // Modern frameworks
                if (fw.StartsWith("net5.") || fw.StartsWith("net6.") || fw.StartsWith("net7.") ||
                    fw.StartsWith("net8.") || fw.StartsWith("net9.") || fw.StartsWith("net10.") ||
                    fw.StartsWith("netstandard") || fw.StartsWith("netcoreapp"))
                {
                    hasModernFramework = true;
                    hasOnlyLegacyFrameworks = false;
                }
                // Legacy frameworks
                else if (fw.StartsWith("net4") || fw.StartsWith("net3") || fw.StartsWith("net2"))
                {
                    // Legacy framework found, but don't return yet - check if there's also a modern one
                }
                else
                {
                    // Unknown framework, assume it's not legacy
                    hasOnlyLegacyFrameworks = false;
                }
            }

            // Only mark as legacy if it has NO modern framework targets
            // Multi-targeted projects (e.g., net8.0;net48) should be treated as modern
            return !hasModernFramework && hasOnlyLegacyFrameworks;
        }
        catch
        {
            return false; // Assume modern if we can't parse
        }
    }

    private string? FindDirectoryBuildProps(string startDir)
    {
        var current = startDir;
        while (current != null)
        {
            var propsPath = Path.Combine(current, "Directory.Build.props");
            if (File.Exists(propsPath))
            {
                return propsPath;
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current || parent == null) break;
            current = parent;
        }
        return null;
    }

    private string? DetectCircularDependencies(Dictionary<string, HashSet<string>> graph)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recursionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var node in graph.Keys)
        {
            var cycle = DfsCycleDetect(node, graph, visited, recursionStack, path);
            if (cycle != null)
            {
                return cycle;
            }
        }

        return null;
    }

    private string? DfsCycleDetect(string node, Dictionary<string, HashSet<string>> graph,
        HashSet<string> visited, HashSet<string> recursionStack, List<string> path)
    {
        if (recursionStack.Contains(node))
        {
            var cycleStart = path.IndexOf(node);
            var cyclePath = path.Skip(cycleStart).Append(node).Select(p => Path.GetFileNameWithoutExtension(p));
            return string.Join(" -> ", cyclePath);
        }

        if (visited.Contains(node))
        {
            return null;
        }

        visited.Add(node);
        recursionStack.Add(node);
        path.Add(node);

        if (graph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                var cycle = DfsCycleDetect(neighbor, graph, visited, recursionStack, path);
                if (cycle != null)
                {
                    return cycle;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        recursionStack.Remove(node);
        return null;
    }

    private static string GetRelativePathHelper(string fromPath, string toPath)
    {
        if (string.IsNullOrEmpty(fromPath)) throw new ArgumentNullException(nameof(fromPath));
        if (string.IsNullOrEmpty(toPath)) throw new ArgumentNullException(nameof(toPath));

        var fromUri = new Uri(AppendDirSeparator(fromPath));
        var toUri = new Uri(toPath);

        if (fromUri.Scheme != toUri.Scheme) return toPath;

        var relativeUri = fromUri.MakeRelativeUri(toUri);
        var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

        if (toUri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        return relativePath;
    }

    private static string AppendDirSeparator(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()) && !path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
        {
            return path + Path.DirectorySeparatorChar;
        }
        return path;
    }
}

class GraphBuildResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> AllProjects { get; set; } = new();
    public Dictionary<string, HashSet<string>> DependencyGraph { get; set; } = new();
}
