SELECT count(*)::int
FROM beacon.mcp_learned_patterns AS m
;
SELECT m.id AS "Id", m.project_id AS "ProjectId", m.data_source_id AS "DataSourceId", m.schema_name AS "SchemaName", m.table_name AS "TableName", m.column_name AS "ColumnName", m.pattern_type AS "PatternType", m.pattern_content AS "PatternContent", m.example_question AS "ExampleQuestion", m.example_sql AS "ExampleSql", m.signal_count AS "SignalCount", m.confidence AS "Confidence", m.status AS "Status", m.created_time AS "CreatedTime", m.last_refreshed_at AS "LastRefreshedAt", (
    SELECT CASE
        WHEN u.display_name IS NULL OR btrim(u.display_name, E' \t\n\r') = '' THEN 'User ' || u.id::text
        ELSE u.display_name
    END
    FROM beacon.users AS u
    WHERE u.archived_time IS NULL AND u.id = m.reviewed_by_user_id
    LIMIT 1) AS "ReviewedByName", m.reviewed_at AS "ReviewedAt"
FROM beacon.mcp_learned_patterns AS m
ORDER BY m.confidence DESC, m.signal_count DESC, m.id
LIMIT @p3 OFFSET @p