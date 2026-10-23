SELECT m0.id AS "Id", m0.created_time AS "CreatedTime", m0.completed_time AS "CompletedTime", CASE
    WHEN m0.status = 1 AND m0.created_time < @abandonedBefore THEN 6
    ELSE m0.status
END AS "Status", m0.success AS "Success", m0.execution_context AS "ExecutionContext", m0.data_source_id AS "DataSourceId", m0.triggered_by_user_id AS "TriggeredByUserId", m0.result_count AS "ResultCount", m0.execution_time_ms AS "ExecutionTimeMs", m0.error_message AS "ErrorMessage", substring(m0.query_text, 1, 4001) AS "QueryText", d0.data_source_type AS "DataSourceType", d0.database_engine_type AS "DatabaseEngineType"
FROM (
    SELECT m.id, m.completed_time, m.created_time, m.data_source_id, m.error_message, m.execution_context, m.execution_time_ms, m.query_text, m.result_count, m.status, m.success, m.triggered_by_user_id
    FROM beacon.manual_query_execution_logs AS m
    WHERE m.created_time >= @from AND m.created_time <= @to AND m.data_source_id IS NOT NULL
    ORDER BY m.created_time DESC, m.id DESC
    LIMIT @p
) AS m0
LEFT JOIN (
    SELECT d.id, d.data_source_type, d.database_engine_type
    FROM beacon.data_sources AS d
    WHERE d.archived_time IS NULL
) AS d0 ON m0.data_source_id = d0.id
ORDER BY m0.created_time DESC, m0.id DESC
;
SELECT u.id AS "Id", u.external_id AS "ExternalId", u.is_enabled AND u.archived_time IS NULL AS "IsActive", u.security_stamp AS "SecurityStamp", u.is_super_admin OR EXISTS (
    SELECT 1
    FROM beacon.user_roles AS u0
    INNER JOIN beacon.roles AS r ON u0.role_id = r.id
    WHERE u.id = u0.user_id AND r.name = 'Admin') AS "IsLiveAdmin", CASE
    WHEN u.is_super_admin THEN 3
    ELSE COALESCE((
        SELECT max(r0.level)
        FROM beacon.user_roles AS u1
        INNER JOIN beacon.roles AS r0 ON u1.role_id = r0.id
        WHERE u.id = u1.user_id), 0)
END AS "RoleLevel"
FROM beacon.users AS u
WHERE u.external_id = @lookup_ExternalId
ORDER BY u.archived_time IS NOT NULL, u.is_enabled DESC, u.id
LIMIT 1
;
INSERT INTO beacon.security_audit_events (action, actor_kind, actor_user_id, api_key_id, caller_hash, category, created_time, details_json, event_guid, hmac, key_id, outcome, project_id, resource_id, resource_type, trace_id)
VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15)
RETURNING id;