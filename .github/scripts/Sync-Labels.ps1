<#
.SYNOPSIS
    Creates or updates GitHub labels with consistent colors and descriptions.

.DESCRIPTION
    Descriptions stay aligned with the comments in `.github/labels.yaml`. Run
    after adding or renaming a label so the repo's labels match the workflows
    that depend on them (pr-labeler, pr-test, pre-release-review,
    review-approval, stale-triage, etc.).

    Ported from cf-discord-relay's `.github/scripts/sync-labels.ts` (Bun TS)
    to PowerShell to fit OpenTDBLookup's pwsh-first toolchain (no Bun on PATH
    required, runs on the same shell used for `Sign-Binary.ps1`).

.PARAMETER Repo
    Target repo as "OWNER/REPO". Defaults to whatever `gh repo view` resolves
    in the current working directory.

.EXAMPLE
    pwsh .github/scripts/Sync-Labels.ps1
    Sync labels to the current repo.

.EXAMPLE
    pwsh .github/scripts/Sync-Labels.ps1 -Repo Synertry/OpenTDBLookup
    Sync to an explicit repo (useful when invoking from outside the working tree).

.NOTES
    Requires: gh CLI on PATH, authenticated with `repo` scope.
#>
[CmdletBinding()]
param(
    [string] $Repo
)

$ErrorActionPreference = 'Stop'

# Label inventory. Keep in sync with `.github/labels.yaml` AND keep the colors
# visually distinct within each group (area = warm/cool mix, type = follows
# Conventional Commits coloring, size = green-to-red gradient, status = state
# semantics, workflow = bot lavender).
$labels = @(
    # --- Area labels ---
    @{ Name = 'area/config';   Color = 'e4e669'; Description = 'Root configuration files (.editorconfig, .gitignore, Directory.Build.props)' }
    @{ Name = 'area/IDE';      Color = 'cccccc'; Description = 'IDE and editor configuration (.vscode, .idea, .vs)' }
    @{ Name = 'area/CI';       Color = '5319e7'; Description = 'GitHub workflows, Dependabot, labelers, scripts' }
    @{ Name = 'area/deps';     Color = '006b75'; Description = 'NuGet dependency manifests (csproj, packages.lock.json)' }
    @{ Name = 'area/source';   Color = '0e8a16'; Description = 'C# source code (non-test)' }
    @{ Name = 'area/ui';       Color = '6f42c1'; Description = 'Avalonia XAML markup and code-behind' }
    @{ Name = 'area/tests';    Color = '1d76db'; Description = 'Tests under OpenTDBLookup.Tests/' }
    @{ Name = 'area/scripts';  Color = '8a4d2c'; Description = 'PowerShell scripts under scripts/' }
    @{ Name = 'area/docs';     Color = 'c5def5'; Description = 'Documentation (README, CONTRIBUTING, CODE_OF_CONDUCT, SECURITY)' }
    @{ Name = 'area/license';  Color = 'fbca04'; Description = 'License file' }

    # --- Type labels (Conventional Commits) ---
    @{ Name = 'type/build';    Color = '0075ca'; Description = 'Changes that affect the build system or external dependencies' }
    @{ Name = 'type/chore';    Color = 'ededed'; Description = "Maintenance tasks that don't modify source or test files" }
    @{ Name = 'type/ci';       Color = '5319e7'; Description = 'Changes to CI configuration files and scripts' }
    @{ Name = 'type/docs';     Color = 'c5def5'; Description = 'Documentation-only changes' }
    @{ Name = 'type/feat';     Color = 'a2eeef'; Description = 'A new feature' }
    @{ Name = 'type/fix';      Color = 'd73a4a'; Description = 'A bug fix' }
    @{ Name = 'type/perf';     Color = 'f9d0c4'; Description = 'A code change that improves performance' }
    @{ Name = 'type/refactor'; Color = 'd4c5f9'; Description = 'A code change that neither fixes a bug nor adds a feature' }
    @{ Name = 'type/revert';   Color = 'b60205'; Description = 'Reverts a previous commit' }
    @{ Name = 'type/style';    Color = 'cfd3d7'; Description = 'Whitespace and formatting changes that do not alter semantics' }
    @{ Name = 'type/test';     Color = '1d76db'; Description = 'Adding missing tests or correcting existing tests' }

    # --- Size labels (green-to-red gradient) ---
    @{ Name = 'size/xs';       Color = '4caf50'; Description = 'Extra small diff (< 25 lines, 1 file)' }
    @{ Name = 'size/s';        Color = '8bc34a'; Description = 'Small diff (< 150 lines, 10 files)' }
    @{ Name = 'size/m';        Color = 'ffeb3b'; Description = 'Medium diff (< 600 lines, 25 files)' }
    @{ Name = 'size/l';        Color = 'ff9800'; Description = 'Large diff (< 2500 lines, 50 files)' }
    @{ Name = 'size/xl';       Color = 'f44336'; Description = 'Extra large diff (>= 5000 lines or 100+ files)' }

    # --- Status labels ---
    @{ Name = 'status/triage';             Color = 'e4820b'; Description = 'Needs triage before action can be taken' }
    @{ Name = 'status/pr-test-passed';     Color = '0e8a16'; Description = 'Lightweight PR test workflow passed (granted by pr-test.yaml)' }
    @{ Name = 'status/review-needed';      Color = 'fbca04'; Description = 'Requires human review before merging' }
    @{ Name = 'status/approval-pending';   Color = 'fbca04'; Description = 'Waiting on approver before fast-forward to release' }
    @{ Name = 'status/approved';           Color = '0e8a16'; Description = 'Approved for release - triggers fast-forward push' }
    @{ Name = 'status/stale';              Color = 'aaaaaa'; Description = 'No activity for 30 days - scheduled for closure' }

    # --- Workflow labels ---
    @{ Name = 'workflow/dependabot'; Color = '7057ff'; Description = 'Automated dependency update from Dependabot' }
    @{ Name = 'workflow/auto-pr';    Color = '7057ff'; Description = 'Pull request opened by an automated workflow' }
)

function Resolve-Repo {
    [CmdletBinding()]
    param([string] $Explicit)

    if ($Explicit) { return $Explicit }

    # gh repo view returns the nameWithOwner of whichever repo `gh` resolved
    # from the current working directory. Falls through to the catch on any
    # non-zero exit so we surface a useful error instead of an empty string.
    try {
        $output = & gh repo view --json nameWithOwner -q '.nameWithOwner' 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "gh repo view failed (exit $LASTEXITCODE): $output"
        }
        $resolved = ($output | Out-String).Trim()
        if (-not $resolved) {
            throw "gh repo view returned an empty repo name; pass -Repo OWNER/REPO explicitly"
        }
        return $resolved
    }
    catch {
        throw "Failed to resolve repo: $($_.Exception.Message)"
    }
}

function Sync-Label {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Repo,
        [Parameter(Mandatory)] [hashtable] $Label
    )

    # --force = create-or-update. Same semantics as the Bun TS version.
    $output = & gh label create $Label.Name `
        --color $Label.Color `
        --description $Label.Description `
        --repo $Repo `
        --force 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "exit $LASTEXITCODE - $(($output | Out-String).Trim())"
    }
}

# --- main ---

$targetRepo = Resolve-Repo -Explicit $Repo
Write-Host "Syncing $($labels.Count) labels to $targetRepo..."

$synced = 0
$failed = 0

foreach ($label in $labels) {
    try {
        Sync-Label -Repo $targetRepo -Label $label
        $synced++
        Write-Host ("  ok  {0}" -f $label.Name)
    }
    catch {
        $failed++
        Write-Host ("  err {0}: {1}" -f $label.Name, $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Done. $synced synced, $failed failed."

if ($failed -gt 0) { exit 1 }
