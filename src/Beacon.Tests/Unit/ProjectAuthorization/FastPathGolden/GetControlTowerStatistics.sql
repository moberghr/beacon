SELECT s.id AS "Id", (
    SELECT count(*)::int
    FROM beacon.query_execution_history AS q
    WHERE s.id = q.subscription_id AND q.created_time >= @windowStart) AS "TotalExecutions", (
    SELECT count(*)::int
    FROM beacon.query_execution_history AS q0
    WHERE s.id = q0.subscription_id AND q0.created_time >= @windowStart AND q0.notification_status IN (2, 4, 3)) AS "SuccessfulExecutions", s.created_time AS "SubscriptionCreatedTime", (
    SELECT count(*)::int
    FROM beacon.query_tasks AS q1
    WHERE q1.archived_time IS NULL AND q1.subscription_id = s.id AND NOT (q1.resolved)) AS "UnresolvedTaskCount"
FROM beacon.subscriptions AS s
WHERE s.archived_time IS NULL
;
SELECT count(*)::int
FROM beacon.data_contracts AS d
WHERE d.archived_time IS NULL AND d.is_enabled AND d.owner_not_authorized_since IS NOT NULL