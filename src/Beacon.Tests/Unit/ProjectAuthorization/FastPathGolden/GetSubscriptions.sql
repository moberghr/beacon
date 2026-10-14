SELECT count(*)::int
FROM beacon.subscriptions AS s
WHERE s.archived_time IS NULL
;
SELECT s0.id, s0.query_id, s0.name, s0.cron_expression, (
    SELECT count(*)::int
    FROM beacon.recipient_subscription AS r
    INNER JOIN (
        SELECT r0.id, r0.archived_time
        FROM beacon.recipients AS r0
        WHERE r0.archived_time IS NULL
    ) AS r1 ON r.recipients_id = r1.id
    WHERE s0.id = r.subscriptions_id AND r1.archived_time IS NULL), s0.id0, a0.id, s1.name, s1.recipients_id, s1.subscriptions_id, s1.id, s0.ai_actor_id, a0.name, s0.create_tasks, s0.store_results
FROM (
    SELECT s.id, s.ai_actor_id, s.create_tasks, s.cron_expression, s.query_id, s.store_results, q0.id AS id0, q0.name
    FROM beacon.subscriptions AS s
    INNER JOIN (
        SELECT q.id, q.name
        FROM beacon.queries AS q
        WHERE q.archived_time IS NULL
    ) AS q0 ON s.query_id = q0.id
    WHERE s.archived_time IS NULL
    ORDER BY q0.name, s.id
    LIMIT @p1 OFFSET @p
) AS s0
LEFT JOIN (
    SELECT a.id, a.name
    FROM beacon.ai_actors AS a
    WHERE a.archived_time IS NULL
) AS a0 ON s0.ai_actor_id = a0.id
LEFT JOIN (
    SELECT r4.name, r2.recipients_id, r2.subscriptions_id, r4.id
    FROM beacon.recipient_subscription AS r2
    INNER JOIN (
        SELECT r3.id, r3.archived_time, r3.name
        FROM beacon.recipients AS r3
        WHERE r3.archived_time IS NULL
    ) AS r4 ON r2.recipients_id = r4.id
    WHERE r4.archived_time IS NULL
) AS s1 ON s0.id = s1.subscriptions_id
ORDER BY s0.name, s0.id, s0.id0, a0.id, s1.recipients_id, s1.subscriptions_id