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
- The OpenTDBLookup version (visible in the app footer or via the `--version` flag once it lands).
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

## Code Signing

Releases are signed with a developer code-signing certificate. SHA-256 checksums are published alongside each release (`SHA256SUMS.txt`). Verify the binary you downloaded matches the published checksum before running it:

```pwsh
# Get-FileHash returns the hash in uppercase; SHA256SUMS.txt stores it in
# lowercase. Compare case-insensitively, or lowercase the output first.
(Get-FileHash .\OpenTDBLookup-vX.Y.Z-win-x64.exe -Algorithm SHA256).Hash.ToLower()
```

If a checksum does not match, **do not run the binary** and please report it via the channels above.
