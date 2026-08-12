<#
.SYNOPSIS
  検証用 VM に RDP でつなげなくなったとき、まずこれを実行する。

.DESCRIPTION
  Android エミュレータと Blender を動かしている検証用 VM
  `azvmqxn3butq5dkgu` は、NSG `aznsgqxn3butq5dkgu` の規則
  `AllowRdpFromMyIp`（優先度 300 / TCP 3389）で
  「今つないでいる端末の IP からだけ」RDP を許可している。

  この端末のグローバル IP は固定ではない。回線が切り替わったり
  再接続したりすると変わるので、そのたびに RDP が
  「接続できません」で止まる。原因は VM 側ではなく、
  許可されている IP が古いこと。

  このスクリプトは現在のグローバル IP を調べ、規則と食い違って
  いたときだけ許可 IP を書き換える。

  なぜ 0.0.0.0/0 にしないのか:
    RDP を全世界に開けると、数分で総当たりログイン試行が始まる。
    このVMは検証用とはいえ、開発中の資材が入っている。
    IP を絞る運用のほうが、面倒でも安全側に倒れる。

  複数の場所から使うなら -AdditionalIp で足せる。
  例) 自宅と会社の両方から使う場合。

  注意: この NSG は検証用 VM 専用。
  共有基盤のネットワーク規則は、ここからは触らないこと。

.EXAMPLE
  pwsh ./scripts/update-vm-rdp-ip.ps1
  pwsh ./scripts/update-vm-rdp-ip.ps1 -CheckOnly
  pwsh ./scripts/update-vm-rdp-ip.ps1 -AdditionalIp '203.0.113.10'
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = 'RG-BLENDER-CODEX',
    [string]$NsgName = 'aznsgqxn3butq5dkgu',
    [string]$RuleName = 'AllowRdpFromMyIp',
    [string[]]$AdditionalIp = @(),
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# az が 32bit Python の警告を出すことがあるので落とす
function Invoke-Az {
    param([string[]]$Arguments)
    $out = & az @Arguments 2>&1 | Where-Object { $_ -notmatch 'UserWarning|warnings\.warn' }
    if ($LASTEXITCODE -ne 0) {
        throw "az $($Arguments -join ' ') が失敗しました: $out"
    }
    return $out
}

Write-Step '現在のグローバル IP を調べています'
$myIp = $null
foreach ($endpoint in @('https://api.ipify.org?format=json', 'https://ifconfig.me/all.json')) {
    try {
        $resp = Invoke-RestMethod -Uri $endpoint -TimeoutSec 15
        $myIp = if ($resp.ip) { $resp.ip } else { $resp.ip_addr }
        if ($myIp) { break }
    } catch {
        Write-Verbose "$endpoint から取得できませんでした: $_"
    }
}

if (-not $myIp) {
    throw 'グローバル IP を取得できませんでした。ネットワークを確認してください。'
}

Write-Host "  この端末の IP: $myIp"

Write-Step "NSG 規則 $RuleName の現状を読み取ります"
$ruleJson = (Invoke-Az @('network', 'nsg', 'rule', 'show',
        '-g', $ResourceGroup, '--nsg-name', $NsgName, '-n', $RuleName, '-o', 'json')) -join "`n"
$rule = $ruleJson | ConvertFrom-Json

$current = @()
if ($rule.sourceAddressPrefix) { $current += $rule.sourceAddressPrefix }
if ($rule.sourceAddressPrefixes) { $current += $rule.sourceAddressPrefixes }
$current = $current | Where-Object { $_ } | Sort-Object -Unique

Write-Host "  現在の許可元: $($current -join ', ')"

if ($current -contains '0.0.0.0/0' -or $current -contains 'Internet' -or $current -contains '*') {
    Write-Warning 'RDP が全世界に開いています。この規則は絞り込むべきです。'
}

$desired = @("$myIp/32")
foreach ($ip in $AdditionalIp) {
    $desired += if ($ip -match '/') { $ip } else { "$ip/32" }
}
$desired = $desired | Sort-Object -Unique

if (-not (Compare-Object $current $desired)) {
    Write-Host '  変更は不要です。今の IP はすでに許可されています。' -ForegroundColor Green
    return
}

Write-Host "  あるべき許可元: $($desired -join ', ')" -ForegroundColor Yellow

if ($CheckOnly) {
    Write-Host '  -CheckOnly が指定されているので、書き換えは行いません。' -ForegroundColor Yellow
    return
}

Write-Step '許可 IP を書き換えます'
$updateArgs = @('network', 'nsg', 'rule', 'update',
    '-g', $ResourceGroup, '--nsg-name', $NsgName, '-n', $RuleName,
    '--source-address-prefixes') + $desired + @('-o', 'none')
Invoke-Az $updateArgs | Out-Null

Write-Host '  更新しました。RDP をつなぎ直してください。' -ForegroundColor Green
