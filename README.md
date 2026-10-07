# Sync .NET Dependencies GitHub Action

Automatically syncs .NET project dependencies to Dockerfiles and GitHub workflow path filters. This action analyzes your entire repository's project dependency graph and intelligently updates only the files affected by changes.

## Key Feature: Transitive Dependency Detection

When you modify a library that's referenced by other projects, this action automatically detects all affected deployable projects (those with Dockerfiles) and updates their files - even if those projects weren't directly modified.

**Example scenario:**
```
Robot.csproj → Library.csproj → NewLibrary.csproj (newly added)
```

If you add `NewLibrary.csproj` and reference it from `Library.csproj`:
- The action detects that `Library.csproj` was modified
- It finds that `Robot.csproj` depends on `Library.csproj`
- It updates `Robot/Dockerfile` to include `NewLibrary.csproj` in the COPY statements

## Features

- **Smart dependency detection** - Finds all deployable projects affected by changes, including transitive dependencies
- **Repository-wide analysis** - Scans all `.sln or .slnx` files to build a complete dependency graph
- **Dockerfile updates** - Generates COPY statements for all project references
- **Workflow path updates** - Generates path filters for efficient CI/CD triggering
- **Automatic detection** - Works without markers by detecting patterns in files
- **Marker support** - Optional markers for explicit control over where updates occur
- **Directory.Build.props support** - Automatically includes `Directory.Build.props` and `Directory.Packages.props`
- **Test project filtering** - Automatically skips test projects
- **Circular dependency detection** - Fails with clear error if cycles are found
- **Legacy project warnings** - Warns about .NET Framework projects
- **Self-contained** - Uses .NET 10 file-based scripting with no external dependencies

## Prerequisites

- .NET 10 SDK or higher must be available in your workflow
- A GitHub App with `contents: write` and `workflows: write` permissions (because `GITHUB_TOKEN` cannot modify workflow files)

## Quick Start

The action supports two modes for detecting where to update files:

### Option 1: Automatic Detection (No Markers Needed)

The action can automatically find and replace the relevant sections:

- **Dockerfiles**: Finds COPY statements for `.csproj`/`.props` files before `RUN dotnet restore` and replaces them. Comments around the block are kept. A Dockerfile without markers that restores a whole solution (`dotnet restore My.sln`) is skipped, because that restore needs every project the solution names; add markers to have its COPY block generated anyway
- **Workflows**: Finds `paths:` sections under `push:`, `pull_request:` and `pull_request_target:` and updates only the entries the action generates — the directories of the solution's projects other than test projects and the projects only they reference, the deployable's own `Directory.Build.props` and `Directory.Packages.props`, and the workflow file itself. An entry for a directory or props file that no longer exists (a renamed or deleted project) is removed too; paths are compared case-sensitively, as GitHub matches them. Every other entry (a `Dockerfile`, a `.sln`, `scripts/**`, a test project's directory, or a project only test projects reference) and every comment is left where it is. A library that only its tests still reference therefore keeps its entry until it is removed by hand. A generated directory the hand-written patterns already include, such as `src/**`, is not added; patterns are applied in order as GitHub does, so a later `!src/Tools/**` takes `src/Tools/...` out again and the directory is then added

This works out of the box with standard Dockerfile and workflow structures.

### Option 2: Explicit Markers (More Control)

For more explicit control over exactly where updates occur, add markers to your files:

#### Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# BEGIN AUTO-GENERATED PROJECT REFERENCES
# END AUTO-GENERATED PROJECT REFERENCES

RUN dotnet restore "MyApp/MyApp.csproj"
```

#### GitHub Workflow

```yaml
on:
  push:
    branches: [main]
    paths:
      # BEGIN AUTO-GENERATED PATHS
      # END AUTO-GENERATED PATHS
  pull_request:
    branches: [main]
    paths:
      # BEGIN AUTO-GENERATED PATHS
      # END AUTO-GENERATED PATHS
```

The action checks for markers first, then falls back to automatic detection.

### Create a Sync Workflow

Create `.github/workflows/sync-dependencies.yml`:

```yaml
name: Sync Dependencies

on:
  push:
    branches-ignore:
      - main
      - master
      - develop
    paths:
      - '**/*.csproj'
      - '**/Directory.Build.props'
      - '**/Directory.Packages.props'

jobs:
  sync:
    runs-on: ubuntu-latest

    steps:
      # GitHub App token required because GITHUB_TOKEN cannot modify workflow files
      - name: Generate GitHub App Token
        id: app-token
        uses: actions/create-github-app-token@v1
        with:
          app-id: ${{ secrets.YOUR_APP_ID }}
          private-key: ${{ secrets.YOUR_APP_PRIVATE_KEY }}

      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-depth: 0  # Needed to compare with base branch
          token: ${{ steps.app-token.outputs.token }}

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0'

      - name: Sync Dependencies
        uses: Likvido/action-dependency-sync@v1
        with:
          base-ref: origin/main  # Compare against main to find changes

      - name: Commit Changes
        run: |
          git config --local user.email "your-bot[bot]@users.noreply.github.com"
          git config --local user.name "Your Bot Name"
          git add -A
          git diff --staged --quiet || git commit -m "chore: sync project dependencies"
          git push
```

## How It Works

```
┌─────────────────────────────────────────────────────────────────┐
│ 1. Discover all .sln or .slnx files in repository               │
├─────────────────────────────────────────────────────────────────┤
│ 2. Build complete dependency graph by parsing .csproj files     │
├─────────────────────────────────────────────────────────────────┤
│ 3. Find all "deployable" projects                               │
│    - A Dockerfile in the project's own directory, OR            │
│    - A parent directory's Dockerfile whose image publishes it   │
│    - Test projects are never deployable                         │
├─────────────────────────────────────────────────────────────────┤
│ 4. Determine which projects were modified                       │
│    - From --modified input, OR                                  │
│    - From git diff against base-ref, OR                         │
│    - All projects (if no input provided)                        │
├─────────────────────────────────────────────────────────────────┤
│ 5. Find affected deployable projects                            │
│    - Deployables that were directly modified                    │
│    - Deployables that transitively depend on modified projects  │
├─────────────────────────────────────────────────────────────────┤
│ 6. Update Dockerfiles and workflows for affected projects       │
│    - A workflow belongs to the Dockerfile its                   │
│      DOCKER_WORKING_DIRECTORY + DOCKERFILE_RELATIVE_PATH name   │
│    - Other workflows are matched by project path, dir or name   │
│    - Uses markers if present                                    │
│    - Falls back to pattern detection if no markers              │
└─────────────────────────────────────────────────────────────────┘
```

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `modified-files` | Comma- or newline-separated list of modified files | No | Auto-detect |
| `base-ref` | Git ref to compare against (e.g., `origin/main`) | No | - |
| `repository-root` | Root directory of the repository | No | `GITHUB_WORKSPACE` |

### Input Priority

1. If `modified-files` is provided, those files are used directly
2. If `base-ref` is provided, git diff is used to detect changes
3. If neither is provided, all deployable projects are updated

## Outputs

| Output | Description |
|--------|-------------|
| `dockerfiles-updated` | Number of Dockerfiles that were updated |
| `workflows-updated` | Number of workflow files that were updated |
| `dependencies-count` | Total number of dependencies processed |

## Examples

### Auto-detect Changes from Base Branch

```yaml
- name: Sync Dependencies
  uses: Likvido/action-dependency-sync@v1
  with:
    base-ref: origin/main
```

### Provide Modified Files Explicitly

Useful when integrating with other actions that detect file changes:

```yaml
- name: Get changed files
  id: changed
  uses: tj-actions/changed-files@v44
  with:
    separator: ","
    files: |
      **/*.csproj
      **/Directory.Build.props
      **/Directory.Packages.props

- name: Sync Dependencies
  uses: Likvido/action-dependency-sync@v1
  with:
    modified-files: ${{ steps.changed.outputs.all_changed_files }}
```

### Update All Projects

When no inputs are provided, all deployable projects are analyzed and updated:

```yaml
- name: Sync All Dependencies
  uses: Likvido/action-dependency-sync@v1
```

### Use Outputs

```yaml
- name: Sync Dependencies
  id: sync
  uses: Likvido/action-dependency-sync@v1
  with:
    base-ref: origin/main

- name: Check Results
  run: |
    echo "Dockerfiles updated: ${{ steps.sync.outputs.dockerfiles-updated }}"
    echo "Workflows updated: ${{ steps.sync.outputs.workflows-updated }}"
    echo "Dependencies processed: ${{ steps.sync.outputs.dependencies-count }}"

- name: Commit if changes were made
  if: steps.sync.outputs.dockerfiles-updated != '0' || steps.sync.outputs.workflows-updated != '0'
  run: |
    git add -A
    git commit -m "chore: sync dependencies"
    git push
```

## Running Locally

You can run the script locally for testing:

```bash
# Update all deployable projects
dotnet run sync-dependencies.cs

# Update based on specific modified files
dotnet run sync-dependencies.cs -- --modified "src/Lib/Lib.csproj,src/Lib2/Lib2.csproj"

# Specify repository root
dotnet run sync-dependencies.cs -- --repo-root /path/to/repo

# Show help
dotnet run sync-dependencies.cs -- --help
```

## Generated Output Examples

### Dockerfile (with markers)

Paths are relative to the Docker build context (detected automatically from existing COPY statements):

```dockerfile
# BEGIN AUTO-GENERATED PROJECT REFERENCES
COPY ["Directory.Build.props", "./"]
COPY ["Likvido.Core/Likvido.Core.csproj", "Likvido.Core/"]
COPY ["Likvido.Domain/Likvido.Domain.csproj", "Likvido.Domain/"]
COPY ["Likvido.Infrastructure/Likvido.Infrastructure.csproj", "Likvido.Infrastructure/"]
COPY ["Likvido.Robot/Likvido.Robot.csproj", "Likvido.Robot/"]
# END AUTO-GENERATED PROJECT REFERENCES
```

### Dockerfile (without markers)

The COPY block before `RUN dotnet restore` is automatically detected and replaced:

```dockerfile
COPY ["Directory.Build.props", "./"]
COPY ["Likvido.Core/Likvido.Core.csproj", "Likvido.Core/"]
COPY ["Likvido.Domain/Likvido.Domain.csproj", "Likvido.Domain/"]
COPY ["Likvido.Robot/Likvido.Robot.csproj", "Likvido.Robot/"]
RUN dotnet restore "Likvido.Robot/Likvido.Robot.csproj"
```

### Workflow Paths

```yaml
paths:
  - "src/Likvido.Core/**"
  - "src/Likvido.Domain/**"
  - "src/Likvido.Infrastructure/**"
  - "src/Likvido.Robot/**"
  - ".github/workflows/robot.yml"
```

## Troubleshooting

### Error: .NET SDK not installed or version < 10

Ensure you have the `actions/setup-dotnet@v4` step with .NET 10 before running this action:

```yaml
- name: Setup .NET
  uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '10.0'
```

### Warning: Could not find 'RUN dotnet restore'

The automatic Dockerfile detection looks for `RUN dotnet restore` to find the COPY block. If your Dockerfile uses a different pattern, add explicit markers.

### Warning: Could not find 'paths:' sections

The automatic workflow detection looks for `paths:` under `push:`, `pull_request:` or `pull_request_target:`. If your workflow doesn't have path filters, add explicit markers or add a `paths:` section.

### A workflow is matched to the wrong project

A workflow that sets `DOCKER_WORKING_DIRECTORY` and `DOCKERFILE_RELATIVE_PATH` (or the `docker-working-directory` and `dockerfile-relative-path` inputs) is updated only for the project that Dockerfile builds. A workflow whose Dockerfile builds no project in the solution, such as a frontend image, can still be matched by the fallback. Set both on any workflow that builds an image. Without them the action falls back to finding the project's path or name in the workflow, ignoring comment lines.

### Warning: Legacy .NET Framework project detected

The action supports modern .NET (5.0+) projects only. Legacy .NET Framework projects will be skipped with a warning. Consider migrating to modern .NET.

### Error: Circular dependency detected

Your project graph contains a circular reference. The error message will show the cycle path. Review your project references and remove the cycle.

### Error: No .sln or .slnx files found

Ensure your repository contains at least one `.sln or .slnx` file. The action uses solution files to discover projects.

### No deployable projects found

A project is deployable when its own directory has a `Dockerfile`, or when a `Dockerfile` in a parent directory runs `dotnet publish` on the project's `.csproj` in its final stage, or in a stage the final stage is built `FROM` or copies `--from`. A publish in any other stage, such as a test stage, does not count, because the deployment pipeline builds the final stage. Test projects are never deployable.

### Changes not detected

If using `base-ref`, ensure:
1. The workflow has `fetch-depth: 0` in the checkout step
2. The base ref exists and is accessible

## Architecture

The action uses:
- **.NET 10 file-based scripting** - No project file needed, self-contained script
- **Direct XML parsing** - Parses `.sln or .slnx` and `.csproj` files directly for dependency analysis
- **Zero external dependencies** - No NuGet packages required, uses only .NET BCL
- **Hybrid detection** - Supports both explicit markers and automatic pattern detection
- **Smart Docker context detection** - Automatically detects the Docker build context from existing COPY statements to generate correct relative paths

## License

MIT

## Contributing

Contributions are welcome! Please open an issue or pull request on GitHub.
