using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using System.Xml.XPath;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Engine-local XPath query over an XML payload. Source is either a file on the engine host
/// (<c>source=file</c>, <c>path=…</c>) or an inline string (<c>source=inline</c>,
/// <c>content=…</c> — typically fed by <c>{{prev.output}}</c>).
///
/// Config:
///   source      "file" | "inline"            (default "inline")
///   path        string, absolute file path   (when source=file)
///   content     string, XML payload          (when source=inline)
///   xpath       string, required
///   namespaces  object { "prefix": "uri" }   (optional; registered in XmlNamespaceManager)
///   resultMode  "single" | "all"             (default "single")
///
/// Result:
///   Success → Output = single match InnerText (single-mode) or JSON array of InnerText
///             values (all-mode); OutputParameters["result"] = Output,
///             OutputParameters["count"] = match count. No match in single-mode succeeds
///             with empty Output and count=0 (use edge conditions to branch on that).
///   Failure → ErrorOutput carries the exception message (invalid XML, invalid XPath,
///             file not found).
/// </summary>
public class XmlQueryActivity : IActivityExecutor
{
    private const int MaxXmlBytes = 8 * 1024 * 1024;

    private readonly IConfiguration? _config;

    public XmlQueryActivity(IConfiguration? config = null)
    {
        _config = config;
    }

    public string ActivityType => "xmlQuery";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => ActivityExecution.RunAsync(async () =>
        {
            var source = config.GetStringOrNull("source")?.ToLowerInvariant() ?? "inline";
            var xpath = config.GetStringOrNull("xpath");
            var resultMode = config.GetStringOrNull("resultMode")?.ToLowerInvariant() ?? "single";

            if (string.IsNullOrWhiteSpace(xpath))
                return Fail("'xpath' is required");

            var loaded = await LoadXmlAsync(source, config, ct);
            if (loaded.Error is not null) return loaded.Error;
            var xml = loaded.Xml!;

            // Hard caps to stop a malicious inline XML from DoS-ing the engine: reject
            // payloads larger than 8 MiB outright, and load via XmlReader with DTD
            // processing prohibited (blocks XXE and billion-laughs entity expansion).
            if (System.Text.Encoding.UTF8.GetByteCount(xml) > MaxXmlBytes)
                return Fail($"content exceeds {MaxXmlBytes} bytes");

            var doc = LoadXmlDocument(xml);
            var nsm = BuildNamespaceManager(doc, config);

            var nav = doc.CreateNavigator()
                ?? throw new InvalidOperationException("XmlDocument.CreateNavigator returned null");
            var (output, count) = EvaluateXPath(nav, xpath, nsm, resultMode);

            return new ActivityResult
            {
                Success = true,
                Output = output,
                OutputParameters = new Dictionary<string, string>
                {
                    ["result"] = output,
                    ["count"] = count.ToString(),
                },
            };
        }, ex => $"XmlQuery error: {ex.Message}");

    private async Task<(string? Xml, ActivityResult? Error)> LoadXmlAsync(string source, JsonElement config, CancellationToken ct)
    {
        if (source == "file")
        {
            var path = config.GetStringOrNull("path");
            if (string.IsNullOrWhiteSpace(path))
                return (null, Fail("'path' is required when source=file"));

            // M-8: apply the same PathGuard config FileOperationActivity uses — ops can
            // restrict file-mode XmlQuery to allow-listed roots / reject `..` traversal.
            if (_config is not null)
            {
                try { PathGuard.Validate(_config, path); }
                catch (InvalidOperationException ex)
                {
                    return (null, Fail($"file access denied: {ex.Message}"));
                }
            }

            if (!File.Exists(path))
                return (null, Fail($"file not found: {path}"));
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaxXmlBytes)
                return (null, Fail($"file exceeds {MaxXmlBytes} bytes"));
            return (await File.ReadAllTextAsync(path, ct), null);
        }

        var inline = config.GetString("content", "");
        if (string.IsNullOrWhiteSpace(inline))
            return (null, Fail("'content' is required when source=inline"));
        return (inline, null);
    }

    private static XmlDocument LoadXmlDocument(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024 * 1024,
            MaxCharactersInDocument = MaxXmlBytes,
        };
        using var stringReader = new StringReader(xml);
        using var xmlReader = XmlReader.Create(stringReader, readerSettings);
        doc.Load(xmlReader);
        return doc;
    }

    private static XmlNamespaceManager BuildNamespaceManager(XmlDocument doc, JsonElement config)
    {
        var nsm = new XmlNamespaceManager(doc.NameTable);
        if (!config.TryGetProperty("namespaces", out var nsEl) || nsEl.ValueKind != JsonValueKind.Object)
            return nsm;
        foreach (var entry in nsEl.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
                nsm.AddNamespace(entry.Name, entry.Value.GetString() ?? "");
        }
        return nsm;
    }

    private static (string Output, int Count) EvaluateXPath(XPathNavigator nav, string xpath, XmlNamespaceManager nsm, string resultMode)
    {
        var result = nav.Evaluate(xpath, nsm);
        return result switch
        {
            XPathNodeIterator nodeIter => ExtractNodes(nodeIter, resultMode),
            double doubleVal => (doubleVal.ToString("G"), 1),
            bool boolVal => (boolVal.ToString().ToLowerInvariant(), 1),
            _ => ExtractString(result),
        };
    }

    private static (string Output, int Count) ExtractNodes(XPathNodeIterator nodeIter, string resultMode)
    {
        var nodes = new List<string?>();
        while (nodeIter.MoveNext())
        {
            if (resultMode == "all")
                nodes.Add(nodeIter.Current?.InnerXml);
            else
            {
                nodes.Add(nodeIter.Current?.Value);
                break;
            }
        }
        var count = resultMode == "all" ? nodes.Count : (nodes.Count > 0 ? 1 : 0);
        var output = resultMode == "all"
            ? JsonSerializer.Serialize(nodes)
            : (nodes.FirstOrDefault() ?? "");
        return (output, count);
    }

    private static (string Output, int Count) ExtractString(object? result)
    {
        var output = result?.ToString() ?? "";
        return (output, string.IsNullOrEmpty(output) ? 0 : 1);
    }

    private static ActivityResult Fail(string message) =>
        new() { Success = false, ErrorOutput = $"XmlQuery: {message}" };
}
