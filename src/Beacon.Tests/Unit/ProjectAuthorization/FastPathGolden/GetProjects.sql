SELECT count(*)::int
FROM beacon.projects AS p
WHERE p.archived_time IS NULL
;
SELECT p.id AS "Id", p.name AS "Name", p.description AS "Description", (
    SELECT count(*)::int
    FROM beacon.project_data_sources AS p0
    WHERE p.id = p0.project_id) AS "DataSourceCount", (
    SELECT count(*)::int
    FROM beacon.git_hub_repositories AS g
    WHERE p.id = g.project_id) AS "RepositoryCount", (
    SELECT max(g0.last_scan_at)
    FROM beacon.git_hub_repositories AS g0
    WHERE p.id = g0.project_id AND g0.last_scan_at IS NOT NULL) AS "LastScanAt", (
    SELECT g1.scan_status
    FROM beacon.git_hub_repositories AS g1
    WHERE p.id = g1.project_id AND g1.last_scan_at IS NOT NULL
    ORDER BY g1.last_scan_at DESC
    LIMIT 1) AS "LastScanStatus", p.created_time AS "CreatedAt", p.requires_membership AS "RequiresMembership", (
    SELECT count(*)::int
    FROM beacon.project_members AS p1
    WHERE p.id = p1.project_id) AS "MemberCount"
FROM beacon.projects AS p
WHERE p.archived_time IS NULL
ORDER BY p.created_time DESC, p.id
LIMIT @p1 OFFSET @p