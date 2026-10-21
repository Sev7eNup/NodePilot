using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>Runs structured queries on the WinRM target, returning only the selected values.</summary>
public sealed class RemoteStructuredQuery(
    IRemoteSessionFactory sessionFactory,
    ICredentialStore credentialStore,
    NodePilotDbContext db,
    IConfiguration configuration)
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private static readonly Lazy<string> JsonAssembly = new(CompressJsonAssembly);
    private static readonly Lazy<string> JsonAssemblyCacheKey = new(() =>
        "NodePilot.JsonQuery." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(JsonAssembly.Value))));

    internal async Task<ActivityResult?> ExecuteAsync(
        StepExecutionContext context, JsonElement config, string activityType, CancellationToken ct)
    {
        var target = await ActivityTargetResolver.ResolveAsync(context, db, credentialStore, ct);
        if (target.Machine is null)
            throw new InvalidOperationException("Query target machine was not found");
        if (target.RunsLocally) return null;

        var script = BuildScript(config, activityType, configuration);
        await using var session = await sessionFactory.CreateSessionAsync(target.Machine, target.Credential, ct);
        var raw = await session.ExecuteScriptAsync(script, config.GetOptionalPositiveInt("timeoutSeconds"), ct);
        if (!raw.Success)
            return new ActivityResult { Success = false, ErrorOutput = raw.ErrorOutput, Output = raw.Output };

        using var document = JsonDocument.Parse(raw.Output);
        var root = document.RootElement;
        var output = root.GetProperty("result").GetString() ?? "";
        var count = root.GetProperty("count").GetInt32();
        return new ActivityResult
        {
            Success = true,
            Output = output,
            OutputParameters = new() { ["result"] = output, ["count"] = count.ToString(CultureInfo.InvariantCulture) },
        };
    }

    internal static string BuildScript(JsonElement config, string activityType, IConfiguration configuration)
    {
        var source = config.GetString("source", "inline").ToLowerInvariant();
        var isJson = activityType == "jsonQuery";
        var query = config.GetStringOrNull(isJson ? "jsonPath" : "xpath");
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("Query expression is required");
        string payload;
        if (source == "file")
        {
            var path = config.GetStringOrNull("path");
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("'path' is required when source=file");
            payload = $$"""
                $__npPath = {{PowerShellOperation.Literal(path)}}
                {{TargetPathGuardScript.Build(configuration, ("$__npPath", "path"))}}
                $__npStream = [System.IO.File]::OpenRead($__npPath)
                try {
                    if ($__npStream.Length -gt {{MaxBytes}}) { throw 'Query file exceeds {{MaxBytes}} bytes' }
                    $__npReader = [System.IO.StreamReader]::new($__npStream)
                    try { $__npContent = $__npReader.ReadToEnd() } finally { $__npReader.Dispose() }
                } finally { $__npStream.Dispose() }
                """;
        }
        else
        {
            var content = config.GetString("content", "");
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("'content' is required when source=inline");
            if (Encoding.UTF8.GetByteCount(content) > MaxBytes) throw new InvalidOperationException($"Query content exceeds {MaxBytes} bytes");
            payload = $"$__npContent = {PowerShellOperation.Literal(content)}";
        }

        return $$"""
            $ErrorActionPreference = 'Stop'
            {{payload}}
            if ([System.Text.Encoding]::UTF8.GetByteCount($__npContent) -gt {{MaxBytes}}) { throw 'Query content exceeds {{MaxBytes}} bytes' }
            $__npQuery = {{PowerShellOperation.Literal(query)}}
            $__npAll = {{(config.GetString("resultMode", "single").Equals("all", StringComparison.OrdinalIgnoreCase) ? "$true" : "$false")}}
            $__npOutput = ''
            $__npCount = 0
            {{(isJson ? BuildJsonQuery() : BuildXmlQuery(config))}}
            ConvertTo-Json -InputObject ([ordered]@{ result = [string]$__npOutput; count = [int]$__npCount }) -Compress -Depth 4
            """;
    }

    private static string BuildJsonQuery() => $$"""
        # Reuse this exact payload in pooled sessions; Load(byte[]) otherwise retains a copy per query.
        $__npJsonAssembly = [AppDomain]::CurrentDomain.GetData('{{JsonAssemblyCacheKey.Value}}')
        if ($null -eq $__npJsonAssembly) {
            $__npCompressed = [System.IO.MemoryStream]::new([Convert]::FromBase64String('{{JsonAssembly.Value}}'))
            $__npZip = [System.IO.Compression.GZipStream]::new($__npCompressed, [System.IO.Compression.CompressionMode]::Decompress)
            $__npAssemblyBytes = [System.IO.MemoryStream]::new()
            try {
                $__npZip.CopyTo($__npAssemblyBytes)
                $__npJsonAssembly = [System.Reflection.Assembly]::Load($__npAssemblyBytes.ToArray())
                [AppDomain]::CurrentDomain.SetData('{{JsonAssemblyCacheKey.Value}}', $__npJsonAssembly)
            } finally { $__npZip.Dispose(); $__npCompressed.Dispose(); $__npAssemblyBytes.Dispose() }
        }
        $__npReaderType = $__npJsonAssembly.GetType('Newtonsoft.Json.JsonTextReader', $true)
        $__npTokenType = $__npJsonAssembly.GetType('Newtonsoft.Json.Linq.JToken', $true)
        $__npArrayType = $__npJsonAssembly.GetType('Newtonsoft.Json.Linq.JArray', $true)
        $__npFormatting = [Enum]::Parse($__npJsonAssembly.GetType('Newtonsoft.Json.Formatting', $true), 'None')
        $__npText = [System.IO.StringReader]::new($__npContent)
        $__npJsonReader = $__npReaderType::new($__npText)
        try {
            $__npJsonReader.MaxDepth = 64
            $__npJsonReader.DateParseHandling = [Enum]::Parse($__npJsonAssembly.GetType('Newtonsoft.Json.DateParseHandling', $true), 'None')
            $__npRoot = $__npTokenType::ReadFrom($__npJsonReader)
        } finally { $__npJsonReader.Close(); $__npText.Dispose() }
        if ($__npAll) {
            $__npMatches = $__npArrayType::new()
            foreach ($__npMatch in $__npRoot.SelectTokens($__npQuery)) { $__npMatches.Add($__npMatch) }
            $__npOutput = $__npMatches.ToString($__npFormatting)
            $__npCount = $__npMatches.Count
        } else {
            $__npToken = $__npRoot.SelectToken($__npQuery)
            if ($null -ne $__npToken) {
                $__npCount = 1
                switch ([string]$__npToken.get_Type()) {
                    'Null' { $__npOutput = '' }
                    'String' { $__npOutput = [string]$__npToken.Value }
                    'Boolean' { $__npOutput = ([string]$__npToken.Value).ToLowerInvariant() }
                    { $_ -in 'Object', 'Array' } { $__npOutput = $__npToken.ToString($__npFormatting) }
                    default { $__npOutput = [Convert]::ToString($__npToken.Value, [System.Globalization.CultureInfo]::InvariantCulture) }
                }
            }
        }
        """;

    private static string BuildXmlQuery(JsonElement config)
    {
        var namespaces = new StringBuilder();
        if (config.TryGetProperty("namespaces", out var entries) && entries.ValueKind == JsonValueKind.Object)
            foreach (var entry in entries.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String)
                    namespaces.AppendLine($"$__npNamespaces.AddNamespace({PowerShellOperation.Literal(entry.Name)}, {PowerShellOperation.Literal(entry.Value.GetString())})");

        return $$"""
            $__npSettings = [System.Xml.XmlReaderSettings]::new()
            $__npSettings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
            $__npSettings.XmlResolver = $null
            $__npSettings.MaxCharactersFromEntities = 1048576
            $__npSettings.MaxCharactersInDocument = {{MaxBytes}}
            $__npText = [System.IO.StringReader]::new($__npContent)
            $__npXmlReader = [System.Xml.XmlReader]::Create($__npText, $__npSettings)
            $__npDoc = [System.Xml.XmlDocument]::new()
            $__npDoc.XmlResolver = $null
            try { $__npDoc.Load($__npXmlReader) } finally { $__npXmlReader.Dispose(); $__npText.Dispose() }
            $__npNamespaces = [System.Xml.XmlNamespaceManager]::new($__npDoc.NameTable)
            {{namespaces}}
            $__npValue = $__npDoc.CreateNavigator().Evaluate($__npQuery, $__npNamespaces)
            if ($__npValue -is [System.Xml.XPath.XPathNodeIterator]) {
                $__npNodes = [System.Collections.Generic.List[string]]::new()
                while ($__npValue.MoveNext()) {
                    $__npNodes.Add($__npValue.Current.Value)
                    if (-not $__npAll) { break }
                }
                $__npCount = $__npNodes.Count
                if ($__npAll) { $__npOutput = ConvertTo-Json -InputObject ($__npNodes.ToArray()) -Compress }
                elseif ($__npCount -gt 0) { $__npOutput = $__npNodes[0] }
            } elseif ($__npValue -is [bool]) {
                $__npOutput = ([string]$__npValue).ToLowerInvariant()
                $__npCount = 1
            } else {
                $__npOutput = [Convert]::ToString($__npValue, [System.Globalization.CultureInfo]::InvariantCulture)
                if ($__npValue -is [double] -or $__npOutput.Length -gt 0) { $__npCount = 1 }
            }
            """;
    }

    private static string CompressJsonAssembly()
    {
        using var source = typeof(RemoteStructuredQuery).Assembly.GetManifestResourceStream("NodePilot.RemoteQuery.Newtonsoft.Json.dll")
            ?? throw new InvalidOperationException("Remote JSON query assembly is missing");
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) source.CopyTo(gzip);
        return Convert.ToBase64String(buffer.ToArray());
    }
}
