<#
.SYNOPSIS
    LINE Messaging API (Bot) の資格情報を User Secrets に投入します。

.DESCRIPTION
    値をコマンドライン引数に書くと PowerShell の履歴に残るため、既定では
    対話プロンプトで入力を受け取ります。入力値は画面に表示されません。

    投入先は src/MimamoriTai.Web の UserSecretsId で、main / 各ワークツリーで共有されます。

.EXAMPLE
    pwsh ./scripts/set-line-secrets.ps1
#>
[CmdletBinding()]
param(
    # 「Messaging API設定」タブの長いトークン
    [string] $ChannelAccessToken,
    # 「チャネル基本設定」タブの32文字のシークレット
    [string] $ChannelSecret,
    # 通知先の userId / groupId (任意)
    [string] $AlertToId,
    # 未指定なら Enabled=true を設定
    [switch] $Disable
)

$ErrorActionPreference = 'Stop'

$webProject = Join-Path $PSScriptRoot '..\src\MimamoriTai.Web' | Resolve-Path

function Read-Secret([string] $Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

if (-not $ChannelAccessToken) {
    $ChannelAccessToken = Read-Secret 'LINE Channel access token (Messaging API設定タブ)'
}
if (-not $ChannelSecret) {
    $ChannelSecret = Read-Secret 'LINE Channel secret (チャネル基本設定タブ)'
}

if ([string]::IsNullOrWhiteSpace($ChannelAccessToken) -or [string]::IsNullOrWhiteSpace($ChannelSecret)) {
    throw 'ChannelAccessToken と ChannelSecret は必須です。'
}

# LINE の channel secret は 32 桁の16進。取り違え (access token を貼る等) を早期に弾く。
if ($ChannelSecret.Length -ne 32) {
    Write-Warning "Channel secret が 32 文字ではありません (実際: $($ChannelSecret.Length))。チャネル基本設定タブの値か確認してください。"
}
if ($ChannelAccessToken.Length -lt 100) {
    Write-Warning "Channel access token が短すぎます (実際: $($ChannelAccessToken.Length))。Messaging API設定タブの長期トークンか確認してください。"
}

Push-Location $webProject
try {
    dotnet user-secrets set 'Line:ChannelAccessToken' $ChannelAccessToken | Out-Null
    dotnet user-secrets set 'Line:ChannelSecret'      $ChannelSecret      | Out-Null
    dotnet user-secrets set 'Line:Enabled'            (-not $Disable ? 'true' : 'false') | Out-Null
    if ($AlertToId) {
        dotnet user-secrets set 'Line:AlertToId' $AlertToId | Out-Null
    }

    Write-Host '投入しました:' -ForegroundColor Green
    dotnet user-secrets list |
        Where-Object { $_ -like 'Line:*' } |
        ForEach-Object {
            $k, $v = $_ -split '\s*=\s*', 2
            if ($k -eq 'Line:Enabled' -or $k -eq 'Line:AlertToId') { "  $k = $v" }
            else { "  $k = [$($v.Length) chars]" }
        }
}
finally {
    Pop-Location
}
