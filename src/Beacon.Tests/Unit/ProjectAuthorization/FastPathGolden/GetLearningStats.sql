SELECT count(*)::int
FROM beacon.mcp_query_signals AS m
WHERE m.project_id IS NOT NULL
;
SELECT count(*)::int
FROM beacon.mcp_query_signals AS m
WHERE m.project_id IS NOT NULL AND m.created_time >= @last7Days
;
SELECT count(*)::int
FROM beacon.mcp_query_signals AS m
WHERE m.project_id IS NOT NULL AND m.created_time >= @last30Days
;
SELECT count(*)::int
FROM beacon.mcp_query_signals AS m
WHERE m.project_id IS NOT NULL AND m.is_successful
;
SELECT count(*)::int
FROM beacon.mcp_learned_patterns AS m
WHERE m.status IN (1, 3)
;
SELECT count(*)::int
FROM beacon.mcp_learned_patterns AS m
WHERE m.status = 0
;
SELECT count(*)::int
FROM beacon.mcp_learned_patterns AS m
WHERE m.status = 2
;
SELECT count(*)::int
FROM beacon.mcp_documentation_patches AS m
WHERE m.status = 1
;
SELECT count(*)::int
FROM beacon.mcp_documentation_patches AS m
WHERE m.status = 0
;
SELECT m.tables_used AS "Tables", count(*)::int AS "Total", count(*) FILTER (WHERE NOT (m.is_successful))::int AS "Errors"
FROM beacon.mcp_query_signals AS m
WHERE m.project_id IS NOT NULL AND m.tables_used IS NOT NULL
GROUP BY m.tables_used
HAVING count(*)::int >= 3
ORDER BY count(*) FILTER (WHERE NOT (m.is_successful))::int::double precision / count(*)::int::double precision DESC
LIMIT @p