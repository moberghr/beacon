SELECT count(*)::int
FROM beacon.query_step_change_history AS q
INNER JOIN beacon.query_steps AS q0 ON q.query_step_id = q0.id
WHERE q0.query_id = @request_QueryId
;
SELECT s.id AS "Id", s.query_step_id AS "QueryStepId", s.name AS "QueryStepName", s.step_order AS "QueryStepOrder", s.ai_actor_id AS "AiActorId", a0.name AS "AiActorName", s.ai_actor_execution_id AS "AiActorExecutionId", s.ai_actor_plan_id AS "AiActorPlanId", s.user_id AS "UserId", s.previous_sql AS "PreviousSql", s.new_sql AS "NewSql", s.change_reason AS "ChangeReason", s.change_source AS "ChangeSource", s.changed_at AS "ChangedAt"
FROM (
    SELECT q.id, q.ai_actor_execution_id, q.ai_actor_id, q.ai_actor_plan_id, q.change_reason, q.change_source, q.changed_at, q.new_sql, q.previous_sql, q.query_step_id, q.user_id, q0.name, q0.step_order
    FROM beacon.query_step_change_history AS q
    INNER JOIN beacon.query_steps AS q0 ON q.query_step_id = q0.id
    WHERE q0.query_id = @request_QueryId
    ORDER BY q.changed_at DESC, q.id
    LIMIT @p2 OFFSET @p
) AS s
LEFT JOIN (
    SELECT a.id, a.name
    FROM beacon.ai_actors AS a
    WHERE a.archived_time IS NULL
) AS a0 ON s.ai_actor_id = a0.id
ORDER BY s.changed_at DESC, s.id