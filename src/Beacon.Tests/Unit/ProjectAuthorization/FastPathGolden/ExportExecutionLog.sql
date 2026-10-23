SELECT m.id AS "Id", m.created_time AS "CreatedTime", m.completed_time AS "CompletedTime", m.status AS "Status", m.success AS "Success", m.execution_context AS "ExecutionContext", m.data_source_id AS "DataSourceId", m.triggered_by_user_id AS "TriggeredByUserId", m.result_count AS "ResultCount", m.execution_time_ms AS "ExecutionTimeMs", m.error_message AS "ErrorMessage", m.query_text AS "QueryText"
FROM beacon.manual_query_execution_logs AS m
WHERE m.created_time >= @from AND m.created_time <= @to AND m.data_source_id IS NOT NULL
ORDER BY m.created_time DESC, m.id DESC
LIMIT @p
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