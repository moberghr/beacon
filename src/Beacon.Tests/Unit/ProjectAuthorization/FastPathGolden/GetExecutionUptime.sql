SELECT q0."HourStart", q0.notification_status AS "NotificationStatus"
FROM (
    SELECT q.notification_status, date_trunc('hour', timezone('UTC', q.created_time)) AS "HourStart"
    FROM beacon.query_execution_history AS q
    WHERE q.created_time >= @firstBucketStart
) AS q0
GROUP BY q0."HourStart", q0.notification_status