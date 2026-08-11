<#
.SYNOPSIS
  Pushes the 見守り隊 operator-console snapshot from the production Azure SQL
  database into the Fabric (Rayfin) SQL database that backs this console.

.DESCRIPTION
  This is the ingestion path README.md listed as unresolved: it reads the
  production database (read-only, `mimamori` schema only, via
  scripts/extract-snapshot.ps1) and MERGEs the result into the
  `HouseholdSnapshots` / `AlertRecords` tables Rayfin generated in Fabric.

  Both ends authenticate with the caller's own Entra token, so no connection
  secret lives in this repo. The production database is never written to.

  Rows are keyed so re-running is idempotent:
    - HouseholdSnapshots.id is derived deterministically from the household GUID
    - AlertRecords.id is the source WatchAlert.Id

  Run after `rayfin up`, which is what creates the target tables.

.EXAMPLE
  ./scripts/sync-to-fabric.ps1
#>
[CmdletBinding()]
param(
    [string]$SnapshotFile,

    # Target Fabric SQL database. Defaults are read from rayfin/.deployments.json
    # when not supplied.
    [string]$FabricServer,
    [string]$FabricDatabase,

    # Skip the production read and reuse an existing snapshot.json.
    [switch]$UseExistingSnapshot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

if (-not $SnapshotFile) { $SnapshotFile = Join-Path $PSScriptRoot '..\snapshot.json' }

if (-not $UseExistingSnapshot) {
    Write-Host 'Reading production snapshot...'
    & (Join-Path $PSScriptRoot 'extract-snapshot.ps1') -OutFile $SnapshotFile
}

$snapshot = Get-Content $SnapshotFile -Raw | ConvertFrom-Json

# Resolve the Fabric SQL endpoint from the deployment record unless overridden.
if (-not $FabricServer -or -not $FabricDatabase) {
    $deployFile = Join-Path $PSScriptRoot '..\rayfin\.deployments.json'
    if (-not (Test-Path $deployFile)) {
        throw "rayfin/.deployments.json not found. Run `rayfin up` first, or pass -FabricServer/-FabricDatabase."
    }
    $deployments = Get-Content $deployFile -Raw | ConvertFrom-Json
    $active = $deployments.deployments.($deployments.active)

    $fabricToken = az account get-access-token --resource https://api.fabric.microsoft.com --query accessToken -o tsv
    $item = Invoke-RestMethod -Headers @{ Authorization = "Bearer $fabricToken" } `
        -Uri "https://api.fabric.microsoft.com/v1/workspaces/$($active.fabricWorkspaceId)/sqlDatabases"
    $db = $item.value | Where-Object { $_.displayName -eq 'mimamoritai-admin' } | Select-Object -First 1
    if (-not $db) { throw 'Could not find the "mimamoritai-admin" SQL database in the deployed workspace.' }

    if (-not $FabricServer) { $FabricServer = $db.properties.serverFqdn }
    if (-not $FabricDatabase) { $FabricDatabase = $db.properties.databaseName }
}

$sqlToken = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
if (-not $sqlToken) { throw 'Could not acquire an Azure SQL access token. Run `az login` first.' }

$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=tcp:$FabricServer;Database=$FabricDatabase;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60"
$conn.AccessToken = $sqlToken
$conn.Open()
Write-Host "Connected to Fabric SQL: $FabricDatabase"

# Rayfin columns are NOT NULL, and the UI treats "" as "unknown"; the source
# columns are nullable, so normalise here rather than in the query.
function Text($value) { if ($null -eq $value) { '' } else { [string]$value } }

# A household is flagged exactly as MimamoriTai.Web AdminConsoleService.NeedsAttention does.
function NeedsAttention($h) {
    return ([int]$h.FailedAlertsInWindow -gt 0) `
        -or ($h.SwitchBotStatus -eq 'Error') `
        -or ($h.DataSourceMode -eq 'Production' -and [int]$h.ActiveLineRecipients -eq 0)
}

# Stable per-household snapshot id, so repeated syncs update instead of duplicating.
function SnapshotId([string]$householdId) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("household-snapshot:$householdId"))
    return ([guid]::new($bytes)).ToString()
}

# Activity buckets have no natural key in the source, so derive one from the
# grain (household + device + hour). Re-syncing an hour overwrites it in place.
function BucketId([string]$householdId, [string]$deviceName, [datetime]$bucketStart) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $key = "activity-bucket:$householdId|$deviceName|$($bucketStart.ToString('o'))"
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($key))
    return ([guid]::new($bytes)).ToString()
}

function Invoke-NonQuery([string]$sql, [hashtable]$params) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $cmd.CommandTimeout = 120
    foreach ($k in $params.Keys) {
        $v = $params[$k]
        $null = $cmd.Parameters.AddWithValue("@$k", $(if ($null -eq $v) { [DBNull]::Value } else { $v }))
    }
    return $cmd.ExecuteNonQuery()
}

$capturedAt = [datetime]::Parse($snapshot.capturedAt).ToUniversalTime()

try {
    $householdMerge = @'
MERGE dbo.HouseholdSnapshots AS t
USING (SELECT @id AS id) AS s ON t.id = s.id
WHEN MATCHED THEN UPDATE SET
    householdId = @householdId, name = @name, dataSourceMode = @dataSourceMode,
    memberCount = @memberCount, residentCount = @residentCount, deviceCount = @deviceCount,
    lastEventUtc = @lastEventUtc, switchBotStatus = @switchBotStatus, switchBotError = @switchBotError,
    activeLineRecipients = @activeLineRecipients, alertsInWindow = @alertsInWindow,
    failedAlertsInWindow = @failedAlertsInWindow, latestRiskLevel = @latestRiskLevel,
    needsAttention = @needsAttention, capturedAt = @capturedAt
WHEN NOT MATCHED THEN INSERT
    (id, householdId, name, dataSourceMode, memberCount, residentCount, deviceCount,
     lastEventUtc, switchBotStatus, switchBotError, activeLineRecipients, alertsInWindow,
     failedAlertsInWindow, latestRiskLevel, needsAttention, capturedAt)
VALUES
    (@id, @householdId, @name, @dataSourceMode, @memberCount, @residentCount, @deviceCount,
     @lastEventUtc, @switchBotStatus, @switchBotError, @activeLineRecipients, @alertsInWindow,
     @failedAlertsInWindow, @latestRiskLevel, @needsAttention, @capturedAt);
'@

    foreach ($h in $snapshot.households) {
        Invoke-NonQuery $householdMerge @{
            id                   = [guid](SnapshotId $h.HouseholdId)
            householdId          = Text $h.HouseholdId
            name                 = Text $h.Name
            dataSourceMode       = Text $h.DataSourceMode
            memberCount          = [string]$h.MemberCount
            residentCount        = [string]$h.ResidentCount
            deviceCount          = [string]$h.DeviceCount
            lastEventUtc         = Text $h.LastEventUtc
            switchBotStatus      = Text $h.SwitchBotStatus
            switchBotError       = Text $h.SwitchBotError
            activeLineRecipients = [string]$h.ActiveLineRecipients
            alertsInWindow       = [string]$h.AlertsInWindow
            failedAlertsInWindow = [string]$h.FailedAlertsInWindow
            latestRiskLevel      = Text $h.LatestRiskLevel
            needsAttention       = [bool](NeedsAttention $h)
            capturedAt           = $capturedAt
        } | Out-Null
    }
    Write-Host "Synced $($snapshot.households.Count) household snapshots"

    $alertMerge = @'
MERGE dbo.AlertRecords AS t
USING (SELECT @id AS id) AS s ON t.id = s.id
WHEN MATCHED THEN UPDATE SET
    householdId = @householdId, householdName = @householdName, riskLevel = @riskLevel,
    score = @score, reason = @reason, success = @success, error = @error, sentAt = @sentAt
WHEN NOT MATCHED THEN INSERT
    (id, householdId, householdName, riskLevel, score, reason, success, error, sentAt)
VALUES
    (@id, @householdId, @householdName, @riskLevel, @score, @reason, @success, @error, @sentAt);
'@

    foreach ($a in $snapshot.alerts) {
        Invoke-NonQuery $alertMerge @{
            id            = [guid]$a.AlertId
            householdId   = Text $a.HouseholdId
            householdName = Text $a.HouseholdName
            riskLevel     = Text $a.RiskLevel
            score         = [string]$a.Score
            reason        = Text $a.Reason
            success       = [bool]$a.Success
            error         = Text $a.Error
            sentAt        = [datetime]::Parse($a.SentAtUtc).ToUniversalTime()
        } | Out-Null
    }
    Write-Host "Synced $($snapshot.alerts.Count) alert records"

    $activityMerge = @'
MERGE dbo.ActivityBuckets AS t
USING (SELECT @id AS id) AS s ON t.id = s.id
WHEN MATCHED THEN UPDATE SET
    householdId = @householdId, householdName = @householdName, deviceName = @deviceName,
    deviceType = @deviceType, bucketStart = @bucketStart, eventCount = @eventCount,
    onCount = @onCount, source = @source
WHEN NOT MATCHED THEN INSERT
    (id, householdId, householdName, deviceName, deviceType, bucketStart, eventCount, onCount, source)
VALUES
    (@id, @householdId, @householdName, @deviceName, @deviceType, @bucketStart, @eventCount, @onCount, @source);
'@

    foreach ($b in $snapshot.activity) {
        $bucketStart = [datetime]::Parse($b.BucketStart).ToUniversalTime()
        Invoke-NonQuery $activityMerge @{
            id            = [guid](BucketId $b.HouseholdId $b.DeviceName $bucketStart)
            householdId   = Text $b.HouseholdId
            householdName = Text $b.HouseholdName
            deviceName    = Text $b.DeviceName
            deviceType    = Text $b.DeviceType
            bucketStart   = $bucketStart
            eventCount    = [string]$b.EventCount
            onCount       = [string]$b.OnCount
            source        = Text $b.Source
        } | Out-Null
    }
    Write-Host "Synced $($snapshot.activity.Count) activity buckets"
}
finally {
    $conn.Close()
}

Write-Host 'Done.'
