<#
.SYNOPSIS
    docs/ARTICLE.md から Qiita 貼り付け用の本文を作る。

.DESCRIPTION
    Qiita 側は記事タイトルを本文とは別に持ち、画像も相対パスでは解決できないので、
    リポジトリの原本をそのまま貼ると必ず崩れる。手で直すと原本と公開版がすぐ食い違う
    ので、変換をスクリプトにしてある。原本は docs/ARTICLE.md だけ。

    やっていることは3つ:
      1. H1 見出しを落とす（Qiita のタイトル欄に入るため、本文にあると二重になる）
      2. 「Qiita 公開版」への自己リンクを落とす（記事自身を指しても意味がない）
      3. 画像の相対パスを公開ミラーの raw URL に差し替える
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\docs\ARTICLE.md'),
    [string]$Destination,
    # 画像は公開ミラー側から配信する。origin は private なので raw が 404 になる。
    [string]$RawBase = 'https://raw.githubusercontent.com/monoqlo78/mimamoritai-careroute-ai/main/docs/images/'
)

$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $Destination = Join-Path $PSScriptRoot '..\docs\qiita-body.md'
}

$lines = [System.Collections.Generic.List[string]](Get-Content -LiteralPath $Source -Encoding utf8)

$out = [System.Collections.Generic.List[string]]::new()
foreach ($line in $lines) {
    # タイトルは Qiita のタイトル欄が持つ。
    if ($out.Count -eq 0 -and $line -match '^#\s') { continue }
    # 記事自身へのリンクと、その直前の引用の区切り。
    if ($line -match '^>\s*Qiita 公開版\s*:') { continue }
    $out.Add($line)
}

# 先頭の空行と、引用ブロック末尾に取り残された "> " だけの行を落とす。
while ($out.Count -gt 0 -and [string]::IsNullOrWhiteSpace($out[0])) { $out.RemoveAt(0) }
for ($i = $out.Count - 1; $i -ge 1; $i--) {
    if ($out[$i].TrimEnd() -eq '>' -and $out[$i - 1].StartsWith('>')) {
        if ($i + 1 -ge $out.Count -or -not $out[$i + 1].StartsWith('>')) { $out.RemoveAt($i) }
    }
}

$body = ($out -join "`n")
$body = $body -replace '\]\(\./images/', "]($RawBase"
$body = $body -replace '\]\(images/', "]($RawBase"

Set-Content -LiteralPath $Destination -Value $body -Encoding utf8NoBOM

$remaining = ([regex]::Matches($body, '\]\((\./)?images/')).Count
Write-Host "Wrote $Destination"
Write-Host "  lines             : $($out.Count)"
Write-Host "  raw image links   : $(([regex]::Matches($body, [regex]::Escape($RawBase))).Count)"
Write-Host "  unresolved images : $remaining"
if ($remaining -gt 0) { throw "相対パスの画像が残っている。" }
