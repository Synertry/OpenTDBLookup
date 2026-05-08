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
    cert store. Read from the OPENTDB_CODESIGN_THUMBPRINT environment variable
    by default.

.PARAMETER PfxBase64
    Base64-encoded PFX byte stream. Read from OPENTDB_CODESIGN_PFX_B64.

.PARAMETER PfxPassword
    Plain-text password for the PFX. Read from OPENTDB_CODESIGN_PASSWORD.

.EXAMPLE
    # Local mode
    $env:OPENTDB_CODESIGN_THUMBPRINT = '<thumbprint>'
    pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/OpenTDBLookup.exe

.EXAMPLE
    # CI mode (env vars set by workflow secrets)
    pwsh ./scripts/Sign-Binary.ps1 -Path ./publish/OpenTDBLookup.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Path,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $Thumbprint   = $env:OPENTDB_CODESIGN_THUMBPRINT,
    [string] $PfxBase64    = $env:OPENTDB_CODESIGN_PFX_B64,
    [string] $PfxPassword  = $env:OPENTDB_CODESIGN_PASSWORD
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) { throw "Binary not found: $Path" }

# Locate signtool.
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

$tempPfx = $null
try {
    if ($PfxBase64) {
        # CI mode: materialize PFX, sign, then nuke the file in finally.
        if (-not $PfxPassword) {
            throw 'OPENTDB_CODESIGN_PASSWORD must be set when using PFX base64.'
        }
        $tempPfx = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.pfx')
        [IO.File]::WriteAllBytes($tempPfx, [Convert]::FromBase64String($PfxBase64))

        & $signtool sign /v /fd SHA256 /td SHA256 /tr $TimestampUrl /f $tempPfx /p $PfxPassword $Path
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    }
    elseif ($Thumbprint) {
        # Local mode: certificate is in the user's cert store.
        & $signtool sign /v /fd SHA256 /td SHA256 /tr $TimestampUrl /sha1 $Thumbprint $Path
        if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }
    }
    else {
        throw 'No signing material configured. Set OPENTDB_CODESIGN_THUMBPRINT (local) or OPENTDB_CODESIGN_PFX_B64 + OPENTDB_CODESIGN_PASSWORD (CI).'
    }

    & $signtool verify /v /pa $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool verify failed with exit code $LASTEXITCODE" }
    Write-Host "Signed and verified: $Path" -ForegroundColor Green
}
finally {
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
    $env:OPENTDB_CODESIGN_PASSWORD = $null
}
