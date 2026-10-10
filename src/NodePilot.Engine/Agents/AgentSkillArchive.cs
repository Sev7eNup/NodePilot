using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NodePilot.Core.Agents;
using YamlDotNet.RepresentationModel;

namespace NodePilot.Engine.Agents;

public static partial class AgentSkillArchive
{
    public sealed record ValidatedSkill(string Name, string Description, string Instructions, string Sha256,
        IReadOnlyDictionary<string, byte[]> Files);

    public static ValidatedSkill Read(byte[] package)
    {
        if (package.Length is 0 or > AgentOptions.MaxSkillPackageBytes) throw new ArgumentException("Skill packages must be 1 byte–10 MB.");
        using var stream = new MemoryStream(package, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 200) throw new ArgumentException("Skill packages are limited to 200 entries.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var name = ValidateRelativePath(entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new ArgumentException("Skill symlinks are not supported.");
            if (entry.Length > AgentOptions.MaxSkillPackageBytes - bytes) throw new ArgumentException("Extracted skill exceeds 10 MB.");
            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                bytes += count;
                if (bytes > AgentOptions.MaxSkillPackageBytes) throw new ArgumentException("Extracted skill exceeds 10 MB.");
                output.Write(buffer, 0, count);
            }
            if (!files.TryAdd(name, output.ToArray())) throw new ArgumentException("Duplicate skill file path.");
        }
        if (!files.TryGetValue("SKILL.md", out var markdown)) throw new ArgumentException("A root SKILL.md is required.");
        if (markdown.Length > 64_000) throw new ArgumentException("SKILL.md exceeds 64 KB.");
        var text = new UTF8Encoding(false, true).GetString(markdown).TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw new ArgumentException("SKILL.md requires YAML frontmatter.");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new ArgumentException("SKILL.md has incomplete frontmatter.");
        var yaml = new YamlStream();
        try { yaml.Load(new StringReader(text[4..end])); }
        catch (YamlDotNet.Core.YamlException) { throw new ArgumentException("SKILL.md contains invalid YAML frontmatter."); }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode fields)
            throw new ArgumentException("SKILL.md frontmatter must be a mapping.");
        string ReadField(string key) => fields.Children.TryGetValue(new YamlScalarNode(key), out var value)
            && value is YamlScalarNode scalar ? scalar.Value?.Trim() ?? "" : "";
        var skillName = ReadField("name");
        var description = ReadField("description");
        if (skillName.Length > 64 || !NamePattern().IsMatch(skillName) || description.Length is < 1 or > 1024)
            throw new ArgumentException("SKILL.md requires a lowercase kebab-case name (up to 64 characters) and description (up to 1024 characters).");
        return new ValidatedSkill(skillName, description, text[(end + 5)..], Convert.ToHexStringLower(SHA256.HashData(package)), files);
    }

    public static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':')
            || Path.IsPathRooted(path) || path.Split('/').Any(s => s is "" or "." or ".." || s.EndsWith('.') || s.EndsWith(' ')
                || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ReservedName().IsMatch(s)))
            throw new ArgumentException("Invalid skill resource path.");
        return path;
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\\.|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();
}
