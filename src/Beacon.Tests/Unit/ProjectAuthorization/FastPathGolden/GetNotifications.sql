SELECT count(*)::int
FROM beacon.query_execution_history AS q
;
SELECT q0.id, s0.id, q2.id, a0.id, s1."Id", s1."Created", s1."NotificationType", s1."RecipientName", s1."SentAt", s1.id0, q0.result_count, q0.created_time, q0.notification_status, q2.name, q0.subscription_id, q0.execution_time_ms, q0.comment, s0.ai_actor_id, a0.name
FROM (
    SELECT q.id, q.comment, q.created_time, q.execution_time_ms, q.notification_status, q.result_count, q.subscription_id
    FROM beacon.query_execution_history AS q
    ORDER BY q.created_time DESC, q.id
    LIMIT @p1 OFFSET @p
) AS q0
INNER JOIN (
    SELECT s.id, s.ai_actor_id, s.query_id
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL
) AS s0 ON q0.subscription_id = s0.id
INNER JOIN (
    SELECT q1.id, q1.name
    FROM beacon.queries AS q1
    WHERE q1.archived_time IS NULL
) AS q2 ON s0.query_id = q2.id
LEFT JOIN (
    SELECT a.id, a.name
    FROM beacon.ai_actors AS a
    WHERE a.archived_time IS NULL
) AS a0 ON s0.ai_actor_id = a0.id
LEFT JOIN (
    SELECT n.id AS "Id", n.created_time AS "Created", n.type AS "NotificationType", r0.name AS "RecipientName", n.sent_at AS "SentAt", r0.id AS id0, n.query_execution_history_id
    FROM beacon.notifications AS n
    INNER JOIN (
        SELECT r.id, r.name
        FROM beacon.recipients AS r
        WHERE r.archived_time IS NULL
    ) AS r0 ON n.recipient_id = r0.id
) AS s1 ON q0.id = s1.query_execution_history_id
ORDER BY q0.created_time DESC, q0.id, s0.id, q2.id, a0.id, s1."Id"