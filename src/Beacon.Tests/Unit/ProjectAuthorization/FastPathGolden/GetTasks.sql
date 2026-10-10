SELECT count(*)::int
FROM beacon.query_tasks AS q
WHERE q.archived_time IS NULL
;
SELECT q0.id AS "Id", q2.name AS "SubscriptionName", q0.latest_result_count AS "LatestResultCount", q0.last_notification_at AS "LastNotificationAt", (
    SELECT count(*)::int
    FROM beacon.notifications AS n
    WHERE q0.id = n.task_id) AS "NotificationCount", q0.created_time AS "CreatedAt", q0.resolved AS "Resolved", q0.resolved_at AS "ResolvedAt", CASE
    WHEN q0.resolved_by_user_id IS NULL THEN NULL
    ELSE (
        SELECT COALESCE(u.display_name, u.user_name)
        FROM beacon.users AS u
        WHERE u.archived_time IS NULL AND u.external_id = q0.resolved_by_user_id
        LIMIT 1)
END AS "ResolvedByUserName", s0.ai_actor_id AS "AiActorId", a0.name AS "AiActorName", (
    SELECT count(*)::int
    FROM beacon.query_execution_history AS q3
    WHERE q3.subscription_id = q0.subscription_id AND q3.created_time >= q0.created_time) AS "ExecutionCount", (
    SELECT count(*)::int
    FROM (
        SELECT DISTINCT q4.result_count
        FROM beacon.query_execution_history AS q4
        WHERE q4.subscription_id = q0.subscription_id AND q4.created_time >= q0.created_time
    ) AS q5) AS "UniqueResultCounts"
FROM (
    SELECT q.id, q.created_time, q.last_notification_at, q.latest_result_count, q.resolved, q.resolved_at, q.resolved_by_user_id, q.subscription_id
    FROM beacon.query_tasks AS q
    WHERE q.archived_time IS NULL
    ORDER BY q.created_time DESC, q.id
    LIMIT @p4 OFFSET @p
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
ORDER BY q0.created_time DESC, q0.id