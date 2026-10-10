using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Agents;
using NodePilot.Data;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Agents;

internal static class AgentSkillTools
{
    internal static async Task<IReadOnlyList<AgentTool>> CreateAsync(AgentRunDatabase database, AgentDefinition definition,
        AgentTarget? target, Guid runId, CancellationToken ct, int outputLimit = 16_000)
    {
        if (definition.SkillIds.Length == 0) return [];
        var packages = await database.UseAsync(db => db.AgentSkillPackages.AsNoTracking().Where(x => definition.SkillIds.Contains(x.Id) && x.Enabled).ToListAsync(ct), ct);
        if (packages.Count != definition.SkillIds.Distinct().Count()) throw new ArgumentException("A selected skill is missing or disabled.");
        var skills = packages.ToDictionary(p => p.Id.ToString(), p => AgentSkillArchive.Read(p.Package));
        foreach (var package in packages)
            if (skills[package.Id.ToString()].Sha256 != package.Sha256) throw new InvalidOperationException("Skill package integrity check failed.");
        var uploaded = new HashSet<string>(StringComparer.Ordinal);
        async Task ValidatePackage(Guid id, CancellationToken token)
        {
            var expected = packages.Single(p => p.Id == id);
            if (!await database.UseAsync(db => db.AgentSkillPackages.AsNoTracking().AnyAsync(x => x.Id == id && x.Enabled && x.Sha256 == expected.Sha256, token), token))
                throw new UnauthorizedAccessException("Selected skill has been disabled or changed.");
        }
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new { skillId = new { type = "string", @enum = skills.Keys.ToArray() } },
            required = new[] { "skillId" }, additionalProperties = false
        });
        var resourceSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = new
            {
                skillId = new { type = "string", @enum = skills.Keys.ToArray() }, path = new { type = "string", maxLength = 240 },
                offset = new { type = "integer", minimum = 0 },
                arguments = new { type = "array", items = new { type = "string", maxLength = 2048 }, maxItems = 32 }
            }, required = new[] { "skillId", "path" }, additionalProperties = false
        });
        async Task<AgentSkillArchive.ValidatedSkill> Resolve(JsonElement input, CancellationToken token)
        {
            var id = input.GetProperty("skillId").GetString()!;
            if (!skills.TryGetValue(id, out var skill)) throw new UnauthorizedAccessException("Skill is not selected for this agent.");
            var guid = Guid.Parse(id);
            await ValidatePackage(guid, token);
            return skill;
        }
        return
        [
            new AgentTool("load_skill", "Inspect selected skill metadata and resource paths. Main instructions are already provided completely in your member context; use read_skill_resource for applicable references. Selected skills: "
                + string.Join("; ", skills.Select(x => $"{x.Key}: {x.Value.Name} — {x.Value.Description}")), schema, async (input, token) =>
                {
                    var skill = await Resolve(input, token);
                    return JsonSerializer.Serialize(new { skill.Name, instructions = "Main instructions are already loaded completely in your member context.", resources = skill.Files.Keys });
                }) { IsSkillGuidance = true, Skills = packages.Select(p => new AgentSkillGuidance(p.Id, p.Name, p.Version, p.Sha256,
                    skills[p.Id.ToString()].Instructions, skills[p.Id.ToString()].Files.Keys.ToArray(), token => ValidatePackage(p.Id, token))).ToArray() },
            new AgentTool("read_skill_resource", "Read a UTF-8 resource or SKILL.md in 8192-byte excerpts. Optional offset is a byte offset; nextOffset continues reading.", resourceSchema, async (input, token) =>
                {
                    var skill = await Resolve(input, token);
                    var path = AgentSkillArchive.ValidateRelativePath(input.GetProperty("path").GetString()!);
                    if (!skill.Files.TryGetValue(path, out var data)) throw new ArgumentException("Unknown skill resource.");
                    var offset = input.TryGetProperty("offset", out var offsetValue) ? offsetValue.GetInt32() : 0;
                    if (offset < 0 || offset > data.Length) throw new ArgumentException("Invalid resource offset.");
                    if (offset < data.Length && (data[offset] & 0xc0) == 0x80)
                        throw new ArgumentException("Resource offset must be at a UTF-8 character boundary.");
                    var count = Math.Min(8192, data.Length - offset);
                    while (true)
                    {
                        while (count > 0 && offset + count < data.Length && (data[offset + count] & 0xc0) == 0x80) count--;
                        if (count == 0 && offset < data.Length) throw new ArgumentException("Skill output limit cannot fit a UTF-8 character.");
                        var page = JsonSerializer.Serialize(new { skillId = input.GetProperty("skillId").GetString(), skill.Sha256,
                            path, offset, nextOffset = offset + count, hasMore = offset + count < data.Length,
                            text = new UTF8Encoding(false, true).GetString(data, offset, count) });
                        if (page.Length <= outputLimit) return page;
                        if (count == 0) throw new ArgumentException("Skill page metadata exceeds the output limit.");
                        count /= 2;
                        if (count == 0) throw new ArgumentException("Skill output limit cannot fit a character.");
                    }
                }) { IsSkillGuidance = true },
            new AgentTool("run_skill_script", "Run a packaged read-only script using a selected shell. The host checks its commands and bound arguments before any upload. Only the same limited read language as the shell tool is supported; target execution policy still applies.", resourceSchema, async (input, token) =>
                {
                    var skill = await Resolve(input, token);
                    var path = AgentSkillArchive.ValidateRelativePath(input.GetProperty("path").GetString()!);
                    if (!path.StartsWith("scripts/", StringComparison.Ordinal) || !skill.Files.ContainsKey(path))
                        throw new ArgumentException("Only packaged files under scripts/ can be executed.");
                    var shell = Path.GetExtension(path).ToLowerInvariant() switch
                    { ".ps1" => "powershell", ".cmd" or ".bat" => "cmd", ".sh" => "bash", _ => throw new ArgumentException("Unsupported skill script type.") };
                    if (!definition.Tools.Any(t => t.Name == shell)) throw new UnauthorizedAccessException("Select the matching shell tool before running skill scripts.");
                    if (target is null) throw new ArgumentException("Skill scripts require a target machine.");
                    var arguments = input.TryGetProperty("arguments", out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
                    var bytes = skill.Files[path];
                    var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
                    if (!hasBom && bytes.Any(b => b > 127))
                        throw new UnauthorizedAccessException("Agent read-only policy: scripts must be ASCII or UTF-8 with BOM so validation and target decoding agree.");
                    var source = new UTF8Encoding(false, true).GetString(bytes.AsSpan(hasBom ? 3 : 0));
                    var prepared = AgentPermissionPolicy.PrepareSkillScript(shell, source.Trim(), arguments);
                    if (shell == "powershell") await AgentSkillExecution.CheckPowerShellPolicyAsync(target, token);
                    var root = await target.EnsureWorkingRootAsync(runId, definition.Id, token);
                    var packageRoot = Path.Combine(root, input.GetProperty("skillId").GetString()!);
                    if (uploaded.Add(packageRoot))
                        try { await UploadAsync(target, packageRoot, skill.Files, token); }
                        catch { uploaded.Remove(packageRoot); throw; }
                    var scriptPath = Path.Combine(packageRoot, path.Replace('/', Path.DirectorySeparatorChar));
                    if (shell == "powershell")
                        await AgentSkillExecution.VerifyPowerShellFileAsync(target, scriptPath, Convert.ToHexString(SHA256.HashData(bytes)), token);
                    // PowerShell retains the verified original bytes for target signature policy.
                    // CMD/Bash execute only the checked form with pinned executables, never package-local commands.
                    var command = shell == "powershell"
                        ? "$ErrorActionPreference='Stop'; & " + PowerShellOperation.Literal(scriptPath) + " " + string.Join(" ", arguments.Select(PowerShellOperation.Literal))
                        : prepared;
                    return AgentSkillExecution.RequireSuccess(await target.ExecuteAsync(AgentProcessScript.Build(shell, command, packageRoot, definition.BashPath), token, 310));
                })
        ];
    }

    private static async Task UploadAsync(AgentTarget target, string root, IReadOnlyDictionary<string, byte[]> files, CancellationToken ct)
    {
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            for (var offset = 0; offset < Math.Max(1, bytes.Length); offset += AgentArtifactStore.TransferBlockBytes)
            {
                var count = Math.Min(AgentArtifactStore.TransferBlockBytes, bytes.Length - offset);
                var base64 = Convert.ToBase64String(bytes, offset, count);
                await target.ExecuteAsync("$ErrorActionPreference='Stop'; $p=" + PowerShellOperation.Literal(path)
                    + "; [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p)); $f=[IO.File]::Open($p,[IO.FileMode]::"
                    + (offset == 0 ? "Create" : "Append") + "); try{$b=[Convert]::FromBase64String('" + base64
                    + "');$f.Write($b,0,$b.Length)}finally{$f.Dispose()}", ct);
            }
            var hash = await target.ExecuteAsync("$ErrorActionPreference='Stop'; $stream=[IO.File]::OpenRead(" + PowerShellOperation.Literal(path)
                + "); $sha=[Security.Cryptography.SHA256]::Create(); try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }"
                + " finally { $sha.Dispose(); $stream.Dispose() }", ct);
            if (!hash.Trim().Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Transferred skill file failed its integrity check.");
        }
    }
}
