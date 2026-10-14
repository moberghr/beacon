SELECT count(*)::int
FROM beacon.queries AS q
WHERE q.archived_time IS NULL
;
SELECT q1.id, (
    SELECT count(*)::int
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL AND q1.id = s.query_id), q1.created_time, q1.name, q1.description, q1.folder_id, q2.path, q1.ai_actor_id, a0.name, q2.id, a0.id, s0.id, s0.step_order, s0.name, s0.description, s0.sql_value, s0.data_source_id, s0.name0, s0.database_engine_type, s0.id0, s0.name1, s0.type, s0.description0, s0.placeholder, s0.id1
FROM (
    SELECT q.id, q.ai_actor_id, q.created_time, q.description, q.folder_id, q.name
    FROM beacon.queries AS q
    WHERE q.archived_time IS NULL
    ORDER BY q.created_time DESC, q.id
    LIMIT @p1 OFFSET @p
) AS q1
LEFT JOIN (
    SELECT q0.id, q0.path
    FROM beacon.query_folders AS q0
    WHERE q0.archived_time IS NULL
) AS q2 ON q1.folder_id = q2.id
LEFT JOIN (
    SELECT a.id, a.name
    FROM beacon.ai_actors AS a
    WHERE a.archived_time IS NULL
) AS a0 ON q1.ai_actor_id = a0.id
LEFT JOIN (
    SELECT q3.id, q3.step_order, q3.name, q3.description, q3.sql_value, q3.data_source_id, d0.name AS name0, d0.database_engine_type, d0.id AS id0, q4.name AS name1, q4.type, q4.description AS description0, q4.placeholder, q4.id AS id1, q3.query_id
    FROM beacon.query_steps AS q3
    INNER JOIN (
        SELECT d.id, d.database_engine_type, d.name
        FROM beacon.data_sources AS d
        WHERE d.archived_time IS NULL
    ) AS d0 ON q3.data_source_id = d0.id
    LEFT JOIN beacon.query_step_parameters AS q4 ON q3.id = q4.query_step_id
) AS s0 ON q1.id = s0.query_id
ORDER BY q1.created_time DESC, q1.id, q2.id, a0.id, s0.step_order, s0.id, s0.id0