-- Mirrors MimamoriTai.Web/Services/AdminConsoleService.LoadAsync, read-only.
-- Window = 7 days, matching AdminConsoleService.DefaultWindowDays.
DECLARE @since DATETIMEOFFSET = DATEADD(day, -7, SYSUTCDATETIME());

SELECT
    h.Id                AS HouseholdId,
    h.Name              AS Name,
    h.DataSourceMode    AS DataSourceMode,
    ISNULL(mem.Cnt, 0)  AS MemberCount,
    ISNULL(res.Cnt, 0)  AS ResidentCount,
    ISNULL(dev.Cnt, 0)  AS DeviceCount,
    ev.LastEventUtc     AS LastEventUtc,
    sb.Status           AS SwitchBotStatus,
    sb.LastErrorMessage AS SwitchBotError,
    ISNULL(lr.Cnt, 0)   AS ActiveLineRecipients,
    ISNULL(al.Total, 0) AS AlertsInWindow,
    ISNULL(al.Failed, 0) AS FailedAlertsInWindow,
    risk.RiskLevel      AS LatestRiskLevel
FROM mimamori.Households h
LEFT JOIN (SELECT HouseholdId, COUNT(*) Cnt FROM mimamori.HouseholdMembers GROUP BY HouseholdId) mem
    ON mem.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, COUNT(*) Cnt FROM mimamori.People WHERE Role = 'Resident' GROUP BY HouseholdId) res
    ON res.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, COUNT(*) Cnt FROM mimamori.Devices GROUP BY HouseholdId) dev
    ON dev.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, MAX(OccurredAtUtc) LastEventUtc FROM mimamori.DeviceEvents GROUP BY HouseholdId) ev
    ON ev.HouseholdId = h.Id
-- Encrypted token/secret columns are deliberately not selected.
LEFT JOIN (SELECT HouseholdId, Status, LastErrorMessage FROM mimamori.SwitchBotConnections) sb
    ON sb.HouseholdId = h.Id
LEFT JOIN (SELECT HouseholdId, COUNT(*) Cnt FROM mimamori.LineRecipients WHERE IsActive = 1 GROUP BY HouseholdId) lr
    ON lr.HouseholdId = h.Id
LEFT JOIN (
    SELECT HouseholdId,
           COUNT(*) Total,
           SUM(CASE WHEN Success = 0 THEN 1 ELSE 0 END) Failed
    FROM mimamori.WatchAlerts WHERE SentAtUtc >= @since GROUP BY HouseholdId
) al ON al.HouseholdId = h.Id
OUTER APPLY (
    SELECT TOP 1 r.RiskLevel FROM mimamori.RiskAssessments r
    WHERE r.HouseholdId = h.Id ORDER BY r.CreatedAtUtc DESC
) risk
ORDER BY h.DataSourceMode, h.CreatedAtUtc;

-- WatchAlert.Message is intentionally excluded: it is family-facing prose that can
-- name the resident. Only the machine-generated Reason is mirrored.
SELECT TOP 50
    a.Id             AS AlertId,
    a.HouseholdId    AS HouseholdId,
    ISNULL(h.Name, N'(deleted)') AS HouseholdName,
    a.RiskLevel      AS RiskLevel,
    a.Score          AS Score,
    a.Reason         AS Reason,
    a.Success        AS Success,
    a.Error          AS Error,
    a.SentAtUtc      AS SentAtUtc
FROM mimamori.WatchAlerts a
LEFT JOIN mimamori.Households h ON h.Id = a.HouseholdId
WHERE a.SentAtUtc >= @since
ORDER BY a.SentAtUtc DESC;

-- Hourly activity rollup. Counts only: no raw payload, no resident identifier.
-- 30 days so the console has a usable time series even when alerting is quiet.
SELECT
    e.HouseholdId                                  AS HouseholdId,
    ISNULL(h.Name, N'(deleted)')                   AS HouseholdName,
    ISNULL(d.Name, N'(unknown)')                   AS DeviceName,
    ISNULL(d.DeviceType, N'')                      AS DeviceType,
    DATEADD(hour, DATEDIFF(hour, 0, CAST(e.OccurredAtUtc AS DATETIME2)), 0) AS BucketStart,
    COUNT(*)                                       AS EventCount,
    SUM(CASE WHEN e.State IN ('on', 'active') THEN 1 ELSE 0 END) AS OnCount,
    MAX(e.Source)                                  AS Source
FROM mimamori.DeviceEvents e
LEFT JOIN mimamori.Households h ON h.Id = e.HouseholdId
LEFT JOIN mimamori.Devices d ON d.Id = e.DeviceId
WHERE e.OccurredAtUtc >= DATEADD(day, -30, SYSUTCDATETIME())
GROUP BY
    e.HouseholdId,
    h.Name,
    d.Name,
    d.DeviceType,
    DATEADD(hour, DATEDIFF(hour, 0, CAST(e.OccurredAtUtc AS DATETIME2)), 0)
ORDER BY BucketStart;
