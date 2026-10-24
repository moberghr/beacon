SELECT q0."Date", q0."Hour", q0.notification_status AS "NotificationStatus"
FROM (
    SELECT q.notification_status, date_trunc('day', q.created_time, 'UTC') AS "Date", date_part('hour', q.created_time AT TIME ZONE 'UTC')::int AS "Hour"
    FROM beacon.query_execution_history AS q
    WHERE q.created_time >= @firstBucketStart
) AS q0
GROUP BY q0."Date", q0."Hour", q0.notification_status