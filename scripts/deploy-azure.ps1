<#
.SYNOPSIS
    CareRoute AI / 見守り隊 を Azure App Service へデプロイします。

.DESCRIPTION
    Release ビルド -> publish -> zip -> az webapp deploy の順に実行します。
    シークレットは一切埋め込みません。Azure SQL への接続は
    App Service のシステム割り当てマネージドID + Microsoft Entra 認証
    (接続文字列に Authentication=Active Directory Default) を使うため、
    パスワードもキーも保存しません。

.EXAMPLE
    pwsh ./scripts/deploy-azure.ps1
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'rg-mimamoritai-hackathon',
    [string]$WebAppName    = 'app-mimamoritai-hack',
    [string]$Project       = 'src/MimamoriTai.Web'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $publishDir = Join-Path ([System.IO.Path]::GetTempPath()) "mimamoritai-publish"
    $zipPath    = Join-Path ([System.IO.Path]::GetTempPath()) "mimamoritai.zip"

    Write-Host '==> Release publish' -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish $Project -c Release -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish に失敗しました。" }

    # ローカルデモ用の SQLite ファイルが混入するとサイズが無駄なので除外する。
    Get-ChildItem $publishDir -Filter '*.db' -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Host '==> Zip' -ForegroundColor Cyan
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

    Write-Host "==> Deploy to $WebAppName" -ForegroundColor Cyan
    az webapp deploy --resource-group $ResourceGroup --name $WebAppName `
        --src-path $zipPath --type zip --async false | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "az webapp deploy に失敗しました。" }

    $host_ = az webapp show -g $ResourceGroup -n $WebAppName --query defaultHostName -o tsv
    Write-Host "==> Done: https://$host_" -ForegroundColor Green
    Write-Host "    LINE webhook URL: https://$host_/webhooks/line" -ForegroundColor Green
}
finally {
    Pop-Location
}
