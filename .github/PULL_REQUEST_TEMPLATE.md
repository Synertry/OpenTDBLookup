<!--
Thanks for contributing! A few notes:

- Title: use conventional-commits (feat:, fix:, refactor:, docs:, chore:, ci:, perf:, test:, build:).
- Branch: prefix matches the type (feat/..., fix/..., etc.) for auto-labeling.
- CI: pr-test.yaml runs lint + tests on PRs to main. The full CI matrix runs after merge.
- Format locally: `dotnet format` before pushing.
-->

## Summary

<!-- What does this PR do, in one or two sentences? -->

## Why

<!-- Motivation. Link the issue if one exists: "Fixes #123", "Refs #456". -->

## Changes

<!-- Bulleted list of the meaningful changes. Skip the boilerplate. -->

-
-

## Test plan

<!-- Concrete steps you (or a reviewer) can run to verify. -->

- [ ] `dotnet test` passes locally
- [ ] `dotnet format --verify-no-changes --severity error` is clean
- [ ] Manual smoke test in the app (if user-visible behavior changed)

## Out-of-scope / follow-ups

<!-- Anything intentionally deferred. -->
