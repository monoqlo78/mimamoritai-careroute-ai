<#
.SYNOPSIS
  Extracts the operator-console snapshot from the 見守り隊 production Azure SQL
  database into snapshot.json, for seeding the Fabric (Rayfin) console.

.DESCRIPTION
  Read-only. Runs scripts/extract-snapshot.sql, which mirrors
  MimamoriTai.Web/Services/AdminConsoleService.LoadAsync and only touches the
  `mimamori` schema. Encrypted SwitchBot credentials and the family-facing
  WatchAlert.Message body are deliberately never selected.

  Authenticates with the caller's own Entra token via `az account get-access-token`,
  so no connection secret is stored in this repo.
#>
[CmdletBinding()]
param(
    [string]$Server = 'sqldb-mngenv.database.windows.net',
    [string]$Database = 'free-sql-db-5743178',
    [string]$OutFile = (Join-Path $PSScriptRoot '..\snapshot.json')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

$token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
if (-not $token) { throw 'Could not acquire an Azure SQL access token. Run `az login` first.' }

$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=tcp:$Server,1433;Database=$Database;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60"
$conn.AccessToken = $token
$conn.Open()

try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = Get-Content (Join-Path $PSScriptRoot 'extract-snapshot.sql') -Raw
    $cmd.CommandTimeout = 120

    $ds = New-Object System.Data.DataSet
    (New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($ds) | Out-Null
}
finally {
    $conn.Close()
}

function ConvertTo-Rows([System.Data.DataTable]$table) {
    $names = @($table.Columns | ForEach-Object { $_.ColumnName })
    foreach ($row in $table.Rows) {
        $o = [ordered]@{}
        foreach ($n in $names) {
            $v = $row[$n]
            $o[$n] = if ($v -is [System.DBNull]) { $null }
                     elseif ($v -is [datetime] -or $v -is [datetimeoffset]) { ([datetimeoffset]$v).ToUniversalTime().ToString('o') }
                     elseif ($v -is [guid]) { $v.ToString() }
                     else { $v }
        }
        [pscustomobject]$o
    }
}

$payload = [ordered]@{
    capturedAt = (Get-Date).ToUniversalTime().ToString('o')
    source     = "$Server/$Database (schema: mimamori)"
    windowDays = 7
    households = @(ConvertTo-Rows $ds.Tables[0])
    alerts     = @(ConvertTo-Rows $ds.Tables[1])
}

$payload | ConvertTo-Json -Depth 6 | Set-Content -Path $OutFile -Encoding utf8
Write-Host "Wrote $($payload.households.Count) households and $($payload.alerts.Count) alerts to $OutFile"
