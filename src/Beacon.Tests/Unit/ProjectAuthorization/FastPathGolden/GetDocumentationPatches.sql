SELECT count(*)::int
FROM beacon.mcp_documentation_patches AS m
;
SELECT m.id AS "Id", m.project_id AS "ProjectId", m.data_source_id AS "DataSourceId", m.target_type AS "TargetType", m.target_identifier AS "TargetIdentifier", m.current_content AS "CurrentContent", m.proposed_content AS "ProposedContent", m.reasoning AS "Reasoning", m.supporting_signal_count AS "SupportingSignalCount", m.status AS "Status", m.created_time AS "CreatedTime", m.applied_at AS "AppliedAt", (
    SELECT CASE
        WHEN u.display_name IS NULL OR btrim(u.display_name, E' \t\n\r') = '' THEN 'User ' || u.id::text
        ELSE u.display_name
    END
    FROM beacon.users AS u
    WHERE u.archived_time IS NULL AND u.id = m.applied_by_user_id
    LIMIT 1) AS "AppliedByName"
FROM beacon.mcp_documentation_patches AS m
ORDER BY m.supporting_signal_count DESC, m.id
LIMIT @p3 OFFSET @p