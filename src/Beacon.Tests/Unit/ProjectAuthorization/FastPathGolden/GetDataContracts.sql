SELECT count(*)::int
FROM beacon.data_contracts AS d
WHERE d.archived_time IS NULL
;
SELECT d1.id AS "Id", d1.data_source_id AS "DataSourceId", d2.name AS "DataSourceName", d1.schema_name AS "SchemaName", d1.table_name AS "TableName", d1.name AS "Name", d1.description AS "Description", d1.cron_expression AS "CronExpression", d1.is_enabled AS "IsEnabled", d1.owner_user_id AS "OwnerUserId", d1.alert_on_failure AS "AlertOnFailure", d1.failure_threshold_score AS "FailureThresholdScore", d1.created_time AS "CreatedTime", (
    SELECT d3.score
    FROM beacon.data_quality_scores AS d3
    WHERE d3.data_source_id = d1.data_source_id AND d3.schema_name = d1.schema_name AND d3.table_name = d1.table_name
    LIMIT 1) AS "LatestScore"
FROM (
    SELECT d.id, d.alert_on_failure, d.created_time, d.cron_expression, d.data_source_id, d.description, d.failure_threshold_score, d.is_enabled, d.name, d.owner_user_id, d.schema_name, d.table_name
    FROM beacon.data_contracts AS d
    WHERE d.archived_time IS NULL
    ORDER BY d.created_time DESC, d.id
    LIMIT @p2 OFFSET @p
) AS d1
INNER JOIN (
    SELECT d0.id, d0.name
    FROM beacon.data_sources AS d0
    WHERE d0.archived_time IS NULL
) AS d2 ON d1.data_source_id = d2.id
ORDER BY d1.created_time DESC, d1.id