<#
.SYNOPSIS
    Azure App Service (app-mimamoritai-hack) のアプリ設定をローカルの User Secrets へ同期します。

.DESCRIPTION
    ローカル開発でも本番と同じ LINE / OrcaRouter / Fabric / Eventhouse の資格情報を使えるようにします。
    値は画面に表示せず、キー名と文字数だけを報告します。

    このスクリプトは 見守り隊 のリソースグループのみを参照します。
    他案件 (rg-fraudshield-tokyo-hackathon など) には一切アクセスしません。

.EXAMPLE
    pwsh ./scripts/sync-secrets-from-azure.ps1
    pwsh ./scripts/sync-secrets-from-azure.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Subscription  = '51420a57-c7be-4920-9e8a-5382f2bad58d',
    [string] $ResourceGroup = 'rg-mimamoritai-hackathon',
    [string] $WebAppName    = 'app-mimamoritai-hack',
    # ローカルでは実 DB / 実環境名を使わないため既定で除外する。
    [string[]] $Exclude     = @('ConnectionStrings__AppDb', 'ASPNETCORE_ENVIRONMENT')
)

$ErrorActionPreference = 'Stop'

if ($ResourceGroup -notlike '*mimamoritai*') {
    throw "このスクリプトは見守り隊専用です。指定されたリソースグループ '$ResourceGroup' は対象外です。"
}

$webProject = Join-Path $PSScriptRoot '..\src\MimamoriTai.Web' | Resolve-Path

Write-Host "取得元: $WebAppName ($ResourceGroup)" -ForegroundColor Cyan

$raw = az webapp config appsettings list `
    --resource-group $ResourceGroup `
    --name $WebAppName `
    --subscription $Subscription `
    --output json 2>$null

if (-not $raw) { throw 'アプリ設定を取得できませんでした。az login の状態を確認してください。' }

$settings = $raw | ConvertFrom-Json

Push-Location $webProject
try {
    $applied = [System.Collections.Generic.List[string]]::new()
    $skipped = [System.Collections.Generic.List[string]]::new()

    foreach ($s in $settings) {
        if ($Exclude -contains $s.name) { $skipped.Add("$($s.name) (除外指定)"); continue }

        $value = [string]$s.value
        if ([string]::IsNullOrWhiteSpace($value)) { $skipped.Add("$($s.name) (空)"); continue }

        # App Service は '__' 区切り、User Secrets は ':' 区切り。
        $key = $s.name -replace '__', ':'

        if ($PSCmdlet.ShouldProcess($key, 'dotnet user-secrets set')) {
            dotnet user-secrets set $key $value | Out-Null
        }
        $applied.Add(('{0,-34} {1,4} chars' -f $key, $value.Length))
    }

    Write-Host "`n投入 ($($applied.Count)件):" -ForegroundColor Green
    $applied | ForEach-Object { "  $_" }

    if ($skipped.Count -gt 0) {
        Write-Host "`nスキップ ($($skipped.Count)件):" -ForegroundColor DarkGray
        $skipped | ForEach-Object { "  $_" }
    }
}
finally {
    Pop-Location
}
