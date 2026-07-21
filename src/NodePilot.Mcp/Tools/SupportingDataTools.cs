using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Supporting resources an agent wires into workflows: managed machines, credentials and global
/// variables. Read paths never surface secrets — credentials carry no password field and secret
/// global values arrive already masked from the API. Create/update accept secrets write-only.
/// Deletes live in the gated DestructiveTools.
/// </summary>
[McpServerToolType]
public sealed class SupportingDataTools
{
    private readonly NodePilotApiClient _api;

    public SupportingDataTools(NodePilotApiClient api) => _api = api;

    // ---- Machines -----------------------------------------------------------

    [McpServerTool(Name = "list_machines", ReadOnly = true)]
    [Description("List managed (WinRM) machines with reachability and usage stats.")]
    public async Task<object> ListMachines(CancellationToken cancellationToken = default)
    {
        var machines = await ApiErrorMapper.Guard(() => _api.ListMachinesAsync(cancellationToken));
        return new { count = machines.Count, machines = machines.Select(Summarize) };
    }

    [McpServerTool(Name = "get_machine", ReadOnly = true)]
    [Description("Get one managed machine by id.")]
    public async Task<object> GetMachine(
        [Description("The machine GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var m = await ApiErrorMapper.Guard(() => _api.GetMachineAsync(Guid(id, "machine id"), cancellationToken));
        return Summarize(m);
    }

    [McpServerTool(Name = "create_machine")]
    [Description("Register a new managed machine (Admin/Operator).")]
    public async Task<object> CreateMachine(
        [Description("Display name.")] string name,
        [Description("Hostname or IP for WinRM.")] string hostname,
        [Description("WinRM port (default 5985).")] int winRmPort = 5985,
        [Description("Use HTTPS/SSL WinRM (default false).")] bool useSsl = false,
        [Description("Optional default credential GUID.")] string? defaultCredentialId = null,
        [Description("Optional comma-separated tags.")] string? tags = null,
        CancellationToken cancellationToken = default)
    {
        var req = new CreateMachineRequest(name, hostname, winRmPort, useSsl, OptGuid(defaultCredentialId, "defaultCredentialId"), tags);
        var m = await ApiErrorMapper.Guard(() => _api.CreateMachineAsync(req, cancellationToken));
        return new { created = true, machineId = m.Id, name = m.Name };
    }

    [McpServerTool(Name = "update_machine")]
    [Description("Update a managed machine (Admin/Operator). Read-modify-write: omitted fields keep their current value (so a partial update never blanks hostname/credential/tags).")]
    public async Task<object> UpdateMachine(
        [Description("The machine GUID.")] string id,
        [Description("New display name (omit to keep current).")] string? name = null,
        [Description("New hostname or IP (omit to keep current).")] string? hostname = null,
        [Description("New WinRM port (omit to keep current).")] int? winRmPort = null,
        [Description("New SSL setting (omit to keep current).")] bool? useSsl = null,
        [Description("New default credential GUID (omit to keep current).")] string? defaultCredentialId = null,
        [Description("New comma-separated tags (omit to keep current).")] string? tags = null,
        CancellationToken cancellationToken = default)
    {
        var guid = Guid(id, "machine id");
        var current = await ApiErrorMapper.Guard(() => _api.GetMachineAsync(guid, cancellationToken));
        var req = new UpdateMachineRequest(
            name ?? current.Name,
            hostname ?? current.Hostname,
            winRmPort ?? current.WinRmPort,
            useSsl ?? current.UseSsl,
            OptGuid(defaultCredentialId, "defaultCredentialId") ?? current.DefaultCredentialId,
            tags ?? current.Tags);
        await ApiErrorMapper.Guard(() => _api.UpdateMachineAsync(guid, req, cancellationToken));
        return new { updated = true, machineId = id };
    }

    [McpServerTool(Name = "test_machine")]
    [Description("Test WinRM connectivity to a machine (Admin/Operator). Optionally override the credential. Returns success + the remote computer name or an error.")]
    public async Task<object> TestMachine(
        [Description("The machine GUID.")] string id,
        [Description("Optional credential GUID to test with (defaults to the machine's default).")] string? credentialId = null,
        CancellationToken cancellationToken = default)
    {
        var req = new TestConnectionRequest(OptGuid(credentialId, "credentialId"));
        var r = await ApiErrorMapper.Guard(() => _api.TestMachineAsync(Guid(id, "machine id"), req, cancellationToken));
        return new { success = r.Success, computerName = r.ComputerName, error = r.Error, credentialUsed = r.CredentialUsed };
    }

    // ---- Credentials --------------------------------------------------------

    [McpServerTool(Name = "list_credentials", ReadOnly = true)]
    [Description("List credentials (name/username/domain only — passwords are never returned).")]
    public async Task<object> ListCredentials(CancellationToken cancellationToken = default)
    {
        var creds = await ApiErrorMapper.Guard(() => _api.ListCredentialsAsync(cancellationToken));
        return new { count = creds.Count, credentials = creds };
    }

    [McpServerTool(Name = "get_credential", ReadOnly = true)]
    [Description("Get one credential by id (no password).")]
    public async Task<object> GetCredential(
        [Description("The credential GUID.")] string id,
        CancellationToken cancellationToken = default)
        => await ApiErrorMapper.Guard(() => _api.GetCredentialAsync(Guid(id, "credential id"), cancellationToken));

    [McpServerTool(Name = "create_credential")]
    [Description("Create a credential (Admin/Operator). The password is write-only and never returned.")]
    public async Task<object> CreateCredential(
        [Description("Display name.")] string name,
        [Description("Username (DOMAIN\\user or user).")] string username,
        [Description("Password (>=8 chars, stored encrypted, never returned).")] string password,
        [Description("Optional domain.")] string? domain = null,
        [Description("Optional account-expiry timestamp (ISO 8601) — feeds the CredentialExpiring alert signal.")] DateTime? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var c = await ApiErrorMapper.Guard(() => _api.CreateCredentialAsync(new CreateCredentialRequest(name, username, password, domain, expiresAt), cancellationToken));
        return new { created = true, credentialId = c.Id, name = c.Name };
    }

    [McpServerTool(Name = "update_credential")]
    [Description("Update a credential (Admin/Operator). Read-modify-write: omitted fields keep their current value. Omit password to rename/retag without rotating it.")]
    public async Task<object> UpdateCredential(
        [Description("The credential GUID.")] string id,
        [Description("New display name (omit to keep current).")] string? name = null,
        [Description("New username (omit to keep current).")] string? username = null,
        [Description("Optional new password (omit to keep the existing one).")] string? password = null,
        [Description("New domain (omit to keep current).")] string? domain = null,
        [Description("New account-expiry timestamp, ISO 8601 (omit to keep current; pass clearExpiresAt=true to remove).")] DateTime? expiresAt = null,
        [Description("Set true to clear a previously set expiry timestamp.")] bool clearExpiresAt = false,
        CancellationToken cancellationToken = default)
    {
        var guid = Guid(id, "credential id");
        var current = await ApiErrorMapper.Guard(() => _api.GetCredentialAsync(guid, cancellationToken));
        var req = new UpdateCredentialRequest(
            name ?? current.Name,
            username ?? current.Username,
            password,                       // null = keep existing (API semantics)
            domain ?? current.Domain,
            clearExpiresAt ? null : (expiresAt ?? current.ExpiresAt));
        await ApiErrorMapper.Guard(() => _api.UpdateCredentialAsync(guid, req, cancellationToken));
        return new { updated = true, credentialId = id };
    }

    // ---- Global variables ---------------------------------------------------

    [McpServerTool(Name = "list_global_variables", ReadOnly = true)]
    [Description("List global variables (Admin/Operator). Secret values are masked as '***' by the server.")]
    public async Task<object> ListGlobalVariables(CancellationToken cancellationToken = default)
    {
        var globals = await ApiErrorMapper.Guard(() => _api.ListGlobalsAsync(cancellationToken));
        return new { count = globals.Count, globals };
    }

    [McpServerTool(Name = "create_global_variable")]
    [Description("Create a global variable (Admin). Mark isSecret=true for secret values (masked on read).")]
    public async Task<object> CreateGlobalVariable(
        [Description("Variable name ([A-Za-z0-9_-], 1-100 chars).")] string name,
        [Description("Value.")] string value,
        [Description("Whether the value is a secret (masked on read).")] bool isSecret = false,
        [Description("Optional description.")] string? description = null,
        [Description("Optional folder id to place the variable in (see list_global_variable_folders). Omit for Root. Cosmetic only.")] string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var fid = OptGuid(folderId, "folder id");
        var g = await ApiErrorMapper.Guard(() => _api.CreateGlobalAsync(new CreateGlobalVariableRequest(name, value, isSecret, description, fid), cancellationToken));
        return new { created = true, globalId = g.Id, name = g.Name, folderId = g.FolderId };
    }

    [McpServerTool(Name = "update_global_variable")]
    [Description("Update a global variable (Admin). Read-modify-write: omitted fields keep their current value. IMPORTANT: isSecret defaults to the variable's CURRENT secret flag — omit it so changing a secret's value does NOT silently demote it to plaintext; pass isSecret=false explicitly to demote on purpose.")]
    public async Task<object> UpdateGlobalVariable(
        [Description("The global variable GUID.")] string id,
        [Description("New name (omit to keep current).")] string? name = null,
        [Description("Optional new value (omit to keep existing).")] string? value = null,
        [Description("Whether the value is a secret. OMIT to keep the current flag; only set it to deliberately promote/demote.")] bool? isSecret = null,
        [Description("New description (omit to keep current).")] string? description = null,
        [Description("Optional folder id to move the variable to. Omit to keep the current folder. Cosmetic only.")] string? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var guid = Guid(id, "global id");
        var current = (await ApiErrorMapper.Guard(() => _api.ListGlobalsAsync(cancellationToken)))
            .FirstOrDefault(v => v.Id == guid)
            ?? throw new McpException($"No global variable with id {guid}.");
        var req = new UpdateGlobalVariableRequest(
            name ?? current.Name,
            value,                              // null = keep existing (API semantics)
            isSecret ?? current.IsSecret,       // keep secret flag unless explicitly changed
            description ?? current.Description,
            OptGuid(folderId, "folder id") ?? current.FolderId);  // keep current folder unless moved
        await ApiErrorMapper.Guard(() => _api.UpdateGlobalAsync(guid, req, cancellationToken));
        return new { updated = true, globalId = id, isSecret = req.IsSecret };
    }

    // ---- Global variable folders (organizational tree) ----------------------

    [McpServerTool(Name = "list_global_variable_folders", ReadOnly = true)]
    [Description("List the global-variable folder tree (path + variable counts). Folders are cosmetic — a variable's {{globals.NAME}} resolution never depends on its folder.")]
    public async Task<object> ListGlobalVariableFolders(CancellationToken cancellationToken = default)
    {
        var folders = await ApiErrorMapper.Guard(() => _api.ListGlobalFoldersAsync(cancellationToken));
        return new { count = folders.Count, folders };
    }

    [McpServerTool(Name = "create_global_variable_folder")]
    [Description("Create a global-variable folder (Admin). Omit parentFolderId to create under Root.")]
    public async Task<object> CreateGlobalVariableFolder(
        [Description("Folder name (max 120 chars, unique among its siblings).")] string name,
        [Description("Optional parent folder id. Omit for Root.")] string? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        var pid = OptGuid(parentFolderId, "parent folder id");
        var f = await ApiErrorMapper.Guard(() => _api.CreateGlobalFolderAsync(new CreateGlobalVariableFolderRequest(pid, name), cancellationToken));
        return new { created = true, folderId = f.Id, path = f.Path };
    }

    [McpServerTool(Name = "rename_global_variable_folder")]
    [Description("Rename a global-variable folder (Admin).")]
    public async Task<object> RenameGlobalVariableFolder(
        [Description("The folder GUID.")] string id,
        [Description("New folder name.")] string name,
        CancellationToken cancellationToken = default)
    {
        var guid = Guid(id, "folder id");
        await ApiErrorMapper.Guard(() => _api.RenameGlobalFolderAsync(guid, new UpdateGlobalVariableFolderRequest(name), cancellationToken));
        return new { updated = true, folderId = id };
    }

    [McpServerTool(Name = "move_global_variable_folder")]
    [Description("Reparent a global-variable folder (Admin). Omit newParentFolderId to move to Root. Rejected on cycles, depth-cap, or a sibling-name clash.")]
    public async Task<object> MoveGlobalVariableFolder(
        [Description("The folder GUID.")] string id,
        [Description("Optional new parent folder id. Omit for Root.")] string? newParentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        var guid = Guid(id, "folder id");
        var pid = OptGuid(newParentFolderId, "parent folder id");
        await ApiErrorMapper.Guard(() => _api.MoveGlobalFolderAsync(guid, new MoveGlobalVariableFolderRequest(pid), cancellationToken));
        return new { moved = true, folderId = id };
    }

    [McpServerTool(Name = "move_global_variable_to_folder")]
    [Description("Move a global variable into a folder (Admin). Purely organizational — does not change how {{globals.NAME}} resolves.")]
    public async Task<object> MoveGlobalVariableToFolder(
        [Description("The global variable GUID.")] string id,
        [Description("The target folder GUID.")] string folderId,
        CancellationToken cancellationToken = default)
    {
        var guid = Guid(id, "global id");
        var fid = Guid(folderId, "folder id");
        await ApiErrorMapper.Guard(() => _api.MoveGlobalToFolderAsync(guid, fid, cancellationToken));
        return new { moved = true, globalId = id, folderId };
    }

    private static object Summarize(MachineResponse m) => new
    {
        id = m.Id,
        name = m.Name,
        hostname = m.Hostname,
        winRmPort = m.WinRmPort,
        useSsl = m.UseSsl,
        isReachable = m.IsReachable,
        lastConnectivityCheck = m.LastConnectivityCheck,
        defaultCredentialId = m.DefaultCredentialId,
        tags = m.Tags,
        usedByWorkflowCount = m.UsedByWorkflowCount,
        recentStepCount = m.RecentStepCount,
        recentFailedStepCount = m.RecentFailedStepCount,
        activeRunCount = m.ActiveRunCount,
    };

    private static System.Guid Guid(string value, string label)
        => System.Guid.TryParse(value, out var g) ? g : throw new McpException($"{label} must be a GUID, got '{value}'.");

    private static System.Guid? OptGuid(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return System.Guid.TryParse(value, out var g) ? g : throw new McpException($"{label} must be a GUID, got '{value}'.");
    }
}
