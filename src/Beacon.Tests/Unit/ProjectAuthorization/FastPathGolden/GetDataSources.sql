SELECT count(*)::int
FROM beacon.data_sources AS d
WHERE d.archived_time IS NULL
;
SELECT d0.id, d0.name, d0.c, d0.c0, d0.c1, d0.metadata_loading_enabled, d0.open_when_unlinked, d0.allow_migration_writes, s0."Id", s0."Name", s0.id0, s0.id1, d0.c2
FROM (
    SELECT d.id, d.name, CASE d.data_source_type
        WHEN 1 THEN 'Database'
        WHEN 2 THEN 'CloudWatch'
        WHEN 3 THEN 'Prometheus'
        WHEN 4 THEN 'Datadog'
        WHEN 5 THEN 'ElasticSearch'
        WHEN 6 THEN 'Databricks'
        WHEN 7 THEN 'BigQuery'
        WHEN 8 THEN 'Api'
        ELSE d.data_source_type::text
    END AS c, CASE
        WHEN d.database_engine_type IS NOT NULL THEN CASE d.database_engine_type
            WHEN 1 THEN 'PostgreSQL'
            WHEN 2 THEN 'MSSQL'
            WHEN 3 THEN 'MySQL'
            WHEN 4 THEN 'SQLite'
            WHEN 5 THEN 'AzureSynapse'
            WHEN 6 THEN 'Snowflake'
            ELSE d.database_engine_type::text
        END
    END AS c0, (
        SELECT count(*)::int
        FROM (
            SELECT DISTINCT q.query_id
            FROM beacon.query_steps AS q
            INNER JOIN (
                SELECT q0.id, q0.archived_time
                FROM beacon.queries AS q0
                WHERE q0.archived_time IS NULL
            ) AS q1 ON q.query_id = q1.id
            WHERE d.id = q.data_source_id AND q1.archived_time IS NULL
        ) AS s) AS c1, d.metadata_loading_enabled, d.open_when_unlinked, d.allow_migration_writes, (
        SELECT count(*)::int
        FROM beacon.projects AS p2
        WHERE FALSE) AS c2
    FROM beacon.data_sources AS d
    WHERE d.archived_time IS NULL
    ORDER BY d.name, d.id
    LIMIT @p6 OFFSET @p
) AS d0
LEFT JOIN (
    SELECT p.project_id AS "Id", p1.name AS "Name", p.id AS id0, p1.id AS id1, p.data_source_id
    FROM beacon.project_data_sources AS p
    INNER JOIN (
        SELECT p0.id, p0.name
        FROM beacon.projects AS p0
        WHERE p0.archived_time IS NULL
    ) AS p1 ON p.project_id = p1.id
) AS s0 ON d0.id = s0.data_source_id
ORDER BY d0.name, d0.id, s0.id0