SELECT q.id AS "Id", q.name AS "Name", q.description AS "Description", q.parent_folder_id AS "ParentFolderId", q.path AS "Path", q.sort_order AS "SortOrder", (
    SELECT count(*)::int
    FROM beacon.queries AS q0
    WHERE q0.archived_time IS NULL AND q.id = q0.folder_id) AS "QueryCount", (
    SELECT count(*)::int
    FROM beacon.query_folders AS q1
    WHERE q1.archived_time IS NULL AND q.id = q1.parent_folder_id) AS "ChildFolderCount"
FROM beacon.query_folders AS q
WHERE q.archived_time IS NULL
ORDER BY q.sort_order
;
SELECT count(*)::int
FROM beacon.queries AS q
WHERE q.archived_time IS NULL AND q.folder_id IS NULL
;
SELECT count(*)::int
FROM beacon.queries AS q
WHERE q.archived_time IS NULL