SELECT d1.data_source_id AS "DataSourceId", d2.name AS "DataSourceName", d1.schema_name AS "SchemaName", d1.table_name AS "TableName", d1.score AS "Score", d1.evaluated_at AS "EvaluatedAt", d1.trend_direction AS "TrendDirection", d1.previous_score AS "PreviousScore", d1.id AS "Id"
FROM (
    SELECT d.id, d.data_source_id, d.evaluated_at, d.previous_score, d.schema_name, d.score, d.table_name, d.trend_direction
    FROM beacon.data_quality_scores AS d
    ORDER BY d.id
    LIMIT @p
) AS d1
INNER JOIN (
    SELECT d0.id, d0.name
    FROM beacon.data_sources AS d0
    WHERE d0.archived_time IS NULL
) AS d2 ON d1.data_source_id = d2.id
ORDER BY d1.id
;
SELECT d.data_source_id AS "DataSourceId", count(*)::int AS "Count", count(*) FILTER (WHERE d.owner_not_authorized_since IS NOT NULL)::int AS "OwnerNotAuthorized"
FROM beacon.data_contracts AS d
WHERE d.archived_time IS NULL AND d.is_enabled
GROUP BY d.data_source_id