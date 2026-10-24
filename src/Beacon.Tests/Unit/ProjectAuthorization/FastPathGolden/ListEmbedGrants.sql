SELECT count(*)::int
FROM beacon.embed_grants AS e
;
SELECT e0.id AS "Id", e0.public_id AS "PublicId", e0.name AS "Name", e0.project_id AS "ProjectId", (
    SELECT p.name
    FROM beacon.projects AS p
    WHERE p.archived_time IS NULL AND p.id = e0.project_id
    LIMIT 1) AS "ProjectName", e0.query_id AS "QueryId", (
    SELECT q0.name
    FROM beacon.queries AS q0
    WHERE q0.archived_time IS NULL AND q0.id = e0.query_id
    LIMIT 1) AS "QueryName", e0.query_version_id AS "QueryVersionId", q.version_number AS "VersionNumber", e0.allowed_origins_json AS "AllowedOriginsJson", e0.locked_parameter_names_json AS "LockedParameterNamesJson", (
    SELECT CASE
        WHEN u.display_name IS NULL OR btrim(u.display_name, E' \t\n\r') = '' THEN 'User ' || u.id::text
        ELSE u.display_name
    END
    FROM beacon.users AS u
    WHERE u.archived_time IS NULL AND u.external_id = e0.created_by_user_id
    LIMIT 1) AS "CreatedByLabel", e0.created_time AS "CreatedTime", e0.expires_time AS "ExpiresTime", e0.revoked_time AS "RevokedTime", e0.last_token_issued_time AS "LastTokenIssuedTime", CASE
    WHEN e0.revoked_time IS NOT NULL THEN 'revoked'
    WHEN e0.expires_time <= @now THEN 'expired'
    WHEN EXISTS (
        SELECT 1
        FROM beacon.queries AS q1
        WHERE q1.archived_time IS NULL AND q1.id = e0.query_id AND q1.active_version_id = e0.query_version_id) THEN 'active'
    ELSE 'superseded'
END AS "Status"
FROM (
    SELECT e.id, e.allowed_origins_json, e.created_by_user_id, e.created_time, e.expires_time, e.last_token_issued_time, e.locked_parameter_names_json, e.name, e.project_id, e.public_id, e.query_id, e.query_version_id, e.revoked_time
    FROM beacon.embed_grants AS e
    ORDER BY e.created_time DESC, e.id
    LIMIT @p7 OFFSET @p
) AS e0
INNER JOIN beacon.query_versions AS q ON e0.query_version_id = q.id
ORDER BY e0.created_time DESC, e0.id