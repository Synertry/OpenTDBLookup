<#
.SYNOPSIS
    Code-signs a Windows binary using either a thumbprint from the local Cert
    Store (interactive / dev mode) or a base64-encoded PFX from environment
    variables (CI mode).

.PARAMETER Path
    Path to the binary to sign.

.PARAMETER TimestampUrl
    RFC3161 timestamp server URL. Defaults to DigiCert.

.PARAMETER Thumbprint
    SHA-1 thumbprint of a code-signing certificate present in the local user's
    cert store. Falls back to OPENTDB_CODESIGN_THUMBPRINT env var, which is
    in turn populated from .env if present.

.PARAMETER PfxBase64
    Base64-encoded PFX byte stream. Falls back to OPENTDB_CODESIGN_PFX_B64.

.PARAMETER PfxPassword
    Plain-text password for the PFX. Falls back to OPENTDB_CODESIGN_PASSWORD.

.PARAMETER EnvFile
    Optional explicit path to a .env file. Without this, the script looks at
    repo-root\.env and ${PWD}\.env in that order. Lines are KEY=VALUE; lines
    starting with # are comments. Existing environment variables are NOT
    overwritten (your shell wins over .env).

.EXAMPLE
    # Local mode with .env at the repo root
    # repo\.env contains: OPENTDB_CODESIGN_THUMBPRINT=757D7602DBB904AAAD87E82701738596DD8DB28A
    pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/slim/OpenTDBLookup.exe

.EXAMPLE
    # Local mode passing the thumbprint inline (no env, no .env needed)
    pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/slim/OpenTDBLookup.exe `
        -Thumbprint 757D7602DBB904AAAD87E82701738596DD8DB28A

.EXAMPLE
    # CI mode (env vars set by workflow secrets)
    pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/slim/OpenTDBLookup.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $Thumbprint,
    [string] $PfxBase64,
    [string] $PfxPassword,
    [string] $EnvFile
)

$ErrorActionPreference = 'Stop'

# --- .env loader ---------------------------------------------------------
# Lets a personal thumbprint or PFX live in a gitignored repo-root file
# instead of forcing the user to set persistent user-scope env vars. Existing
# process env vars take precedence so a CI override or a one-off shell
# variable still wins.
$envCandidates = @()
if ($EnvFile) { $envCandidates += $EnvFile }
$envCandidates += (Join-Path (Split-Path $PSScriptRoot -Parent) '.env')
$envCandidates += (Join-Path (Get-Location).Path '.env')
foreach ($candidate in ($envCandidates | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $candidate)) { continue }
    Write-Verbose "Loading env vars from $candidate"
    Get-Content -LiteralPath $candidate | ForEach-Object {
        $line = $_
        if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { return }
        if ($line -match '^\s*(export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
            $name  = $matches[2]
            $value = $matches[3]
            if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
                $value = $value.Substring(1, $value.Length - 2)
            } elseif ($value.Length -ge 2 -and $value.StartsWith("'") -and $value.EndsWith("'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            if (-not [Environment]::GetEnvironmentVariable($name, 'Process')) {
                [Environment]::SetEnvironmentVariable($name, $value, 'Process')
            }
        }
    }
    break  # only load the first candidate that exists
}

# Apply parameter -> env -> null precedence after .env has had a chance to
# populate the environment.
if (-not $Thumbprint)  { $Thumbprint  = $env:OPENTDB_CODESIGN_THUMBPRINT }
if (-not $PfxBase64)   { $PfxBase64   = $env:OPENTDB_CODESIGN_PFX_B64 }
if (-not $PfxPassword) { $PfxPassword = $env:OPENTDB_CODESIGN_PASSWORD }

# --- Locate signtool -----------------------------------------------------
if (-not (Test-Path $Path)) { throw "Binary not found: $Path" }

$signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source
if (-not $signtool) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    foreach ($root in $candidates) {
        $found = Get-ChildItem -Path $root -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
                 Where-Object FullName -Like '*\x64\signtool.exe' |
                 Sort-Object FullName -Descending |
                 Select-Object -First 1
        if ($found) { $signtool = $found.FullName; break }
    }
}
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK or add it to PATH.' }

# --- Sign + verify -------------------------------------------------------
$tempPfx = $null
$importedCertThumbprint = $null
try {
    if ($PfxBase64) {
        # CI mode: materialize PFX, import into the runner's CurrentUser cert
        # store, sign by thumbprint, then remove both the file and the cert.
        #
        # Why not signtool /f /p: that pattern puts the PFX password on the
        # signtool process command line, which is visible to other processes
        # under the same user (Task Manager, Sysmon, ETW traces). Importing
        # the cert means the password only crosses Import-PfxCertificate's
        # SecureString boundary and never appears on a command line.
        if (-not $PfxPassword) {
            throw 'OPENTDB_CODESIGN_PASSWORD must be set when using PFX base64.'
        }
        $tempPfx = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.pfx')
        [IO.File]::WriteAllBytes($tempPfx, [Convert]::FromBase64String($PfxBase64))

        $securePass = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
        $imported = Import-PfxCertificate -FilePath $tempPfx -CertStoreLocation 'Cert:\CurrentUser\My' -Password $securePass
        $importedCertThumbprint = $imported.Thumbprint

        & $signtool sign /v /fd SHA256 /td SHA256 /tr $TimestampUrl /sm /sha1 $importedCertThumbprint $Path
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    }
    elseif ($Thumbprint) {
        # Local mode: certificate is already in the user's cert store.
        & $signtool sign /v /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $Thumbprint $Path
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    }
    else {
        throw 'No signing material configured. Pass -Thumbprint, set OPENTDB_CODESIGN_THUMBPRINT (env or .env), or supply OPENTDB_CODESIGN_PFX_B64 + OPENTDB_CODESIGN_PASSWORD for CI.'
    }

    & $signtool verify /v /pa $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool verify failed with exit code $LASTEXITCODE" }
    Write-Host "Signed and verified: $Path" -ForegroundColor Green
}
finally {
    if ($importedCertThumbprint) {
        try {
            Remove-Item -Path "Cert:\CurrentUser\My\$importedCertThumbprint" -Force -ErrorAction SilentlyContinue
        } catch {
            Write-Warning "Failed to remove temp cert from store: $_"
        }
    }
    if ($tempPfx -and (Test-Path $tempPfx)) {
        try {
            Remove-Item $tempPfx -Force
        } catch {
            Write-Warning "Failed to delete temp PFX at ${tempPfx}: $_"
        }
    }
    # Clear both the script parameter and the environment variable so a later
    # 'env' dump, child process, or hung shell session does not leak the
    # password. Remove-Variable only clears the local script binding.
    Remove-Variable -Name 'PfxPassword' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'securePass' -ErrorAction SilentlyContinue
    $env:OPENTDB_CODESIGN_PASSWORD = $null
}
