# OpenTDBLookup

> Paste a trivia question, get the correct answer instantly.

OpenTDBLookup is a small Windows desktop app that maintains a local cache of every verified question on [Open Trivia DB](https://opentdb.com) and looks up the correct answer the moment you paste a question. It was built because the ChillZone Discord trivia minigame uses OpenTDB questions verbatim and answering them by hand is suspenseful, slow, and frequently wrong.

## Features

- **Instant lookup.** Paste a question, see the correct answer with a 100ms-debounced search.
- **Local cache.** First launch performs a full scrape (a few minutes); subsequent launches load instantly.
- **Weekly auto-refresh.** Counts are checked at most every 7 days; only changed categories are re-fetched.
- **Optional clipboard watcher.** When enabled, pasting a recognized question into any other app auto-replaces the clipboard with the correct answer.
- **Tray icon.** Optional minimize-to-tray with quick toggle of the clipboard watcher.
- **Hotkeys.** `Ctrl+L` focus input, `Ctrl+Enter` copy answer, `Ctrl+R` refresh, `Esc` clear.
- **Dark theme by default.**

## Quick start

Two builds are published with each release:

- **`OpenTDBLookup-vX.Y.Z-win-x64.exe`** (slim, ~16 MB) - default. Requires the [.NET 10 runtime](https://dotnet.microsoft.com/download) on the target machine.
- **`OpenTDBLookup-vX.Y.Z-win-x64-selfcontained.exe`** (portable, ~50 MB) - bundles the runtime. Use this when you can't or won't install .NET 10 on the target machine.

Steps:

1. Download whichever `.exe` matches your situation from the [Releases](../../releases) page.
2. Drop it anywhere on disk and double-click to run. No installer required.
3. The first launch performs the initial scrape (~5-10 minutes; the OpenTDB API enforces a 5-second gap between requests).
4. Once the dialog closes, paste a question into the input box. The correct answer appears below.

The cache lives in `questions.json` next to the executable. Logs land in `logs/opentdb-*.log`.

## OpenTDB API reference

OpenTDBLookup talks to the public Open Trivia DB API:

| Purpose | URL |
|---|---|
| List categories | `https://opentdb.com/api_category.php` |
| Verified counts per category | `https://opentdb.com/api_count_global.php` |
| Per-category-difficulty count | `https://opentdb.com/api_count.php?category=X` |
| Fetch questions | `https://opentdb.com/api.php?amount=N&category=X&difficulty=Y&token=T&encode=base64` |
| Request session token | `https://opentdb.com/api_token.php?command=request` |
| Reset session token | `https://opentdb.com/api_token.php?command=reset&token=T` |

Response codes: `0` success, `1` no results, `2` invalid parameter, `3` token not found, `4` token empty (re-request token), `5` rate limited (one IP per 5 seconds).

OpenTDBLookup always passes `encode=base64` and decodes locally to avoid HTML-entity decode bugs. The API only returns verified questions.

## Build from source

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows 10 / 11 (Avalonia targets `win-x64`)
- PowerShell 7+ (for the signing script)

```powershell
git clone <repo-url>
cd OpenTDBLookup
dotnet restore
dotnet build
dotnet test
```

Two publish profiles are supported.

**Slim** - framework-dependent, requires .NET 10 on the target. Smallest binary, fastest startup. Use this for personal use and CI builds where you control the runtime:

```powershell
dotnet publish OpenTDBLookup -c Release -r win-x64 --no-self-contained `
  -p:PublishSingleFile=true `
  -o ./publish/slim
```

**Portable** - self-contained, bundles the .NET runtime. Larger binary; runs anywhere x64 Windows runs. Use this when you ship to machines you don't control:

```powershell
dotnet publish OpenTDBLookup -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o ./publish/portable
```

`PublishReadyToRun` is intentionally not used - it adds ~40 MB for ~200 ms of cold-start savings, which is the wrong trade for a desktop tool you click once per session.

## Code signing

> **Note:** Public releases are currently **unsigned** because the project does not yet hold a public code-signing certificate. Windows SmartScreen will show an "unrecognized publisher" warning the first time you run a release binary. Verify integrity via the published `SHA256SUMS.txt` (see [Security](.github/SECURITY.md)) until a real cert lands.

The repo ships a single signing script at `scripts/Sign-Binary.ps1` with two modes. The CI release pipeline already integrates with it; the moment the `CODESIGN_PFX_B64` + `CODESIGN_PASSWORD` repo secrets are configured, signing turns on with no code change.

### Local (developer) mode

Place a code-signing cert in your user cert store. Three ways to point the script at it, in order of convenience:

```powershell
# 1. Pass the thumbprint inline.
pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/slim/OpenTDBLookup.exe `
  -Thumbprint <sha1-thumbprint>

# 2. .env at the repo root (gitignored). Copy .env.example to .env and edit.
#    The script auto-loads it on every invocation.
pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/slim/OpenTDBLookup.exe

# 3. Persistent user-scope env var.
[Environment]::SetEnvironmentVariable('OPENTDB_CODESIGN_THUMBPRINT', '<sha1-thumbprint>', 'User')
pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/slim/OpenTDBLookup.exe
```

If you already have your own personal signing wrapper (e.g. a `Sign-File`
alias around `signtool`), feel free to use that directly - the included
script is just a default for people who don't.

### CI (release pipeline) mode

When the `CODESIGN_PFX_B64` + `CODESIGN_PASSWORD` repo secrets exist, the `Release` workflow signs both built binaries before attaching them to the GitHub Release. When the secrets are absent (current default), the sign steps are skipped and the workflow publishes unsigned binaries plus `SHA256SUMS.txt`.

Secret contract:

- `CODESIGN_PFX_B64` - base64-encoded PFX byte stream
- `CODESIGN_PASSWORD` - PFX password

Both are mapped to `OPENTDB_CODESIGN_PFX_B64` / `OPENTDB_CODESIGN_PASSWORD` and consumed by the same script. The PFX is imported into the runner's cert store, signed by thumbprint, then removed; the temp file and cert are cleaned up in `finally`.

## App icon

`OpenTDBLookup/Assets/logo.ico` is used both as the executable icon and the tray icon. Replace it with your own multi-resolution `.ico` to rebrand.

## Troubleshooting

- **`questions.json` corrupt?** Delete it and relaunch; the app will perform a fresh scrape.
- **Clipboard watcher feels slow.** Polling is intentionally throttled to 250 ms to avoid hammering the OS clipboard.
- **`4 errors` (or similar) in the log during scrape.** OpenTDB's session-token endpoint is occasionally flaky; the app retries automatically.
- **Logs.** `logs/opentdb-YYYY-MM-DD.log`, daily rolling, 7 days retained.

## Out of scope

This is intentionally a small app. The following are **not** implemented and will be politely declined as feature requests:

- Authentication / login
- Telemetry / analytics
- Auto-update
- Internationalization
- Mobile / Linux / macOS publish targets

## License

MIT - see [LICENSE](./LICENSE).

## Acknowledgements

- [Open Trivia DB](https://opentdb.com) - the data source. Thanks to the community for verifying questions.
- [Avalonia](https://avaloniaui.net) and [FluentAvalonia](https://github.com/amwx/FluentAvalonia) for the UI stack.
