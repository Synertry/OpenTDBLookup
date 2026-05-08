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

1. Download `OpenTDBLookup.exe` from the [Releases](../../releases) page.
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
dotnet publish OpenTDBLookup -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o ./publish
```

The published binary is `./publish/OpenTDBLookup.exe`.

## Code signing

The repo ships a single signing script at `scripts/Sign-Binary.ps1` with two modes.

### Local (developer) mode

Place a code-signing cert in your user cert store, then:

```powershell
$env:OPENTDB_CODESIGN_THUMBPRINT = '<sha1-thumbprint>'
pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/OpenTDBLookup.exe
```

### CI (release pipeline) mode

The `Release` workflow reads two secrets:

- `CODESIGN_PFX_B64` - base64-encoded PFX byte stream
- `CODESIGN_PASSWORD` - plain-text PFX password

Both are mapped to `OPENTDB_CODESIGN_PFX_B64` / `OPENTDB_CODESIGN_PASSWORD` and consumed by the same script. The PFX is materialized to a temp file and securely deleted in `finally`.

## Tray icon

`OpenTDBLookup/Assets/tray-icon.ico` is a placeholder magenta square (`#D23CF2`) at 16x16 + 32x32. Replace it with your own multi-resolution `.ico` to brand the app.

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
