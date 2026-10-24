SELECT u.id AS "Id", u.external_id AS "ExternalId", u.created_time AS "CreatedTime", u.is_enabled AND u.archived_time IS NULL AS "IsActive", u.security_stamp AS "SecurityStamp", u.is_super_admin OR EXISTS (
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
SELECT count(*)::int
FROM beacon.ai_actor_plans AS a
WHERE a.status = 1 AND a.proposed_at > @cutoff AND a.ai_actor_id IN (
    SELECT a0.id
    FROM beacon.ai_actors AS a0
    WHERE a0.archived_time IS NULL AND a0.created_by_user_id = @caller_UserId
)