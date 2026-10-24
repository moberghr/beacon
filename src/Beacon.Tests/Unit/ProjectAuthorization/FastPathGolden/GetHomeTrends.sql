SELECT count(*)::int
FROM beacon.subscriptions AS s
WHERE s.archived_time IS NULL
;
SELECT count(*)::int
FROM beacon.subscriptions AS s
WHERE s.archived_time IS NULL AND s.created_time <= @windowStart
;
SELECT s0."Key" AS "Date", count(*)::int AS "Count"
FROM (
    SELECT date_trunc('day', s.created_time, 'UTC') AS "Key"
    FROM beacon.subscriptions AS s
    WHERE s.archived_time IS NULL AND s.created_time >= @AddDays
) AS s0
GROUP BY s0."Key"
;
SELECT q0."Date", q0.execution_time_ms AS "Ms", count(*)::int AS "Count"
FROM (
    SELECT q.execution_time_ms, date_trunc('day', q.created_time, 'UTC') AS "Date"
    FROM beacon.query_execution_history AS q
    WHERE q.created_time >= @windowStart
) AS q0
GROUP BY q0."Date", q0.execution_time_ms
;
SELECT count(*)::int
FROM beacon.query_execution_history AS q
WHERE q.created_time >= @priorStart AND q.created_time < @windowStart
;
SELECT q0."Key" AS "Date", count(*)::int AS "Count"
FROM (
    SELECT date_trunc('day', q.created_time, 'UTC') AS "Key"
    FROM beacon.query_execution_history AS q
    WHERE q.created_time >= @AddDays
) AS q0
GROUP BY q0."Key"
;
SELECT avg(q.execution_time_ms)
FROM beacon.query_execution_history AS q
WHERE q.created_time >= @priorStart AND q.created_time < @windowStart
;
SELECT q.subscription_id AS "SubscriptionId", avg(q.execution_time_ms) AS "AvgMs"
FROM beacon.query_execution_history AS q
WHERE q.created_time >= @windowStart
GROUP BY q.subscription_id
;
SELECT n0."Key" AS "Date", count(*)::int AS "Count"
FROM (
    SELECT date_trunc('day', n.sent_at, 'UTC') AS "Key"
    FROM beacon.notifications AS n
    WHERE n.sent_at >= @windowStart
) AS n0
GROUP BY n0."Key"
;
SELECT count(*)::int
FROM beacon.notifications AS n
WHERE n.sent_at >= @priorStart AND n.sent_at < @windowStart
;
SELECT n0."Key" AS "Date", count(*)::int AS "Count"
FROM (
    SELECT date_trunc('day', n.sent_at, 'UTC') AS "Key"
    FROM beacon.notifications AS n
    WHERE n.sent_at >= @AddDays
) AS n0
GROUP BY n0."Key"
;
SELECT count(*)::int
FROM beacon.anomaly_events AS a
WHERE NOT (a.acknowledged)
;
SELECT count(*)::int
FROM beacon.anomaly_events AS a
WHERE a.acknowledged AND a.detected_time >= @windowStart
;
SELECT count(*)::int
FROM beacon.anomaly_events AS a
WHERE a.detected_time >= @priorStart AND a.detected_time < @windowStart
;
SELECT count(*)::int
FROM beacon.anomaly_events AS a
WHERE a.detected_time >= @windowStart
;
SELECT a0."Key" AS "Date", count(*)::int AS "Count"
FROM (
    SELECT date_trunc('day', a.detected_time, 'UTC') AS "Key"
    FROM beacon.anomaly_events AS a
    WHERE a.detected_time >= @AddDays
) AS a0
GROUP BY a0."Key"
;
SELECT count(*)::int
FROM beacon.data_sources AS d
WHERE d.archived_time IS NULL
;
SELECT count(*)::int
FROM beacon.recipients AS r
WHERE r.archived_time IS NULL
;
SELECT count(*)::int
FROM (
    SELECT DISTINCT r.notification_type::int AS c
    FROM beacon.recipients AS r
    WHERE r.archived_time IS NULL
) AS r0