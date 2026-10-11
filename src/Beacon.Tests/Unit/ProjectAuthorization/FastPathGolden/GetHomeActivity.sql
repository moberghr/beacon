SELECT q2.name || ' ran successfully', 'scheduled · ' || q0.execution_time_ms::int::text || 'ms', q0.created_time
FROM (
    SELECT q.created_time, q.execution_time_ms, q.subscription_id
    FROM beacon.query_execution_history AS q
    WHERE q.created_time >= @since AND q.notification_status = 2
    ORDER BY q.created_time DESC
    LIMIT @p
) AS q0
INNER JOIN (
    SELECT s.id, s.query_id
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL
) AS s0 ON q0.subscription_id = s0.id
INNER JOIN (
    SELECT q1.id, q1.name
    FROM beacon.queries AS q1
    WHERE q1.archived_time IS NULL
) AS q2 ON s0.query_id = q2.id
ORDER BY q0.created_time DESC
;
SELECT q2.name || ' failed', q0.comment, CASE
    WHEN q0.notification_status = 5 THEN 'execution timed out'
    ELSE 'execution failed'
END, q0.created_time
FROM (
    SELECT q.comment, q.created_time, q.notification_status, q.subscription_id
    FROM beacon.query_execution_history AS q
    WHERE q.created_time >= @since AND q.notification_status IN (7, 5)
    ORDER BY q.created_time DESC
    LIMIT @p
) AS q0
INNER JOIN (
    SELECT s.id, s.query_id
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL
) AS s0 ON q0.subscription_id = s0.id
INNER JOIN (
    SELECT q1.id, q1.name
    FROM beacon.queries AS q1
    WHERE q1.archived_time IS NULL
) AS q2 ON s0.query_id = q2.id
ORDER BY q0.created_time DESC
;
SELECT q2.name || ' was skipped', q0.comment, q0.created_time
FROM (
    SELECT q.comment, q.created_time, q.subscription_id
    FROM beacon.query_execution_history AS q
    WHERE q.created_time >= @since AND q.notification_status = 8
    ORDER BY q.created_time DESC
    LIMIT @p
) AS q0
INNER JOIN (
    SELECT s.id, s.query_id
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL
) AS s0 ON q0.subscription_id = s0.id
INNER JOIN (
    SELECT q1.id, q1.name
    FROM beacon.queries AS q1
    WHERE q1.archived_time IS NULL
) AS q2 ON s0.query_id = q2.id
ORDER BY q0.created_time DESC
;
SELECT 'subscription created for ' || q0.name, COALESCE((
    SELECT count(*)::int
    FROM beacon.recipient_subscription AS r
    INNER JOIN (
        SELECT r0.id
        FROM beacon.recipients AS r0
        WHERE r0.archived_time IS NULL
    ) AS r1 ON r.recipients_id = r1.id
    WHERE s0.id = r.subscriptions_id)::text, '') || ' recipient(s)', s0.created_time
FROM (
    SELECT s.id, s.created_time, s.query_id
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL AND s.created_time >= @since
    ORDER BY s.created_time DESC
    LIMIT @p
) AS s0
INNER JOIN (
    SELECT q.id, q.name
    FROM beacon.queries AS q
    WHERE q.archived_time IS NULL
) AS q0 ON s0.query_id = q0.id
ORDER BY s0.created_time DESC
;
SELECT 'task resolved for ' || q2.name, COALESCE(q0.resolution_notes, 'resolved'), q0.resolved_at
FROM (
    SELECT q.resolution_notes, q.resolved_at, q.subscription_id
    FROM beacon.query_tasks AS q
    WHERE q.archived_time IS NULL AND q.resolved AND q.resolved_at >= @since
    ORDER BY q.resolved_at DESC
    LIMIT @p
) AS q0
INNER JOIN (
    SELECT s.id, s.query_id
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL
) AS s0 ON q0.subscription_id = s0.id
INNER JOIN (
    SELECT q1.id, q1.name
    FROM beacon.queries AS q1
    WHERE q1.archived_time IS NULL
) AS q2 ON s0.query_id = q2.id
ORDER BY q0.resolved_at DESC
;
SELECT 'new task opened for ' || q2.name, q0.created_time
FROM (
    SELECT q.created_time, q.subscription_id
    FROM beacon.query_tasks AS q
    WHERE q.archived_time IS NULL AND NOT (q.resolved) AND q.created_time >= @since
    ORDER BY q.created_time DESC
    LIMIT @p
) AS q0
INNER JOIN (
    SELECT s.id, s.query_id
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL
) AS s0 ON q0.subscription_id = s0.id
INNER JOIN (
    SELECT q1.id, q1.name
    FROM beacon.queries AS q1
    WHERE q1.archived_time IS NULL
) AS q2 ON s0.query_id = q2.id
ORDER BY q0.created_time DESC
;
SELECT CASE
    WHEN a0.severity IN ('Critical', 'High') THEN 'crit'
    ELSE 'warn'
END, 'anomaly detected on ' || q0.name, COALESCE(a0.explanation, 'severity: ' || a0.severity), a0.detected_time
FROM (
    SELECT a.detected_time, a.explanation, a.severity, a.subscription_id
    FROM beacon.anomaly_events AS a
    WHERE a.detected_time >= @since
    ORDER BY a.detected_time DESC
    LIMIT @p
) AS a0
INNER JOIN (
    SELECT s.id, s.query_id
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL
) AS s0 ON a0.subscription_id = s0.id
INNER JOIN (
    SELECT q.id, q.name
    FROM beacon.queries AS q
    WHERE q.archived_time IS NULL
) AS q0 ON s0.query_id = q0.id
ORDER BY a0.detected_time DESC