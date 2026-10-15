SELECT count(*)::int
FROM beacon.ai_actors AS a
WHERE a.archived_time IS NULL
;
SELECT a0.id AS "ActorId", a0.name AS "Name", CASE
    WHEN length(a0.instructions)::int > 100 THEN substring(a0.instructions, 1, 100) || '...'
    ELSE a0.instructions
END AS "Instructions", a0.data_source_id AS "DataSourceId", d0.name AS "DataSourceName", a0.status AS "Status", a0.think_count AS "ThinkCount", a0.last_think_time AS "LastThinkTime", a0.total_cost AS "TotalCost", a0.created_time AS "CreatedTime"
FROM (
    SELECT a.id, a.created_time, a.data_source_id, a.instructions, a.last_think_time, a.name, a.status, a.think_count, a.total_cost
    FROM beacon.ai_actors AS a
    WHERE a.archived_time IS NULL
    ORDER BY a.created_time DESC, a.id
    LIMIT @p1 OFFSET @p
) AS a0
INNER JOIN (
    SELECT d.id, d.name
    FROM beacon.data_sources AS d
    WHERE d.archived_time IS NULL
) AS d0 ON a0.data_source_id = d0.id
ORDER BY a0.created_time DESC, a0.id