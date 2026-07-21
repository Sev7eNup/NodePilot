using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin,Operator")]
public class CredentialsController : ControllerBase
{
    private readonly ICredentialStore _credentialStore;
    private readonly IAuditWriter _audit;

    // Minimum length for stored service-account passwords. No max cap here (DPAPI has no
    // truncation limit like BCrypt), so password managers generating 128-char secrets pass.
    private const int MinCredentialPasswordLength = 8;
    private const int MaxCredentialNameLength = 200;
    private const int MaxCredentialUsernameLength = 200;
    private const int MaxCredentialDomainLength = 255;

    public CredentialsController(ICredentialStore credentialStore, IAuditWriter audit)
    {
        _credentialStore = credentialStore;
        _audit = audit;
    }

    private static string? ValidateCredentialPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinCredentialPasswordLength)
            return $"password must be at least {MinCredentialPasswordLength} characters";
        return null;
    }

    private static string? ValidateCredentialFields(string? name, string? username, string? domain)
    {
        if (string.IsNullOrWhiteSpace(name)) return "name is required";
        if (name.Length > MaxCredentialNameLength) return $"name must be at most {MaxCredentialNameLength} characters";
        if (string.IsNullOrWhiteSpace(username)) return "username is required";
        if (username.Length > MaxCredentialUsernameLength) return $"username must be at most {MaxCredentialUsernameLength} characters";
        if (domain is not null && domain.Length > MaxCredentialDomainLength)
            return $"domain must be at most {MaxCredentialDomainLength} characters";
        if ((name + username + domain).Any(char.IsControl))
            return "name, username and domain must not contain control characters";
        return null;
    }

    [HttpGet]
    public async Task<ActionResult<List<CredentialResponse>>> GetAll(CancellationToken ct)
    {
        var credentials = await _credentialStore.GetAllAsync(ct);
        var response = credentials.Select(c => new CredentialResponse(c.Id, c.Name, c.Username, c.Domain, c.ExpiresAt)).ToList();
        return Ok(response);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CredentialResponse>> GetById(Guid id, CancellationToken ct)
    {
        try
        {
            var c = await _credentialStore.GetAsync(id, ct);
            return Ok(new CredentialResponse(c.Id, c.Name, c.Username, c.Domain, c.ExpiresAt));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<ActionResult<CredentialResponse>> Create(CreateCredentialRequest request, CancellationToken ct)
    {
        if (ValidateCredentialFields(request.Name, request.Username, request.Domain) is { } fieldError)
            return BadRequest(new { error = fieldError });
        if (ValidateCredentialPassword(request.Password) is { } policyError)
            return BadRequest(new { error = policyError });

        var credential = await _credentialStore.CreateAsync(
            request.Name.Trim(), request.Username.Trim(), request.Password, request.Domain?.Trim(), request.ExpiresAt, ct);

        ApiMetrics.CredentialOperations.Add(1,
            new("operation", "create"),
            new("result", "success"));

        // IMPORTANT: never embed the password (or anything derived from it) in the audit row.
        // Only name/username are safe to log — the encrypted blob stays in the Credentials table.
        await _audit.LogAsync(AuditActions.CredentialCreated, "Credential", credential.Id,
            AuditDetails.Json(("name", credential.Name), ("username", credential.Username)), ct);

        var response = new CredentialResponse(credential.Id, credential.Name, credential.Username, credential.Domain, credential.ExpiresAt);
        return CreatedAtAction(nameof(GetById), new { id = credential.Id }, response);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateCredentialRequest request, CancellationToken ct)
    {
        if (ValidateCredentialFields(request.Name, request.Username, request.Domain) is { } fieldError)
            return BadRequest(new { error = fieldError });
        // Update allows a null/empty password (rotate name only). Only validate if present.
        if (!string.IsNullOrEmpty(request.Password)
            && ValidateCredentialPassword(request.Password) is { } policyError)
            return BadRequest(new { error = policyError });

        try
        {
            var name = request.Name.Trim();
            var username = request.Username.Trim();
            var domain = request.Domain?.Trim();
            await _credentialStore.UpdateAsync(id, name, username, request.Password, domain, request.ExpiresAt, ct);

            // Flag whether the password was touched so an auditor can later tell "name change"
            // from "secret rotation". The value itself is never written.
            await _audit.LogAsync(AuditActions.CredentialUpdated, "Credential", id,
                AuditDetails.Json(("name", name), ("passwordChanged", !string.IsNullOrEmpty(request.Password))), ct);

            ApiMetrics.CredentialOperations.Add(1,
                new("operation", "update"),
                new("result", "success"));

            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await _credentialStore.DeleteAsync(id, ct);
            await _audit.LogAsync(AuditActions.CredentialDeleted, "Credential", id, null, ct);

            ApiMetrics.CredentialOperations.Add(1,
                new("operation", "delete"),
                new("result", "success"));

            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
