using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Remote registry activity. Covers both keys and values through these operations:
///
///   read         — read a single value (with valueName) or all values under the key.
///   write        — write a value; creates missing keys idempotently. valueType
///                  selects REG_SZ / REG_DWORD / REG_QWORD / REG_BINARY / REG_MULTI_SZ /
///                  REG_EXPAND_SZ. Defaults to String.
///   deleteValue  — remove a single value.
///   deleteKey    — remove a key and all its sub-keys recursively.
///   createKey    — create an empty key idempotently.
///   exists       — check whether the key exists, or (with valueName) whether the value exists.
///   listSubKeys  — list sub-key names under the path as an array.
///   listValues   — list values (name/type/value) as an array.
///
/// Output format: every operation emits a JSON result object between markers, which
/// PostProcess projects into OutputParameters (param.value, param.exists,
/// param.values, param.subKeys, param.count, param.type — depending on the op). This
/// keeps downstream steps consistent — `{{step.param.values}}` is always a
/// JSON array, `{{step.param.exists}}` is always "true"/"false".
/// </summary>
public class RegistryActivity : BaseRemoteActivity
{
    public override string ActivityType => "registryOperation";

    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("REGISTRY");

    private static readonly HashSet<string> AllowedValueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "String", "ExpandString", "Binary", "DWord", "MultiString", "QWord"
    };

    // PowerShell's Test-Path / Get-Item / Remove-Item work on ANY PSDrive — filesystem,
    // registry, env, certs. Without a prefix guard a typo like `keyPath: "C:\Windows"`
    // + `operation: "deleteKey"` would translate into `Remove-Item -Recurse -Force
    // C:\Windows`. Restrict keyPath to recognised registry-provider prefixes only.
    // Underscore-prefix support for HKEY_* spellings + the explicit `Registry::` PS
    // provider notation; trailing colon variants of the drive aliases.
    private static readonly string[] AllowedRegistryPrefixes =
    {
        "HKLM:", "HKCU:", "HKCR:", "HKU:", "HKCC:",
        "HKEY_LOCAL_MACHINE", "HKEY_CURRENT_USER", "HKEY_CLASSES_ROOT",
        "HKEY_USERS", "HKEY_CURRENT_CONFIG", "HKEY_PERFORMANCE_DATA",
        "Registry::",
    };

    internal static bool IsRegistryKeyPath(string? keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) return false;
        var trimmed = keyPath.TrimStart();
        foreach (var prefix in AllowedRegistryPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public RegistryActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration configuration)
        : base(sessionFactory, credentialStore, db, engineFactory, configuration) { }

    protected override string BuildScript(JsonElement config, StepExecutionContext context)
    {
        var rawOp = config.TryGetProperty("operation", out var op) ? op.GetString() : "read";
        var operation = (rawOp ?? "read").Trim();
        var canonicalOp = operation.ToLowerInvariant();

        var keyPath = config.GetStringOrNull("keyPath");
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException("Registry: 'keyPath' is required");

        if (!IsRegistryKeyPath(keyPath))
            throw new InvalidOperationException(
                $"Registry: 'keyPath' must reference a registry provider path. Allowed prefixes: " +
                "HKLM:\\, HKCU:\\, HKCR:\\, HKU:\\, HKCC:\\, HKEY_*\\, Registry::. " +
                $"Got: '{keyPath}'.");

        var valueName = config.GetStringOrNull("valueName");
        var value = config.GetStringOrNull("value");
        var valueType = config.GetStringOrNull("valueType");

        var qKey = PowerShellOperation.Literal(keyPath);
        var qName = PowerShellOperation.Literal(valueName);
        var qValue = PowerShellOperation.Literal(value);

        // valueType is checked against a whitelist and then inserted below as a bare
        // PowerShell token in -Type — it is NOT quoted. Validation MUST happen here,
        // otherwise a user-controlled string would land unescaped as a cmdlet argument.
        string typeToken = "String";
        if (canonicalOp == "write")
        {
            if (string.IsNullOrWhiteSpace(valueName))
                throw new InvalidOperationException("Registry 'write' requires 'valueName'");
            if (!string.IsNullOrWhiteSpace(valueType))
            {
                if (!AllowedValueTypes.Contains(valueType))
                    throw new InvalidOperationException(
                        $"Registry 'write': unknown valueType '{valueType}'. Allowed: {string.Join(", ", AllowedValueTypes)}");
                typeToken = AllowedValueTypes.First(t => string.Equals(t, valueType, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (canonicalOp == "deletevalue" && string.IsNullOrWhiteSpace(valueName))
            throw new InvalidOperationException("Registry 'deleteValue' requires 'valueName'");

        var opBody = canonicalOp switch
        {
            "read" => string.IsNullOrWhiteSpace(valueName)
                ? BuildReadAllValues()
                : BuildReadSingleValue(),
            "write" => BuildWrite(typeToken),
            "deletevalue" => BuildDeleteValue(),
            "deletekey" => BuildDeleteKey(),
            "createkey" => BuildCreateKey(),
            "exists" => string.IsNullOrWhiteSpace(valueName)
                ? BuildExistsKey()
                : BuildExistsValue(),
            "listsubkeys" => BuildListSubKeys(),
            "listvalues" => BuildListValues(),
            _ => throw new InvalidOperationException($"Unknown registry operation: {operation}")
        };

        // canonicalOp is whitelisted (any other value already threw above), so it's
        // safe to interpolate it directly into the script block below.
        return $$"""
            $ErrorActionPreference = 'Stop'
            $__keyPath = {{qKey}}
            $__valueName = {{qName}}
            $__rawValue = {{qValue}}
            $__result = [ordered]@{ operation = '{{canonicalOp}}'; ok = $true }
            try {
            {{opBody}}
            } catch {
                $__result.ok = $false
                $__result.error = $_.Exception.Message
            }
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 6)}}
            """;
    }

    // --- op bodies (PowerShell snippets) ---

    private static string BuildReadSingleValue() => """
                $v = Get-ItemPropertyValue -LiteralPath $__keyPath -Name $__valueName
                $key = Get-Item -LiteralPath $__keyPath
                $__result.value = $v
                $__result.type = $key.GetValueKind($__valueName).ToString()
        """;

    private static string BuildReadAllValues() => """
                $key = Get-Item -LiteralPath $__keyPath
                $items = New-Object System.Collections.ArrayList
                foreach ($n in $key.Property) {
                    [void]$items.Add([ordered]@{
                        name  = $n
                        type  = $key.GetValueKind($n).ToString()
                        value = $key.GetValue($n)
                    })
                }
                $__result.values = @($items)
                $__result.count = $items.Count
        """;

    private static string BuildWrite(string typeToken) => $$"""
                if (-not (Test-Path -LiteralPath $__keyPath)) {
                    New-Item -Path $__keyPath -Force | Out-Null
                }
                switch ('{{typeToken}}') {
                    'DWord'       { $__typed = [int]$__rawValue }
                    'QWord'       { $__typed = [long]$__rawValue }
                    'Binary'      {
                        $hex = ($__rawValue -replace '[\s,;:-]', '') -replace '^0x', ''
                        if ($hex.Length % 2 -ne 0) { throw "Binary value needs even number of hex digits" }
                        $bytes = New-Object 'System.Collections.Generic.List[byte]'
                        for ($i = 0; $i -lt $hex.Length; $i += 2) {
                            [void]$bytes.Add([Convert]::ToByte($hex.Substring($i, 2), 16))
                        }
                        $__typed = $bytes.ToArray()
                    }
                    'MultiString' { $__typed = ,@($__rawValue -split "`r?`n") }
                    default       { $__typed = $__rawValue }
                }
                Set-ItemProperty -LiteralPath $__keyPath -Name $__valueName -Type {{typeToken}} -Value $__typed
                $__result.type = '{{typeToken}}'
        """;

    private static string BuildDeleteValue() => """
                Remove-ItemProperty -LiteralPath $__keyPath -Name $__valueName -Force
        """;

    private static string BuildDeleteKey() => """
                if (Test-Path -LiteralPath $__keyPath) {
                    Remove-Item -LiteralPath $__keyPath -Recurse -Force
                }
        """;

    private static string BuildCreateKey() => """
                if (-not (Test-Path -LiteralPath $__keyPath)) {
                    New-Item -Path $__keyPath -Force | Out-Null
                    $__result.created = $true
                } else {
                    $__result.created = $false
                }
        """;

    private static string BuildExistsKey() => """
                $__result.exists = [bool](Test-Path -LiteralPath $__keyPath)
        """;

    private static string BuildExistsValue() => """
                if (Test-Path -LiteralPath $__keyPath) {
                    $key = Get-Item -LiteralPath $__keyPath
                    $__result.exists = [bool]($key.Property -contains $__valueName)
                } else {
                    $__result.exists = $false
                }
        """;

    private static string BuildListSubKeys() => """
                $names = New-Object System.Collections.ArrayList
                Get-ChildItem -LiteralPath $__keyPath -ErrorAction Stop | ForEach-Object {
                    [void]$names.Add($_.PSChildName)
                }
                $__result.subKeys = @($names)
                $__result.count = $names.Count
        """;

    private static string BuildListValues() => """
                $key = Get-Item -LiteralPath $__keyPath
                $items = New-Object System.Collections.ArrayList
                foreach ($n in $key.Property) {
                    [void]$items.Add([ordered]@{
                        name  = $n
                        type  = $key.GetValueKind($n).ToString()
                        value = $key.GetValue($n)
                    })
                }
                $__result.values = @($items)
                $__result.count = $items.Count
        """;

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        var output = raw.Output ?? "";
        if (!PowerShellOperation.TryParseJsonBlock(output, ResultMarkers, out var doc, out var parseError))
        {
            if (parseError is null) return raw;

            return new ActivityResult
            {
                Success = false,
                Output = raw.Output,
                ErrorOutput = $"Registry: could not parse result JSON: {parseError}",
                Duration = raw.Duration,
            };
        }

        using (doc!)
        {
            var root = doc!.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var operation = root.TryGetProperty("operation", out var opEl) ? opEl.GetString() ?? "" : "";

            if (!ok) return BuildFailureResult(root, raw);

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            var display = ProjectOperationOutputs(operation, root, parameters);

            return new ActivityResult
            {
                Success = true,
                Output = display,
                ErrorOutput = raw.ErrorOutput,
                Duration = raw.Duration,
                OutputParameters = parameters,
            };
        }
    }

    private static ActivityResult BuildFailureResult(JsonElement root, ActivityResult raw)
    {
        var err = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
        return new ActivityResult
        {
            Success = false,
            Output = null,
            ErrorOutput = string.IsNullOrEmpty(err) ? raw.ErrorOutput : err,
            Duration = raw.Duration,
        };
    }

    private static string ProjectOperationOutputs(string operation, JsonElement root, Dictionary<string, string> parameters) =>
        operation switch
        {
            "read" => ProjectRead(root, parameters),
            "exists" => ProjectExists(root, parameters),
            "listsubkeys" => ProjectListSubKeys(root, parameters),
            "listvalues" => ProjectListValues(root, parameters),
            "write" => ProjectWrite(root, parameters),
            "createkey" => ProjectCreateKey(root, parameters),
            "deletevalue" or "deletekey" => "OK",
            _ => "",
        };

    private static string ProjectRead(JsonElement root, Dictionary<string, string> parameters)
    {
        if (root.TryGetProperty("value", out var vEl))
        {
            var valStr = PowerShellOperation.JsonElementToScalarString(vEl);
            parameters["value"] = valStr;
            if (root.TryGetProperty("type", out var tEl))
                parameters["type"] = tEl.GetString() ?? "";
            return valStr;
        }
        if (root.TryGetProperty("values", out var valuesEl))
        {
            parameters["values"] = valuesEl.GetRawText();
            var count = root.TryGetProperty("count", out var cEl) ? cEl.GetInt32() : 0;
            parameters["count"] = count.ToString();
            return valuesEl.GetRawText();
        }
        return "";
    }

    private static string ProjectExists(JsonElement root, Dictionary<string, string> parameters)
    {
        var exists = root.TryGetProperty("exists", out var eEl) && eEl.GetBoolean();
        parameters["exists"] = exists ? "true" : "false";
        return exists ? "True" : "False";
    }

    private static string ProjectListSubKeys(JsonElement root, Dictionary<string, string> parameters)
    {
        if (root.TryGetProperty("subKeys", out var sEl))
            parameters["subKeys"] = sEl.GetRawText();
        var subCount = root.TryGetProperty("count", out var scEl) ? scEl.GetInt32() : 0;
        parameters["count"] = subCount.ToString();
        return root.TryGetProperty("subKeys", out var sEl2) ? sEl2.GetRawText() : "[]";
    }

    private static string ProjectListValues(JsonElement root, Dictionary<string, string> parameters)
    {
        if (root.TryGetProperty("values", out var lvEl))
            parameters["values"] = lvEl.GetRawText();
        var lvCount = root.TryGetProperty("count", out var lvcEl) ? lvcEl.GetInt32() : 0;
        parameters["count"] = lvCount.ToString();
        return root.TryGetProperty("values", out var lvEl2) ? lvEl2.GetRawText() : "[]";
    }

    private static string ProjectWrite(JsonElement root, Dictionary<string, string> parameters)
    {
        if (root.TryGetProperty("type", out var wtEl))
            parameters["type"] = wtEl.GetString() ?? "";
        return "OK";
    }

    private static string ProjectCreateKey(JsonElement root, Dictionary<string, string> parameters)
    {
        var created = root.TryGetProperty("created", out var crEl) && crEl.GetBoolean();
        parameters["created"] = created ? "true" : "false";
        return created ? "Created" : "AlreadyExists";
    }

}
