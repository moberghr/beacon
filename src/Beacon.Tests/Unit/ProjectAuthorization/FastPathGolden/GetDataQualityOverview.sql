SELECT d.data_source_id AS "DataSourceId", d1.name AS "DataSourceName", d.schema_name AS "SchemaName", d.table_name AS "TableName", d.score AS "Score", d.evaluated_at AS "EvaluatedAt", d.trend_direction AS "TrendDirection", d.previous_score AS "PreviousScore", d.id AS "Id"
FROM beacon.data_quality_scores AS d
INNER JOIN (
    SELECT d0.id, d0.name
    FROM beacon.data_sources AS d0
    WHERE d0.archived_time IS NULL
) AS d1 ON d.data_source_id = d1.id
;
SELECT d.data_source_id AS "DataSourceId", count(*)::int AS "Count"
FROM beacon.data_contracts AS d
WHERE d.archived_time IS NULL AND d.is_enabled
GROUP BY d.data_source_id