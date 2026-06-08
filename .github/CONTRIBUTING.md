# Contributing to OpenTDBLookup

Thanks for taking an interest. This is a small personal desktop app, but contributions are welcome - bug reports, fixes, and well-scoped features.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Development Workflow](#development-workflow)
    - [Prerequisites](#prerequisites)
    - [Branching Strategy](#branching-strategy)
    - [Coding Standards](#coding-standards)
    - [Testing](#testing)
    - [Documentation](#documentation)
- [Pull Request Process](#pull-request-process)
- [Issue Reporting](#issue-reporting)
- [Communication](#communication)

## Code of Conduct

This project and everyone participating in it is governed by our [Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code.

## Development Workflow

Always develop against the latest stable .NET SDK (currently .NET 10): [Latest .NET](https://dotnet.microsoft.com/download). The project targets `net10.0` and uses Avalonia 12.

### Prerequisites

- .NET 10 SDK
- Windows 10 / 11 (the app publishes `win-x64` only; tests run cross-platform)
- PowerShell 7+ (for the signing script and dev workflow scripts)
- A code-signing certificate if you intend to ship a signed binary locally; see [README - Code signing](../README.md#code-signing).

### Branching Strategy

- `main` is the integration branch. Every feature/fix lands here first via PR.
- `release` is the publishable line. Pushes to `release` trigger the [Release workflow](workflows/release.yaml) which auto-tags the next semver and ships signed binaries.
- The hop from `main` to `release` is automated: when CI passes on `main`, the [Integrate step](workflows/ci.yaml) opens an `auto-pr`. After approval, [review-approval.yaml](workflows/review-approval.yaml) fast-forwards the release branch (no merge commit, no rebase) so the exact reviewed SHA ships.
- Create feature branches from `main`:
  ```bash
  git checkout -b feat/your-feature-name
  # or
  git checkout -b fix/issue-you-are-fixing
  ```
  Branch prefixes follow [Conventional Commits](https://www.conventionalcommits.org/) and drive auto-labeling in [labels.yaml](labels.yaml).

### Coding Standards

The repo enforces these via `.editorconfig`, `Directory.Build.props`, and the lint workflow. Most fixes are one `dotnet format` away.

- **File-scoped namespaces.** `namespace OpenTDBLookup.Services;` not the block form.
- **`using` directives outside the namespace** (the codebase predates the file-scoped switch in places; new files should match).
- **Always use braces** even for one-liners.
- **`var` when the type is apparent**, explicit type for built-ins.
- **Nullable reference types are on** repo-wide. `CS8618` (uninitialized non-null) and `CS8625` (null-literal to non-null) are warnings; fix the root cause rather than `!`-asserting.
- **Immutability first** for data classes. `record` for DTOs; reach for mutation only in ViewModels / state holders.
- **Use the centralized `Directory.Build.props`.** Don't re-declare `LangVersion`, `Nullable`, or `Version` in individual `.csproj` files.

### Testing

- All new behavior should be accompanied by tests in `OpenTDBLookup.Tests/`.
- Test framework: **xUnit** + **FluentAssertions** + **RichardSzalay.MockHttp** for HTTP.
- Snake-case test names are the house style (`Merge_keeps_same_text_under_different_category_or_difficulty`); xUnit doesn't enforce a convention and the existing tests are consistent.
- Run locally:
  ```pwsh
  dotnet test
  ```
  CI runs with coverage: `dotnet test --collect:"XPlat Code Coverage"`.

### Documentation

- Public types in the `OpenTDBLookup` app project: XML doc comments are encouraged. `CS1591` (missing doc on public member) is suppressed so partial coverage is fine.
- The `README.md` is the user-facing source of truth. If you change user-visible behavior, update it in the same PR.

## Pull Request Process

1. Update your fork to include the latest changes from upstream:
   ```bash
   git fetch origin
   git rebase origin/main
   ```

2. Ensure your code passes `dotnet format`, `dotnet build`, and `dotnet test` locally before pushing.

3. Submit a pull request with a clear title and description:
    - What does this PR do?
    - Any specific issues or challenges to note?
    - References to related issues or discussions

4. The [PR Test workflow](workflows/pr-test.yaml) runs lint + tests on every push to the PR. Once it passes, the `status/pr-test-passed` label is added.

5. Your PR will be reviewed; minor cleanups may be applied directly.

6. Once approved, the maintainer merges to `main`. The full CI workflow then runs, opens the `main -> release` integrate PR, and after a second review fast-forwards the release branch.

## Issue Reporting

- Use the issue tracker for bugs and feature proposals.
- The repository includes templates for bug reports and feature requests; please use them.
- For bugs, include:
    - A clear title and description
    - Steps to reproduce
    - Expected vs. actual behavior
    - Version information (app version, .NET SDK version, Windows version)
    - The relevant `logs/opentdb-YYYY-MM-DD.log` content (redact any personal data)

- For feature requests, include:
    - The problem you're trying to solve
    - Proposed solution or ideas
    - Any alternatives you've considered
    - Whether the request collides with the README's "Out of scope" list (auth, telemetry, auto-update, i18n, non-Windows targets)

## Communication

- For quick questions, open a [Discussion](https://github.com/Synertry/OpenTDBLookup/discussions).
- For bugs and feature requests, use [Issues](https://github.com/Synertry/OpenTDBLookup/issues).
- For significant changes, please open an issue first.

---

Thanks again for contributing.
