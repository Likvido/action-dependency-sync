#!/usr/bin/dotnet run

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    Console.WriteLine("::error::No .sln files found in repository");
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
var deployableProjects = FindDeployableProjects(graphResult.AllProjects, repoRoot);
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

    // Update Dockerfile
    var dockerfilePath = FindDockerfile(projectDir, repoRoot);
    if (dockerfilePath != null)
    {
        Console.WriteLine($"\nUpdating Dockerfile: {GetRelativePath(repoRoot, dockerfilePath)}");
        var result = UpdateDockerfile(dockerfilePath, deployableProject, dependencies, directoryBuildProps, directoryPackagesProps, repoRoot);
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

    // Update workflow file
    var workflowPath = FindWorkflowFile(deployableProject, repoRoot);
    if (workflowPath != null)
    {
        Console.WriteLine($"Updating workflow: {GetRelativePath(repoRoot, workflowPath)}");
        var result = UpdateWorkflow(workflowPath, deployableProject, dependencies, repoRoot);
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
    return Directory.GetFiles(repoRoot, "*.sln", SearchOption.AllDirectories)
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
        if (dockerfile != null)
        {
            deployable.Add(project);
        }
    }
    return deployable;
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

static string? FindWorkflowFile(string projectPath, string repoRoot)
{
    var workflowDir = Path.Combine(repoRoot, ".github", "workflows");
    if (!Directory.Exists(workflowDir))
    {
        return null;
    }

    var projectName = Path.GetFileNameWithoutExtension(projectPath);
    var projectRelativePath = GetRelativePath(repoRoot, projectPath).Replace("\\", "/");

    foreach (var workflow in Directory.GetFiles(workflowDir, "*.yml").Concat(Directory.GetFiles(workflowDir, "*.yaml")))
    {
        var content = File.ReadAllText(workflow);
        if (content.Contains(projectRelativePath) || content.Contains(projectName))
        {
            return workflow;
        }
    }

    return null;
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

static (bool Success, string Message) UpdateDockerfile(string dockerfilePath, string projectPath, HashSet<string> dependencies,
    string? directoryBuildProps, string? directoryPackagesProps, string repoRoot)
{
    var content = File.ReadAllText(dockerfilePath);
    var beginMarker = "# BEGIN AUTO-GENERATED PROJECT REFERENCES";
    var endMarker = "# END AUTO-GENERATED PROJECT REFERENCES";

    var beginIndex = content.IndexOf(beginMarker);
    var endIndex = content.IndexOf(endMarker);

    // Determine docker context - the directory containing the Dockerfile
    // This is what docker build uses as the context when building
    var dockerContext = Path.GetDirectoryName(dockerfilePath)!;

    // However, in many setups, the context is a parent directory (where the solution is)
    // We detect this by looking at the existing COPY paths in the Dockerfile
    var detectedContext = DetectDockerContext(content, dockerfilePath, repoRoot);
    if (detectedContext != null)
    {
        dockerContext = detectedContext;
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

    // Add lines after the COPY block (from restoreLineIndex onwards)
    for (int i = restoreLineIndex; i < lines.Count; i++)
    {
        newLines.Add(lines[i]);
    }

    var newContent = string.Join("\n", newLines);
    File.WriteAllText(dockerfilePath, newContent);

    return (true, "");
}

static (bool Success, string Message) UpdateWorkflow(string workflowPath, string projectPath, HashSet<string> dependencies, string repoRoot)
{
    var content = File.ReadAllText(workflowPath);
    var beginMarker = "# BEGIN AUTO-GENERATED PATHS";
    var endMarker = "# END AUTO-GENERATED PATHS";

    // Collect unique directories for paths
    var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            // Write sorted paths
            foreach (var path in uniquePaths.OrderBy(p => p))
            {
                sb.AppendLine($"{indent}- \"{path}/**\"");
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
    var result = UpdateWorkflowWithoutMarkers(workflowPath, content, uniquePaths, workflowRelativePath);
    if (result.Success)
    {
        return (true, "Updated using pattern detection (no markers)");
    }

    return (false, result.Message);
}

static (bool Success, string Message) UpdateWorkflowWithoutMarkers(string workflowPath, string content, HashSet<string> uniquePaths, string workflowRelativePath)
{
    var lines = content.Split('\n').ToList();
    var modified = false;

    // Find all "paths:" lines and replace their content
    for (int i = 0; i < lines.Count; i++)
    {
        var line = lines[i];
        var trimmedLine = line.TrimEnd();

        // Check if this line is "paths:" (with optional leading whitespace)
        if (trimmedLine.TrimStart().Equals("paths:", StringComparison.OrdinalIgnoreCase) ||
            trimmedLine.TrimStart().StartsWith("paths:", StringComparison.OrdinalIgnoreCase))
        {
            // Get the indentation of the "paths:" line
            var pathsIndent = line.Length - line.TrimStart().Length;

            // Find the end of the paths array (next line with same or less indentation that isn't a list item or empty)
            var pathsStart = i + 1;
            var pathsEnd = pathsStart;

            while (pathsEnd < lines.Count)
            {
                var nextLine = lines[pathsEnd];
                var nextTrimmed = nextLine.TrimStart();

                // Empty line - continue
                if (string.IsNullOrWhiteSpace(nextLine))
                {
                    pathsEnd++;
                    continue;
                }

                // Comment line within paths - continue
                if (nextTrimmed.StartsWith("#"))
                {
                    pathsEnd++;
                    continue;
                }

                // List item (starts with -) - this is part of paths array
                if (nextTrimmed.StartsWith("-"))
                {
                    pathsEnd++;
                    continue;
                }

                // Check indentation - if same or less than paths:, we've exited the array
                var nextIndent = nextLine.Length - nextLine.TrimStart().Length;
                if (nextIndent <= pathsIndent)
                {
                    break;
                }

                pathsEnd++;
            }

            // Only proceed if we found some paths entries
            if (pathsEnd > pathsStart)
            {
                // Determine the indentation for list items (typically 2 more than paths:)
                var itemIndent = new string(' ', pathsIndent + 2);

                // Build new paths entries
                var newPathLines = new List<string>();
                foreach (var path in uniquePaths.OrderBy(p => p))
                {
                    newPathLines.Add($"{itemIndent}- \"{path}/**\"");
                }
                newPathLines.Add($"{itemIndent}- \"{workflowRelativePath}\"");

                // Replace the old paths entries with new ones
                lines.RemoveRange(pathsStart, pathsEnd - pathsStart);
                lines.InsertRange(pathsStart, newPathLines);

                // Adjust index since we modified the list
                i = pathsStart + newPathLines.Count - 1;
                modified = true;
            }
        }
    }

    if (!modified)
    {
        return (false, "Could not find 'paths:' sections in workflow file. Please add markers manually:\n" +
                      "    # BEGIN AUTO-GENERATED PATHS\n" +
                      "    # END AUTO-GENERATED PATHS");
    }

    var newContent = string.Join("\n", lines);
    File.WriteAllText(workflowPath, newContent);

    return (true, "");
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
  1. Discovers all .sln files in the repository
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
     - Workflows: Finds 'paths:' sections and replaces their contents

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
