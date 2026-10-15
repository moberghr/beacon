SELECT count(*)::int
FROM beacon.query_tasks AS q
WHERE q.archived_time IS NULL
;
SELECT count(*)::int
FROM beacon.query_tasks AS q
WHERE q.archived_time IS NULL AND NOT (q.resolved)
;
SELECT count(*)::int
FROM beacon.query_tasks AS q
WHERE q.archived_time IS NULL AND q.resolved