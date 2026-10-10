SELECT count(*)::int
FROM beacon.query_approval_requests AS q
WHERE q.status = 1
;
SELECT q1.id AS "Id", q1.query_id AS "QueryId", q2.name AS "QueryName", q3.version_number AS "VersionNumber", q1.status AS "Status", q1.requested_by_user_name AS "RequestedByUserName", q1.created_time AS "CreatedTime", q1.change_summary AS "ChangeSummary"
FROM (
    SELECT q.id, q.change_summary, q.created_time, q.query_id, q.query_version_id, q.requested_by_user_name, q.status
    FROM beacon.query_approval_requests AS q
    WHERE q.status = 1
    ORDER BY q.created_time DESC, q.id
    LIMIT @p1 OFFSET @p
) AS q1
INNER JOIN (
    SELECT q0.id, q0.name
    FROM beacon.queries AS q0
    WHERE q0.archived_time IS NULL
) AS q2 ON q1.query_id = q2.id
INNER JOIN beacon.query_versions AS q3 ON q1.query_version_id = q3.id
ORDER BY q1.created_time DESC, q1.id