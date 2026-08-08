<#
.SYNOPSIS
    Scans the repository for accidentally committed secrets.

.DESCRIPTION
    Reports only the file and line number of a suspicious match.
    It NEVER prints the matched value itself, so running this script
    (including in CI logs) can never leak a secret.

.EXAMPLE
    pwsh ./scripts/check-secrets.ps1
#>

[CmdletBinding()]
param(
    [string]$Path = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

# name -> regex. Keep patterns specific enough to avoid drowning in false positives.
$patterns = [ordered]@{
    'LINE channel access token' = 'channelAccessToken"\s*:\s*"[A-Za-z0-9+/=]{40,}'
    'Generic long secret value' = '"(Secret|Token|ApiKey|Password|ClientSecret)"\s*:\s*"[^"]{16,}"'
    'SQL connection with password' = 'Password\s*=\s*[^;"\s]{6,}'
    'Azure storage key' = 'AccountKey\s*=\s*[A-Za-z0-9+/=]{40,}'
    'AWS access key id' = '\bAKIA[0-9A-Z]{16}\b'
    'Private key block' = '-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----'
    'Bearer token literal' = 'Bearer\s+[A-Za-z0-9\-\._~\+/]{30,}'
}

$excludedDirs = @('bin', 'obj', '.git', '.vs', 'node_modules', 'TestResults')
$excludedFiles = @('check-secrets.ps1')

Write-Host "Scanning: $Path" -ForegroundColor Cyan

$files = Get-ChildItem -Path $Path -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    $relative = $_.FullName.Substring($Path.Length).TrimStart('\', '/')
    $segments = $relative -split '[\\/]'
    ($excludedDirs | Where-Object { $segments -contains $_ }).Count -eq 0 -and
    $excludedFiles -notcontains $_.Name -and
    $_.Length -lt 2MB
}

$findings = @()

foreach ($file in $files) {
    $relative = $file.FullName.Substring($Path.Length).TrimStart('\', '/')

    foreach ($name in $patterns.Keys) {
        $matches = Select-String -Path $file.FullName -Pattern $patterns[$name] -AllMatches -ErrorAction SilentlyContinue
        foreach ($m in $matches) {
            $findings += [pscustomobject]@{
                Rule = $name
                File = $relative
                Line = $m.LineNumber
            }
        }
    }
}

# Tracked files that should never be committed at all.
$forbidden = @('.env', 'secrets.json', 'appsettings.Local.json')
foreach ($file in $files) {
    if ($forbidden -contains $file.Name) {
        $findings += [pscustomobject]@{
            Rule = 'Forbidden file present'
            File = $file.FullName.Substring($Path.Length).TrimStart('\', '/')
            Line = 0
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host "OK: no secret-like content found." -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "Potential secrets found ($($findings.Count)). Values are intentionally not displayed." -ForegroundColor Red
$findings | Sort-Object File, Line | Format-Table -AutoSize
Write-Host "Review each location manually, then move the value to user-secrets or an environment variable." -ForegroundColor Yellow
exit 1
