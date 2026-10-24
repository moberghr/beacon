SELECT count(*)::int
FROM (
    SELECT 1
    FROM beacon.data_quality_scores AS d
    INNER JOIN (
        SELECT d0.id, d0.name
        FROM beacon.data_sources AS d0
        WHERE d0.archived_time IS NULL
    ) AS d1 ON d.data_source_id = d1.id
    GROUP BY d.data_source_id, d1.name
) AS s
;
SELECT d.data_source_id AS "DataSourceId", d1.name AS "DataSourceName", avg(d.score) AS "AverageScore", count(*)::int AS "TotalTables", count(*) FILTER (WHERE d.score >= 80.0)::int AS "HealthyTables", count(*) FILTER (WHERE d.trend_direction = 3)::int AS "DegradingTables"
FROM beacon.data_quality_scores AS d
INNER JOIN (
    SELECT d0.id, d0.name
    FROM beacon.data_sources AS d0
    WHERE d0.archived_time IS NULL
) AS d1 ON d.data_source_id = d1.id
GROUP BY d.data_source_id, d1.name
ORDER BY d1.name, d.data_source_id
LIMIT @p1 OFFSET @p
;
SELECT d.data_source_id AS "DataSourceId", count(*)::int AS "Count", count(*) FILTER (WHERE d.owner_not_authorized_since IS NOT NULL)::int AS "OwnerNotAuthorized"
FROM beacon.data_contracts AS d
WHERE d.archived_time IS NULL AND d.data_source_id = ANY (@sourceIds) AND d.is_enabled
GROUP BY d.data_source_id
;
SELECT d.id AS "Id", d.data_source_id AS "DataSourceId", d.schema_name AS "SchemaName", d.table_name AS "TableName", d.score AS "Score", d.evaluated_at AS "EvaluatedAt", d.trend_direction AS "TrendDirection", d.previous_score AS "PreviousScore"
FROM beacon.data_quality_scores AS d
WHERE d.data_source_id = ANY (@sourceIds) AND (
    SELECT count(*)::int
    FROM beacon.data_quality_scores AS d0
    WHERE d0.data_source_id = d.data_source_id AND (d0.score < d.score OR (d0.score = d.score AND d0.id < d.id))) < 20
ORDER BY d.score, d.id