SELECT count(*)::int
FROM beacon.query_execution_history AS q
;
SELECT q0.id, s.id, q1.id, a.id, s0."Id", s0."Created", s0."NotificationType", s0."RecipientName", s0."SentAt", s0.id0, q0.result_count, q0.created_time, q0.notification_status, q1.name, q0.subscription_id, s.archived_time IS NOT NULL OR q1.archived_time IS NOT NULL, q0.execution_time_ms, q0.comment, s.ai_actor_id, a.name
FROM (
    SELECT q.id, q.comment, q.created_time, q.execution_time_ms, q.notification_status, q.result_count, q.subscription_id
    FROM beacon.query_execution_history AS q
    ORDER BY q.created_time DESC, q.id
    LIMIT @p1 OFFSET @p
) AS q0
INNER JOIN beacon.subscriptions AS s ON q0.subscription_id = s.id
INNER JOIN beacon.queries AS q1 ON s.query_id = q1.id
LEFT JOIN beacon.ai_actors AS a ON s.ai_actor_id = a.id
LEFT JOIN (
    SELECT n.id AS "Id", n.created_time AS "Created", n.type AS "NotificationType", r.name AS "RecipientName", n.sent_at AS "SentAt", r.id AS id0, n.query_execution_history_id
    FROM beacon.notifications AS n
    INNER JOIN beacon.recipients AS r ON n.recipient_id = r.id
) AS s0 ON q0.id = s0.query_execution_history_id
ORDER BY q0.created_time DESC, q0.id, s.id, q1.id, a.id, s0."Id"