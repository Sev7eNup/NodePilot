/*
  NodePilot — load-time diagnosis (SQL Server)

  Read-only. Answers the four questions that decide where slow dashboard and list loads come
  from: how much history exists, how much of it a 7- or 30-day window touches, whether the
  server has enough memory to keep it cached, and which edition is running.

  Usage:
    sqlcmd -S <server> -d <database> -E -i diagnose-load-times.sqlserver.sql -o result.txt

  The PostgreSQL equivalent is diagnose-load-times.postgres.sql.
*/

SET NOCOUNT ON;

PRINT '=== 1. Edition and memory ===';
PRINT '  Express caps the buffer pool near 1.4 GB. On a large history that alone keeps every';
PRINT '  dashboard query reading from disk, and no amount of query tuning changes it.';
SELECT
    SERVERPROPERTY('Edition')                AS edition,
    SERVERPROPERTY('ProductVersion')         AS product_version,
    SERVERPROPERTY('IsIntegratedSecurityOnly') AS integrated_security_only;

SELECT
    physical_memory_kb / 1024        AS server_physical_memory_mb,
    committed_target_kb / 1024       AS sql_target_memory_mb,
    committed_kb / 1024              AS sql_committed_memory_mb
FROM sys.dm_os_sys_info;

PRINT '';
PRINT '=== 2. Row counts and size of the tables the dashboard reads ===';
PRINT '  StepExecutions is normally the largest: several rows per execution, and it carries the';
PRINT '  unbounded error/output columns.';
SELECT
    t.name                                   AS table_name,
    SUM(p.rows)                              AS approx_rows,
    SUM(a.total_pages) * 8 / 1024            AS total_mb,
    SUM(a.used_pages)  * 8 / 1024            AS used_mb
FROM sys.tables t
JOIN sys.indexes i      ON i.object_id = t.object_id
JOIN sys.partitions p   ON p.object_id = i.object_id AND p.index_id = i.index_id
JOIN sys.allocation_units a ON a.container_id = p.partition_id
WHERE t.name IN ('WorkflowExecutions', 'StepExecutions', 'Workflows', 'AuditLog', 'SupportEvents')
  AND i.index_id <= 1
GROUP BY t.name
ORDER BY SUM(a.total_pages) DESC;

PRINT '';
PRINT '=== 3. How much history each dashboard window touches ===';
PRINT '  This is the direct answer to "why is 30 days slower than 24 hours".';
SELECT
    'Executions' AS scope,
    COUNT(*)                                                              AS all_time,
    SUM(CASE WHEN StartedAt >= DATEADD(hour, -24, GETUTCDATE()) THEN 1 ELSE 0 END) AS last_24h,
    SUM(CASE WHEN StartedAt >= DATEADD(day,   -7, GETUTCDATE()) THEN 1 ELSE 0 END) AS last_7d,
    SUM(CASE WHEN StartedAt >= DATEADD(day,  -30, GETUTCDATE()) THEN 1 ELSE 0 END) AS last_30d,
    MIN(StartedAt)                                                        AS oldest_execution
FROM WorkflowExecutions;

PRINT '';
PRINT '  Failed runs drive /api/stats/failure-causes, which groups on an unbounded text column.';
SELECT
    COUNT(*)                                                              AS failed_all_time,
    SUM(CASE WHEN StartedAt >= DATEADD(day,  -7, GETUTCDATE()) THEN 1 ELSE 0 END) AS failed_7d,
    SUM(CASE WHEN StartedAt >= DATEADD(day, -30, GETUTCDATE()) THEN 1 ELSE 0 END) AS failed_30d
FROM WorkflowExecutions
WHERE Status = 'Failed';

PRINT '';
PRINT '  Retried steps drive the dashboard retry ratio. AttemptCount has no index, so this';
PRINT '  predicate scans the largest table.';
SELECT COUNT(*) AS step_rows, SUM(CASE WHEN AttemptCount > 1 THEN 1 ELSE 0 END) AS retried_steps
FROM StepExecutions;

PRINT '';
PRINT '=== 4. Workflow count ===';
PRINT '  The first workflow list after a restart reads and parses every definition, so this';
PRINT '  number and the definition sizes set the cold-start cost.';
SELECT
    COUNT(*)                        AS workflows,
    SUM(LEN(DefinitionJson)) / 1024 AS definitions_total_kb,
    MAX(LEN(DefinitionJson)) / 1024 AS largest_definition_kb
FROM Workflows;

PRINT '';
PRINT '=== 5. Retention check ===';
PRINT '  Executions retention defaults to 30 days. Rows older than that mean retention is off,';
PRINT '  behind, or never had a leader to run on.';
SELECT COUNT(*) AS executions_older_than_30d
FROM WorkflowExecutions
WHERE StartedAt < DATEADD(day, -30, GETUTCDATE());
