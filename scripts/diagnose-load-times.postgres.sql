/*
  NodePilot — load-time diagnosis (PostgreSQL)

  Read-only. The PostgreSQL counterpart to diagnose-load-times.sqlserver.sql: how much history
  exists, how much of it a 7- or 30-day dashboard window touches, and whether the server is
  sized to keep that cached.

  Usage:
    psql -h <host> -U <user> -d <database> -f diagnose-load-times.postgres.sql > result.txt
*/

\echo '=== 1. Server version and memory settings ==='
SELECT version();
SELECT name, setting, unit
FROM pg_settings
WHERE name IN ('shared_buffers', 'effective_cache_size', 'work_mem', 'max_connections')
ORDER BY name;

\echo ''
\echo '=== 2. Row counts and size of the tables the dashboard reads ==='
\echo '  StepExecutions is normally the largest: several rows per execution, and it carries the'
\echo '  unbounded error/output columns.'
SELECT
    relname                                        AS table_name,
    n_live_tup                                     AS approx_rows,
    pg_size_pretty(pg_total_relation_size(relid))  AS total_size,
    pg_size_pretty(pg_indexes_size(relid))         AS index_size
FROM pg_stat_user_tables
WHERE relname IN ('WorkflowExecutions', 'StepExecutions', 'Workflows', 'AuditLog', 'SupportEvents')
ORDER BY pg_total_relation_size(relid) DESC;

\echo ''
\echo '=== 3. How much history each dashboard window touches ==='
\echo '  This is the direct answer to "why is 30 days slower than 24 hours".'
SELECT
    COUNT(*)                                                                  AS all_time,
    COUNT(*) FILTER (WHERE "StartedAt" >= NOW() - INTERVAL '24 hours')         AS last_24h,
    COUNT(*) FILTER (WHERE "StartedAt" >= NOW() - INTERVAL '7 days')           AS last_7d,
    COUNT(*) FILTER (WHERE "StartedAt" >= NOW() - INTERVAL '30 days')          AS last_30d,
    MIN("StartedAt")                                                          AS oldest_execution
FROM "WorkflowExecutions";

\echo ''
\echo '  Failed runs drive /api/stats/failure-causes, which groups on an unbounded text column.'
SELECT
    COUNT(*)                                                          AS failed_all_time,
    COUNT(*) FILTER (WHERE "StartedAt" >= NOW() - INTERVAL '7 days')  AS failed_7d,
    COUNT(*) FILTER (WHERE "StartedAt" >= NOW() - INTERVAL '30 days') AS failed_30d
FROM "WorkflowExecutions"
WHERE "Status" = 'Failed';

\echo ''
\echo '  Retried steps drive the dashboard retry ratio. AttemptCount has no index, so this'
\echo '  predicate scans the largest table.'
SELECT COUNT(*) AS step_rows, COUNT(*) FILTER (WHERE "AttemptCount" > 1) AS retried_steps
FROM "StepExecutions";

\echo ''
\echo '=== 4. Workflow count ==='
\echo '  The first workflow list after a restart reads and parses every definition, so this'
\echo '  number and the definition sizes set the cold-start cost.'
SELECT
    COUNT(*)                                       AS workflows,
    pg_size_pretty(SUM(LENGTH("DefinitionJson"))::bigint) AS definitions_total,
    pg_size_pretty(MAX(LENGTH("DefinitionJson"))::bigint) AS largest_definition
FROM "Workflows";

\echo ''
\echo '=== 5. Retention check ==='
\echo '  Executions retention defaults to 30 days. Rows older than that mean retention is off,'
\echo '  behind, or never had a leader to run on.'
SELECT COUNT(*) AS executions_older_than_30d
FROM "WorkflowExecutions"
WHERE "StartedAt" < NOW() - INTERVAL '30 days';
