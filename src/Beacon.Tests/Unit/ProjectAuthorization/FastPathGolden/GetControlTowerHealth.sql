SELECT count(*)::int
FROM beacon.subscriptions AS s
INNER JOIN (
    SELECT q.id
    FROM beacon.queries AS q
    WHERE q.archived_time IS NULL
) AS q0 ON s.query_id = q0.id
WHERE s.archived_time IS NULL
;
SELECT s0.id AS "Id", s0.created_time AS "SubscriptionCreatedTime", s0.name AS "QueryName", q6.path AS "FolderPath", s0.create_tasks AS "CreateTasks", s0.store_results AS "StoreResults", s0.ai_actor_id AS "AiActorId", a0.name AS "AiActorName", (
    SELECT count(*)::int
    FROM beacon.query_execution_history AS q7
    WHERE s0.id = q7.subscription_id AND q7.created_time >= @windowStart) AS "TotalExecutions", (
    SELECT count(*)::int
    FROM beacon.query_execution_history AS q8
    WHERE s0.id = q8.subscription_id AND q8.created_time >= @windowStart AND q8.notification_status IN (2, 4, 3)) AS "SuccessfulExecutions", (
    SELECT count(*)::int
    FROM beacon.query_tasks AS q9
    WHERE q9.archived_time IS NULL AND q9.subscription_id = s0.id AND NOT (q9.resolved)) AS "UnresolvedTaskCount", (
    SELECT count(*)::int
    FROM beacon.query_tasks AS q10
    WHERE q10.archived_time IS NULL AND q10.subscription_id = s0.id) AS "TotalTaskCount"
FROM (
    SELECT s.id, s.ai_actor_id, s.create_tasks, s.created_time, s.store_results, q0.folder_id, q0.name, (
        SELECT count(*)::int
        FROM beacon.query_tasks AS q1
        WHERE q1.archived_time IS NULL AND q1.subscription_id = s.id AND NOT (q1.resolved)) AS c, CASE
        WHEN (
            SELECT count(*)::int
            FROM beacon.query_execution_history AS q2
            WHERE s.id = q2.subscription_id AND q2.created_time >= @windowStart) = 0 THEN 100.0
        ELSE ((
            SELECT count(*)::int
            FROM beacon.query_execution_history AS q3
            WHERE s.id = q3.subscription_id AND q3.created_time >= @windowStart AND q3.notification_status IN (2, 4, 3))::double precision / (
            SELECT count(*)::int
            FROM beacon.query_execution_history AS q4
            WHERE s.id = q4.subscription_id AND q4.created_time >= @windowStart)::double precision) * 100.0
    END AS c0
    FROM beacon.subscriptions AS s
    INNER JOIN (
        SELECT q.id, q.folder_id, q.name
        FROM beacon.queries AS q
        WHERE q.archived_time IS NULL
    ) AS q0 ON s.query_id = q0.id
    WHERE s.archived_time IS NULL
    ORDER BY (
        SELECT count(*)::int
        FROM beacon.query_tasks AS q1
        WHERE q1.archived_time IS NULL AND q1.subscription_id = s.id AND NOT (q1.resolved)) DESC, CASE
        WHEN (
            SELECT count(*)::int
            FROM beacon.query_execution_history AS q2
            WHERE s.id = q2.subscription_id AND q2.created_time >= @windowStart) = 0 THEN 100.0
        ELSE ((
            SELECT count(*)::int
            FROM beacon.query_execution_history AS q3
            WHERE s.id = q3.subscription_id AND q3.created_time >= @windowStart AND q3.notification_status IN (2, 4, 3))::double precision / (
            SELECT count(*)::int
            FROM beacon.query_execution_history AS q4
            WHERE s.id = q4.subscription_id AND q4.created_time >= @windowStart)::double precision) * 100.0
    END, q0.name, s.id
    LIMIT @p5 OFFSET @p
) AS s0
LEFT JOIN (
    SELECT q5.id, q5.path
    FROM beacon.query_folders AS q5
    WHERE q5.archived_time IS NULL
) AS q6 ON s0.folder_id = q6.id
LEFT JOIN (
    SELECT a.id, a.name
    FROM beacon.ai_actors AS a
    WHERE a.archived_time IS NULL
) AS a0 ON s0.ai_actor_id = a0.id
ORDER BY s0.c DESC, s0.c0, s0.name, s0.id