using System.Security.Claims;
using System.Security.Cryptography;
using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Security;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin-only generic database viewer and cell-editor.
/// Exposes all EF-tracked entity types for browse + inline-edit + row-delete.
///
/// Security stance:
/// - Only Admin role can access any endpoint.
/// - Entity-level capabilities (canUpdate, canDelete) are enforced server-side.
/// - Hidden columns (PasswordHash, EncryptedPassword) are never returned.
/// - Masked columns (GlobalVariable.Value for secrets) are returned as "***".
/// - PK columns and read-only columns reject PATCH.
/// - User entity enforces last-admin guard and self-demote block.
/// - Audit entries are committed in the SAME SaveChangesAsync as the mutation (atomic).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class DbAdminController : ControllerBase
{
    private const int MaxTake = 200;
    private const int MaxSqlLength = DbAdminQueryExecutor.MaxSqlLength;
    private const string WriteConfirmHeader = "X-Confirm-Write";
    private const string WriteConfirmValue = "ALLOW";

    private readonly NodePilotDbContext _db;
    private readonly DbAdminMetadataService _meta;
    private readonly DbAdminQueryExecutor _executor;
    private readonly IAuditStager _stager;
    private readonly IMemoryCache _userStateCache;
    private readonly ILogger<DbAdminController> _logger;

    public DbAdminController(NodePilotDbContext db, DbAdminMetadataService meta,
        DbAdminQueryExecutor executor,
        IAuditStager stager,
        IMemoryCache userStateCache, ILogger<DbAdminController> logger)
    {
        _db = db;
        _meta = meta;
        _executor = executor;
        _stager = stager;
        _userStateCache = userStateCache;
        _logger = logger;
    }

    /// <summary>
    /// Returns schema metadata for all entity types including capabilities,
    /// column info, and cascade-delete targets.
    /// </summary>
    [HttpGet("tables")]
    public async Task<ActionResult<List<DbAdminTableInfo>>> GetTables(CancellationToken ct)
    {
        var result = new List<DbAdminTableInfo>();

        foreach (var table in _meta.GetAllTables().OrderBy(t => t.Name))
        {
            var count = await CountRowsAsync(table.EntityType.ClrType, ct);

            result.Add(new DbAdminTableInfo(
                Name: table.Name,
                DisplayName: ToDisplayName(table.Name),
                DbTableName: table.DbTableName,
                PkColumns: table.PkColumns,
                Capabilities: new DbAdminCapabilities(
                    table.Capabilities.CanUpdate,
                    table.Capabilities.CanDelete),
                Columns: table.Columns
                    .Where(c => !c.IsHidden)
                    .Select(c => new DbAdminColumnInfo(
                        Name: c.Name,
                        ClrType: FriendlyTypeName(c.ClrType, c.IsNullable),
                        IsNullable: c.IsNullable,
                        MaxLength: c.MaxLength,
                        IsPrimaryKey: c.IsPrimaryKey,
                        IsMasked: c.Name == "Value" && table.Name == "GlobalVariable",
                        IsReadOnly: c.IsReadOnly))
                    .ToList(),
                RowCount: count,
                CascadeDeletesTo: table.CascadeDeletesTo
            ));
        }

        return Ok(result);
    }

    /// <summary>
    /// Returns a paginated page of rows for the given entity type.
    /// Hidden columns are excluded; masked columns show "***".
    /// </summary>
    [HttpGet("tables/{name}/rows")]
    public async Task<ActionResult<DbAdminRowsResponse>> GetRows(
        string name,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        [FromQuery] string? orderBy = null,
        [FromQuery] bool desc = false,
        CancellationToken ct = default)
    {
        var table = _meta.GetTable(name);
        if (table is null) return NotFound(new { message = $"Unknown table '{name}'" });

        take = Math.Clamp(take, 1, MaxTake);
        skip = Math.Max(skip, 0);

        var (total, rows) = await DbAdminQueryBuilder.QueryAsync(_db, table, skip, take, orderBy, desc, ct);
        await WriteRowsViewedAuditAsync(name, skip, take, orderBy, desc, total, rows.Count, ct);
        return Ok(new DbAdminRowsResponse(total, rows));
    }

    /// <summary>
    /// Updates a single column in a single row. Commits audit entry atomically
    /// in the same transaction as the mutation.
    /// </summary>
    [HttpPatch("tables/{name}/rows")]
    public async Task<IActionResult> PatchRow(
        string name,
        [FromQuery] string[] pk,
        [FromBody] DbAdminPatchRequest req,
        CancellationToken ct)
    {
        // AuditLog.Add is staged atomically by PatchRowCore inside the retryable transaction.
        var table = _meta.GetTable(name);
        if (table?.EntityType.ClrType != typeof(User))
            return await PatchRowCore(name, pk, req, ct);

        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        AuditLogEntry? committedAudit = null;
        IActionResult result;
        try
        {
            result = await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                committedAudit = null;
                await using var transaction = await _db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, ct);
                await AdminAccountMutationGate.AcquireTransactionLockAsync(_db, ct);
                var attemptResult = await PatchRowCore(
                    name, pk, req, ct, audit => committedAudit = audit);
                if (attemptResult is NoContentResult)
                    await transaction.CommitAsync(ct);
                else
                    await transaction.RollbackAsync(ct);
                return attemptResult;
            });
        }
        catch (DbUpdateException ex)
        {
            var correlationId = HttpContext.TraceIdentifier;
            _logger.LogWarning(ex,
                "DbAdmin UpdateCell constraint violation on {Table} pk={Pk} column={Column} correlationId={CorrelationId}",
                name, string.Join(";", pk), req.Column, correlationId);
            return Conflict(new
            {
                code = "constraint_violation",
                message = "Update rejected by database constraints.",
                correlationId,
            });
        }
        if (committedAudit is not null)
            AuditEventForwarder.ForwardCommitted(_logger, committedAudit);
        return result;
    }

    private async Task<IActionResult> PatchRowCore(
        string name,
        string[] pk,
        DbAdminPatchRequest req,
        CancellationToken ct,
        Action<AuditLogEntry>? deferAuditForward = null)
    {
        var table = _meta.GetTable(name);
        if (table is null) return NotFound(new { message = $"Unknown table '{name}'" });
        if (!table.Capabilities.CanUpdate) return StatusCode(405, new { message = $"Table '{name}' is read-only." });

        // Column validation
        var colMeta = table.Columns.FirstOrDefault(c =>
            string.Equals(c.Name, req.Column, StringComparison.OrdinalIgnoreCase));
        if (colMeta is null) return BadRequest(new { code = "unknown_column", message = $"Column '{req.Column}' not found." });
        if (colMeta.IsHidden) return BadRequest(new { code = "readonly_column", message = $"Column '{req.Column}' is hidden." });
        if (colMeta.IsReadOnly) return BadRequest(new { code = "readonly_column", message = $"Column '{req.Column}' is read-only." });

        // Check for GlobalVariable.Value mask (always read-only regardless of IsSecret)
        var restriction = DbAdminPolicy.GetColumnRestriction(name, req.Column);
        if (restriction.IsReadOnly) return BadRequest(new { code = "readonly_column", message = $"Column '{req.Column}' is read-only." });

        // PK validation
        if (pk.Length != table.PkColumns.Count)
            return BadRequest(new { code = "invalid_pk", message = $"Expected {table.PkColumns.Count} PK value(s)." });

        var entity = await DbAdminQueryBuilder.FindByPkAsync(_db, table, pk, ct);
        if (entity is null) return NotFound(new { message = "Row not found." });

        // Coerce the new value
        object? coercedValue;
        try
        {
            coercedValue = DbAdminQueryBuilder.CoerceJsonValue(req.Value, colMeta.ClrType);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { code = "invalid_value", message = ex.Message });
        }

        // Entity-specific guards. User mutations arrive here inside the retryable,
        // transaction-scoped invariant gate established by PatchRow.
        if (entity is User user)
        {
            var callerId = GetCallerId();
            if (callerId is null) return Unauthorized();
            var guard = await DbAdminPolicy.PreUpdateUserGuardAsync(user, req.Column, coercedValue, _db, callerId.Value, ct);
            if (guard.IsBlocked) return BadRequest(new { code = guard.Code, message = guard.Message });
        }

            var entry = _db.Entry(entity);
            var oldValue = entry.Property(req.Column).CurrentValue;
            var invalidatesUserSessions = entity is User
                && IsUserSessionInvalidatingColumn(req.Column)
                && !Equals(oldValue, coercedValue);
            var reactivatesExternalUser = entity is User externalUser
                && string.Equals(req.Column, nameof(NodePilot.Core.Models.User.IsActive), StringComparison.OrdinalIgnoreCase)
                && oldValue is false
                && coercedValue is true
                && externalUser.Provider != NodePilot.Core.Enums.AuthProvider.Local;
            entry.Property(req.Column).CurrentValue = coercedValue;
            if (invalidatesUserSessions && entity is User changedUser)
                UserSessionInvalidation.BumpSecurityStamp(changedUser);
            if (reactivatesExternalUser && entity is User reactivatedUser)
            {
                // DbAdmin is an administrative bypass around the normal user endpoint.
                // Reactivation must not make a still-fresh pre-tombstone membership snapshot
                // authoritative again; require the provider to publish/authenticate a new one.
                reactivatedUser.LastDirectorySyncAt = null;
                reactivatedUser.DirectorySyncStatus = "ReactivationReauthRequired";
                _db.DirectoryMemberships.RemoveRange(await _db.DirectoryMemberships
                    .Where(membership => membership.UserId == reactivatedUser.Id)
                    .ToListAsync(ct));
                var sessions = await _db.AuthSessions
                    .Where(session => session.UserId == reactivatedUser.Id && session.RevokedAt == null)
                    .ToListAsync(ct);
                foreach (var session in sessions)
                    session.RevokedAt = DateTime.UtcNow;
            }

            // Audit entry attached to the SAME DbContext — committed in one SaveChangesAsync
            // so the row mutation and the audit row are atomic. Routes through IAuditStager
            // (same as every other audit-write path) so redaction + 4 KiB cap apply uniformly.
            var pkDisplay = string.Join(";", pk);
            var auditActor = new AuditActor(GetCallerId(), User.FindFirstValue(ClaimTypes.Name),
                HttpContext?.Connection?.RemoteIpAddress?.ToString());
            var auditEntry = _stager.Build(
                action: AuditActions.DbAdminRowUpdated,
                actor: auditActor,
                resourceType: name,
                resourceId: TryParseGuid(pk.Length == 1 ? pk[0] : null),
                details: AuditDetails.Json(
                    ("table", name),
                    ("pk", pkDisplay),
                    ("column", req.Column),
                    ("oldValue", SafeRedact(oldValue)),
                    ("newValue", SafeRedact(coercedValue))));
            _db.AuditLog.Add(auditEntry);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (_db.Database.CurrentTransaction is null)
            {
                // L-3 (security audit 2026-05-15): never echo provider error text to the client —
                // it leaks schema, index, and constraint details that a compromised Admin (or
                // a lower-trust Admin once RBAC stage B lands) can mine to map the database.
                // The correlation id lets an operator grep server logs for the matching detail.
                var correlationId = HttpContext.TraceIdentifier;
                _logger.LogWarning(ex, "DbAdmin UpdateCell constraint violation on {Table} pk={Pk} column={Column} correlationId={CorrelationId}",
                    name, string.Join(";", pk), req.Column, correlationId);
                return Conflict(new { code = "constraint_violation", message = "Update rejected by database constraints.", correlationId });
            }

            if (invalidatesUserSessions && entity is User savedUser)
                UserSessionInvalidation.InvalidateUserStateCache(_userStateCache, savedUser.Id);
            if (deferAuditForward is null)
                AuditEventForwarder.ForwardCommitted(_logger, auditEntry);
            else
                deferAuditForward(auditEntry);

        return NoContent();
    }

    /// <summary>
    /// Deletes a single row. Commits audit entry atomically in the same transaction.
    /// </summary>
    [HttpDelete("tables/{name}/rows")]
    public async Task<IActionResult> DeleteRow(
        string name,
        [FromQuery] string[] pk,
        CancellationToken ct)
    {
        // AuditLog.Add is staged atomically by DeleteRowCore inside the retryable transaction.
        var table = _meta.GetTable(name);
        if (table?.EntityType.ClrType != typeof(User))
            return await DeleteRowCore(name, pk, ct);

        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        AuditLogEntry? committedAudit = null;
        IActionResult result;
        try
        {
            result = await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                committedAudit = null;
                await using var transaction = await _db.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, ct);
                await AdminAccountMutationGate.AcquireTransactionLockAsync(_db, ct);
                var attemptResult = await DeleteRowCore(
                    name, pk, ct, audit => committedAudit = audit);
                if (attemptResult is NoContentResult)
                    await transaction.CommitAsync(ct);
                else
                    await transaction.RollbackAsync(ct);
                return attemptResult;
            });
        }
        catch (DbUpdateException ex)
        {
            var correlationId = HttpContext.TraceIdentifier;
            _logger.LogWarning(ex,
                "DbAdmin DeleteRow constraint violation on {Table} pk={Pk} correlationId={CorrelationId}",
                name, string.Join(";", pk), correlationId);
            return Conflict(new
            {
                code = "constraint_violation",
                message = "Delete rejected by database constraints (likely referenced by another row).",
                correlationId,
            });
        }
        if (committedAudit is not null)
            AuditEventForwarder.ForwardCommitted(_logger, committedAudit);
        return result;
    }

    private async Task<IActionResult> DeleteRowCore(
        string name,
        string[] pk,
        CancellationToken ct,
        Action<AuditLogEntry>? deferAuditForward = null)
    {
        var table = _meta.GetTable(name);
        if (table is null) return NotFound(new { message = $"Unknown table '{name}'" });
        if (!table.Capabilities.CanDelete) return StatusCode(405, new { message = $"Table '{name}' does not allow row deletion." });

        if (pk.Length != table.PkColumns.Count)
            return BadRequest(new { code = "invalid_pk", message = $"Expected {table.PkColumns.Count} PK value(s)." });

        var entity = await DbAdminQueryBuilder.FindByPkAsync(_db, table, pk, ct);
        if (entity is null) return NotFound(new { message = "Row not found." });

        // Entity-specific guards. User mutations arrive here inside the retryable,
        // transaction-scoped invariant gate established by DeleteRow.
        if (entity is User user)
        {
            var callerId = GetCallerId();
            if (callerId is null) return Unauthorized();
            var guard = await DbAdminPolicy.PreDeleteUserGuardAsync(user, _db, callerId.Value, ct);
            if (guard.IsBlocked) return BadRequest(new { code = guard.Code, message = guard.Message });
        }

            var deletedUserId = entity is User deletedUser ? deletedUser.Id : (Guid?)null;
            _db.Remove(entity);

            var pkDisplay = string.Join(";", pk);
            var auditActor = new AuditActor(GetCallerId(), User.FindFirstValue(ClaimTypes.Name),
                HttpContext?.Connection?.RemoteIpAddress?.ToString());
            var auditEntry = _stager.Build(
                action: AuditActions.DbAdminRowDeleted,
                actor: auditActor,
                resourceType: name,
                resourceId: TryParseGuid(pk.Length == 1 ? pk[0] : null),
                details: AuditDetails.Json(("table", name), ("pk", pkDisplay)));
            _db.AuditLog.Add(auditEntry);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (_db.Database.CurrentTransaction is null)
            {
                // L-3 (security audit 2026-05-15): generic conflict response — see UpdateCell.
                var correlationId = HttpContext.TraceIdentifier;
                _logger.LogWarning(ex, "DbAdmin DeleteRow constraint violation on {Table} pk={Pk} correlationId={CorrelationId}",
                    name, string.Join(";", pk), correlationId);
                return Conflict(new { code = "constraint_violation", message = "Delete rejected by database constraints (likely referenced by another row).", correlationId });
            }

            if (deletedUserId is { } id)
                UserSessionInvalidation.InvalidateUserStateCache(_userStateCache, id);
            if (deferAuditForward is null)
                AuditEventForwarder.ForwardCommitted(_logger, auditEntry);
            else
                deferAuditForward(auditEntry);

        return NoContent();
    }

    /// <summary>
    /// Returns capabilities + active provider so the UI can decide whether to show the
    /// write-mode toggle and which provider-badge to render.
    /// </summary>
    [HttpGet("info")]
    public ActionResult<DbAdminInfoResponse> GetInfo()
    {
        var opts = _executor.Options;
        return Ok(new DbAdminInfoResponse(
            Provider: _executor.Provider,
            AllowWriteQueries: opts.AllowWriteQueries,
            QueryTimeoutSeconds: Math.Clamp(opts.QueryTimeoutSeconds, 1, 600),
            QueryMaxRows: Math.Max(1, opts.QueryMaxRows)));
    }

    /// <summary>
    /// Executes an ad-hoc SQL statement against the active database. Read-mode is the
    /// default; write-mode is gated by both a server-side config flag AND a per-request
    /// confirmation header. Every call is audited with the statement text.
    /// </summary>
    [HttpPost("query")]
    public async Task<IActionResult> ExecuteQuery([FromBody] DbAdminQueryRequest req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Sql))
            return BadRequest(new DbAdminQueryError("empty_sql", "SQL statement is required.", null));

        if (req.Sql.Length > MaxSqlLength)
            return BadRequest(new DbAdminQueryError("sql_too_long",
                $"SQL exceeds the {MaxSqlLength}-byte limit.", null));

        var mode = string.Equals(req.Mode, "write", StringComparison.OrdinalIgnoreCase) ? "write" : "read";

        // Keyword whitelist — defence-in-depth on top of the read-only transaction.
        var keyword = DbAdminQueryExecutor.FirstKeyword(req.Sql);
        if (keyword is null)
            return BadRequest(new DbAdminQueryError("no_keyword",
                "Could not detect a SQL keyword in the input.", null));

        if (mode == "read" && !DbAdminQueryExecutor.IsReadOnlyKeyword(keyword))
            return BadRequest(new DbAdminQueryError("non_readonly_statement",
                $"Statement starts with '{keyword.ToUpperInvariant()}' which is not allowed in read mode. " +
                "Switch to write mode to execute mutations.", null));

        if (mode == "read" && DbAdminQueryExecutor.ContainsMultipleStatements(req.Sql))
            return BadRequest(new DbAdminQueryError("multiple_statements_not_allowed",
                "Read mode accepts exactly one SQL statement. Switch to write mode to execute batches.", null));

        if (mode == "write")
        {
            if (!_executor.Options.AllowWriteQueries)
                return StatusCode(StatusCodes.Status403Forbidden, new DbAdminQueryError(
                    "write_disabled",
                    "Write queries are disabled. Set DbAdmin:AllowWriteQueries=true in configuration to enable.",
                    null));

            // The header is the deliberate "I know what I'm doing" gesture from the UI. Curl-only
            // operators have to set it explicitly — which is exactly the friction we want for a
            // statement that mutates arbitrary application data. AuditLog storage is blocked below.
            if (!Request.Headers.TryGetValue(WriteConfirmHeader, out var confirmHeader)
                || !string.Equals(confirmHeader.ToString(), WriteConfirmValue, StringComparison.Ordinal))
                return BadRequest(new DbAdminQueryError("missing_confirmation",
                    $"Write mode requires the '{WriteConfirmHeader}: {WriteConfirmValue}' header.", null));

            if (DbAdminQueryExecutor.ReferencesProtectedAuditStorage(req.Sql))
            {
                await WriteQueryAuditAsync(
                    AuditActions.DbAdminSqlWrite,
                    mode,
                    req.Sql,
                    success: false,
                    rowsAffected: null,
                    reason: "protected_audit_storage",
                    ct);
                return BadRequest(new DbAdminQueryError(
                    "protected_audit_storage",
                    "Write-mode cannot target NodePilot audit storage.",
                    null));
            }

            // Write-mode is the one fail-closed exception to the general best-effort audit
            // policy. If the durable + forwarded attempt cannot be recorded, executing
            // arbitrary SQL would create a change with no surviving evidence.
            var attemptRecorded = await WriteQueryAuditAsync(
                AuditActions.DbAdminSqlWriteAttempted,
                mode,
                req.Sql,
                success: null,
                rowsAffected: null,
                reason: null,
                ct);
            if (!attemptRecorded)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new DbAdminQueryError(
                    "audit_unavailable",
                    "Write query was not executed because its audit attempt could not be persisted.",
                    HttpContext.TraceIdentifier));
            }
        }

        DbAdminQueryResult result;
        try
        {
            result = mode == "write"
                ? await _executor.ExecuteWriteAsync(req.Sql, ct)
                : await _executor.ExecuteReadAsync(req.Sql, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Provider-error text leaks schema/index/constraint details. For read-mode we keep a
            // hint (operators need to debug typos), for write-mode we go fully generic + correlation
            // ID. This mirrors L-3 in the 2026-05-15 security audit.
            var correlationId = HttpContext.TraceIdentifier;
            _logger.LogWarning(ex,
                "DbAdmin query failed mode={Mode} correlationId={CorrelationId} sql={SqlPreview}",
                mode, correlationId, Preview(req.Sql));

            // Audit failures too — failed write attempts are interesting.
            await WriteQueryAuditAsync(
                mode == "write" ? AuditActions.DbAdminSqlWrite : AuditActions.DbAdminSqlExecuted,
                mode,
                req.Sql,
                success: false,
                rowsAffected: null,
                reason: "execution_failed",
                ct);

            var message = mode == "write"
                ? "Statement rejected by the database."
                : SanitiseReadError(ex.Message);
            return Conflict(new DbAdminQueryError("execution_failed", message, correlationId));
        }

        await WriteQueryAuditAsync(
            mode == "write" ? AuditActions.DbAdminSqlWrite : AuditActions.DbAdminSqlExecuted,
            mode,
            req.Sql,
            success: true,
            rowsAffected: result.RowsAffected,
            reason: null,
            ct);

        return Ok(new DbAdminQueryResponse(
            Columns: result.Columns,
            Rows: result.Rows,
            RowsAffected: result.RowsAffected,
            DurationMs: result.DurationMs,
            Truncated: result.Truncated,
            Mode: result.Mode));
    }

    private async Task<bool> WriteQueryAuditAsync(
        string action,
        string mode,
        string sql,
        bool? success,
        int? rowsAffected,
        string? reason,
        CancellationToken ct)
    {
        var auditActor = new AuditActor(GetCallerId(), User.FindFirstValue(ClaimTypes.Name),
            HttpContext?.Connection?.RemoteIpAddress?.ToString());

        // The stager already caps details at 4 KiB, so even pathological SQL pastes are bounded
        // in audit storage. The full statement is represented by a stable hash + byte length +
        // statement count; only the bounded preview itself is retained.
        var auditEntry = _stager.Build(
            action: action,
            actor: auditActor,
            resourceType: "DbAdminQuery",
            resourceId: null,
            details: AuditDetails.Json(
                ("mode", mode),
                ("success", success.HasValue ? (success.Value ? "true" : "false") : "pending"),
                ("reason", reason),
                ("rowsAffected", rowsAffected?.ToString() ?? string.Empty),
                ("sql", Preview(sql)),
                ("sqlSha256", SqlHash(sql)),
                ("sqlBytes", Encoding.UTF8.GetByteCount(sql)),
                ("statementCount", DbAdminQueryExecutor.CountStatements(sql))));
        _db.AuditLog.Add(auditEntry);

        try
        {
            await _db.SaveChangesAsync(ct);
            AuditEventForwarder.ForwardCommitted(_logger, auditEntry);
            return true;
        }
        catch (Exception ex)
        {
            // Never let an audit write failure mask the actual query result.
            _logger.LogError(ex, "Failed to persist DbAdmin query audit entry");
            return false;
        }
    }

    private async Task WriteRowsViewedAuditAsync(
        string table,
        int skip,
        int take,
        string? orderBy,
        bool descending,
        long total,
        int returned,
        CancellationToken ct)
    {
        var actor = new AuditActor(GetCallerId(), User.FindFirstValue(ClaimTypes.Name),
            HttpContext?.Connection?.RemoteIpAddress?.ToString());
        var entry = _stager.Build(
            AuditActions.DbAdminRowsViewed,
            actor,
            "DbAdminTable",
            null,
            AuditDetails.Json(
                ("table", table),
                ("skip", skip),
                ("take", take),
                ("orderBy", orderBy),
                ("descending", descending),
                ("total", total),
                ("returned", returned)));
        _db.AuditLog.Add(entry);

        try
        {
            await _db.SaveChangesAsync(ct);
            AuditEventForwarder.ForwardCommitted(_logger, entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist DbAdmin table-view audit entry");
        }
    }

    private static string SqlHash(string sql)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

    private static string Preview(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return string.Empty;
        return sql.Length > 1000 ? sql[..1000] + "…" : sql;
    }

    private static string SanitiseReadError(string message)
    {
        if (string.IsNullOrEmpty(message)) return "Statement rejected by the database.";
        // Cap the size; the message itself is useful for typo-debugging but extremely long
        // provider errors are usually just noise (stack-like SQL Server messages).
        return message.Length > 800 ? message[..800] + "…" : message;
    }

    // --- Helpers ---

    private async Task<long> CountRowsAsync(Type clrType, CancellationToken ct)
    {
        try
        {
            // Non-generic IQueryable via reflection — same as QueryBuilder approach
            var setMethod = typeof(DbContext).GetMethods()
                .First(m => m.Name == "Set" && m.IsGenericMethod && m.GetParameters().Length == 0);
            var dbSet = setMethod.MakeGenericMethod(clrType).Invoke(_db, null) as IQueryable<object>;
            if (dbSet is null) return 0;
            return await dbSet.AsNoTracking().LongCountAsync(ct);
        }
        catch
        {
            return 0;
        }
    }

    private Guid? GetCallerId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static Guid? TryParseGuid(string? s)
        => s is not null && Guid.TryParse(s, out var g) ? g : null;

    private static string? SafeRedact(object? value)
    {
        if (value is null) return null;
        var s = value.ToString() ?? string.Empty;
        // Don't pass through potential secrets in audit details — just show the length
        // for byte arrays; for strings keep it if short enough to be useful
        if (value is byte[]) return "[binary]";
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    private static bool IsUserSessionInvalidatingColumn(string columnName)
        => string.Equals(columnName, nameof(NodePilot.Core.Models.User.Role), StringComparison.OrdinalIgnoreCase)
           || string.Equals(columnName, nameof(NodePilot.Core.Models.User.IsActive), StringComparison.OrdinalIgnoreCase);

    private static string ToDisplayName(string entityName)
    {
        // "WorkflowExecution" → "Workflow Executions"
        var spaced = System.Text.RegularExpressions.Regex.Replace(
            entityName, @"(?<=[a-z])(?=[A-Z])", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        // Pluralize last word naively (enough for display)
        var parts = spaced.Split(' ');
        parts[^1] = Pluralize(parts[^1]);
        return string.Join(" ", parts);
    }

    private static string Pluralize(string word) => word switch
    {
        "Entry"     => "Entries",
        "Heartbeat" => "Heartbeats",
        _           => word.EndsWith('s') ? word : word + "s"
    };

    private static string FriendlyTypeName(Type t, bool isNullable)
    {
        var name = t.Name switch
        {
            "String"   => "string",
            "Boolean"  => "boolean",
            "Int32"    => "int",
            "Int64"    => "long",
            "Double"   => "double",
            "Decimal"  => "decimal",
            "Single"   => "float",
            "Guid"     => "guid",
            "DateTime" => "datetime",
            "Byte[]"   => "bytes",
            _          => t.IsEnum ? "enum:" + t.Name : t.Name.ToLowerInvariant(),
        };
        return isNullable ? name + "?" : name;
    }
}
