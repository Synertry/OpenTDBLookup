# Security Policy

## Supported Versions

OpenTDBLookup ships from the `release` branch. Only the latest released version is supported. There is no LTS, no backport policy, no extended-support tier.

| Version | Supported          |
| ------- | ------------------ |
| latest  | Yes                |
| older   | No (please upgrade) |

## Reporting a Vulnerability

If you find a security issue in OpenTDBLookup, **please do not open a public GitHub issue.** Use GitHub's Private Vulnerability Reporting so the issue can be patched before disclosure.

- **GitHub Private Vulnerability Reporting:** open https://github.com/Synertry/OpenTDBLookup/security/advisories/new and file a draft advisory. This is private; only repo maintainers can see it, and GitHub handles the full lifecycle (acknowledgment, CVE assignment, public disclosure timing).

Please include:

- A short description of the issue.
- Steps to reproduce (or a proof-of-concept).
- The OpenTDBLookup version (visible in the app footer, or via `OpenTDBLookup.exe --version`).
- Any relevant log excerpt from `logs/opentdb-YYYY-MM-DD.log`.

I aim to acknowledge within **72 hours** and to ship a fix within **14 days** for high-severity issues. Low-severity issues may be batched into a regular release.

## Scope

In scope:

- The OpenTDBLookup desktop application binary and its supporting scripts (`scripts/Sign-Binary.ps1`).
- The release pipeline (`.github/workflows/*.yaml`) - tampering opportunities, secret exposure, etc.
- The cached question store (`questions.json`) - injection or path-traversal issues.

Out of scope:

- The Open Trivia DB API itself (https://opentdb.com). Report those to OpenTDB directly.
- Issues that require physical or local-admin access to the machine running OpenTDBLookup.
- Issues in third-party dependencies that do not have a known impact on this app's attack surface (file a Dependabot upgrade instead).
- Social-engineering / phishing attacks unrelated to the app's behavior.

## Binary Integrity

Public release binaries are currently **unsigned** (the project does not yet hold a public code-signing certificate). Windows SmartScreen will warn about an unrecognized publisher on first run.

Integrity is gated by a **Sigstore-backed build provenance attestation** published with every release. The attestation cryptographically ties each binary to the GitHub Actions workflow run, source commit, and build environment that produced it - and lives on the public Sigstore transparency log, so a tampered release asset cannot fake a valid attestation. Verify before running:

```pwsh
gh attestation verify .\OpenTDBLookup-vX.Y.Z-win-x64.exe --owner Synertry
```

If verification fails, **do not run the binary** and please report it via the channels above.

A `SHA256SUMS.txt` file is also published alongside the binaries for convenience (CI logs, mirrors, quick eyeballing), but it is shipped from the same source as the binaries themselves and is **not** an independent integrity control - always prefer the attestation verification above.
