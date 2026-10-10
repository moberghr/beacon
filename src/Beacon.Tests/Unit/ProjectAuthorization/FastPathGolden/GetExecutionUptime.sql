SELECT q.created_time AS "CreatedTime", q.notification_status AS "NotificationStatus"
FROM beacon.query_execution_history AS q
WHERE q.created_time >= @firstBucketStart