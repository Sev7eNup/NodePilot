using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Line-oriented text-file editing on a remote or local target: append, prepend, insert,
/// delete, replace, replaceLine. The edit runs as a PowerShell script, so one code path covers
/// WinRM targets and the in-process engine, and the emitted script is visible in step details.
///
/// Encoding is taken from the BOM (UTF-8, UTF-16-LE, UTF-16-BE), falling back to UTF-8 without
/// BOM, and reused on write so the file keeps its shape; the encoding config key overrides
/// detection. Line endings follow the first <c>\r\n</c> or <c>\n</c> in the source unless
/// <c>lineEnding</c> forces <c>crlf</c> or <c>lf</c>.
///
/// The whole file is read into memory, so <c>maxFileSizeMB</c> (default 50, configurable via
/// <c>FileSystemOperation:TextEdit:MaxFileSizeMB</c>) caps the size this activity accepts.
///
/// Every mutation is written to <c>&lt;path&gt;.nodepilot-tmp-&lt;guid&gt;</c> and then
/// <c>Move-Item -Force</c>d over the original, so a failed write leaves the original untouched.
/// <c>backupSuffix</c> copies the file to a sibling <i>before</i> the mutation, which keeps the
/// pre-edit content available even after a successful edit.
/// </summary>
public class TextFileEditActivity : BaseRemoteActivity
{
    public override string ActivityType => "textFileEdit";

    // A short marker token keeps the wrapper noise small for tooling that streams step output
    // incrementally (SignalR). PostProcess looks for these exact strings.
    private static readonly PowerShellOperationMarkers ResultMarkers = PowerShellOperation.Markers("TEXTEDIT");

    private readonly IConfiguration _config;

    // Canonical lowercase operation tokens. The set is case-insensitive so config authors can
    // write "replaceLine" or "REPLACELINE"; the value is lowercased before validation and before
    // it reaches PowerShell, so both switches compare against the lowercase form.
    private static readonly HashSet<string> KnownOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "append", "prepend", "insert", "delete", "replace", "replaceline",
    };

    private static readonly HashSet<string> KnownEncodings = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "utf8", "utf8-bom", "utf16le", "utf16be", "ascii",
    };

    private static readonly HashSet<string> KnownLineEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        "preserve", "crlf", "lf",
    };

    public TextFileEditActivity(
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        PowerShellEngineFactory engineFactory,
        IConfiguration config)
        : base(sessionFactory, credentialStore, db, engineFactory, config)
    {
        _config = config;
    }

    protected override string BuildScript(JsonElement config, StepExecutionContext context)
    {
        var operation = config.GetStringOrNull("operation");
        if (string.IsNullOrWhiteSpace(operation))
            throw new InvalidOperationException(
                "Text File Edit: 'operation' is required (append, prepend, insert, delete, replace, replaceLine)");
        if (!KnownOperations.Contains(operation))
            throw new InvalidOperationException($"Text File Edit: unknown operation '{operation}'");

        var path = config.GetStringOrNull("path");
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Text File Edit: 'path' is required");
        PathGuard.Validate(_config, path, allowWildcards: false);

        var encoding = config.GetString("encoding", "auto");
        if (!KnownEncodings.Contains(encoding))
            throw new InvalidOperationException(
                $"Text File Edit: unknown encoding '{encoding}'. Allowed: auto, utf8, utf8-bom, utf16le, utf16be, ascii.");

        var lineEnding = config.GetString("lineEnding", "preserve");
        if (!KnownLineEndings.Contains(lineEnding))
            throw new InvalidOperationException(
                $"Text File Edit: unknown lineEnding '{lineEnding}'. Allowed: preserve, crlf, lf.");

        var createIfMissing = config.GetBool("createIfMissing", false);
        var dryRun = config.GetBool("dryRun", false);
        var backupSuffix = config.GetStringOrNull("backupSuffix");
        if (!string.IsNullOrEmpty(backupSuffix))
            PathGuard.ValidateLeafName(SyntheticLeafName(path, backupSuffix));

        var maxFileSizeMb = config.GetOptionalPositiveInt("maxFileSizeMB")
            ?? ResolveDefaultMaxFileSizeMb(_config);

        // Per-operation config validation, so config errors surface before a session is opened.
        // The PowerShell side checks file state (existence, line number in range) separately.
        var op = operation!.ToLowerInvariant();
        ValidateOpRequirements(op, config);

        var lineNumber = config.GetOptionalPositiveInt("lineNumber");
        var (rangeFrom, rangeTo) = ReadLineRange(config);

        var matchPattern = config.GetStringOrNull("matchPattern");
        var replacement = config.GetStringOrNull("replace");
        var content = config.GetStringOrNull("content");
        var useRegex = config.GetBool("useRegex", false);
        var ignoreCase = config.GetBool("ignoreCase", false);
        var occurrences = config.GetString("occurrences", "all").ToLowerInvariant();
        if (occurrences != "all" && occurrences != "first")
            throw new InvalidOperationException(
                $"Text File Edit: unknown occurrences '{occurrences}'. Allowed: all, first.");
        var appendIfMissing = config.GetBool("appendIfMissing", false);
        var appendIfMissingExact = config.GetBool("appendIfMissingExact", true);

        var qPath = PowerShellOperation.Literal(path);
        var qBackupSuffix = PowerShellOperation.Literal(backupSuffix ?? string.Empty);
        var qEncoding = PowerShellOperation.Literal(encoding);
        var qLineEnding = PowerShellOperation.Literal(lineEnding);
        var qContent = PowerShellOperation.Literal(content);
        var qMatch = PowerShellOperation.Literal(matchPattern);
        var qReplace = PowerShellOperation.Literal(replacement);
        var qOperation = PowerShellOperation.Literal(op);

        var lineNumberLit = lineNumber?.ToString() ?? "0";
        var rangeFromLit = rangeFrom?.ToString() ?? "0";
        var rangeToLit = rangeTo?.ToString() ?? "0";
        var targetPathGuard = TargetPathGuardScript.Build(
            _config,
            ("$__path", "path"),
            ("$__path + $__backupSuffix", "backupPath"));

        return $$"""
            $ErrorActionPreference = 'Stop'
            $__path = {{qPath}}
            $__op = {{qOperation}}
            $__encodingMode = {{qEncoding}}
            $__lineEndingMode = {{qLineEnding}}
            $__backupSuffix = {{qBackupSuffix}}
            $__content = {{qContent}}
            $__matchPattern = {{qMatch}}
            $__replacement = {{qReplace}}
            $__lineNumber = {{lineNumberLit}}
            $__rangeFrom = {{rangeFromLit}}
            $__rangeTo = {{rangeToLit}}
            $__useRegex = ${{(useRegex ? "true" : "false")}}
            $__ignoreCase = ${{(ignoreCase ? "true" : "false")}}
            $__occurrencesAll = ${{(occurrences == "all" ? "true" : "false")}}
            $__createIfMissing = ${{(createIfMissing ? "true" : "false")}}
            $__dryRun = ${{(dryRun ? "true" : "false")}}
            $__appendIfMissing = ${{(appendIfMissing ? "true" : "false")}}
            $__appendIfMissingExact = ${{(appendIfMissingExact ? "true" : "false")}}
            $__maxFileSizeBytes = {{maxFileSizeMb}}L * 1024 * 1024
            $__result = [ordered]@{
                operation = $__op
                path = $__path
                ok = $true
                dryRun = $__dryRun
                linesBefore = 0
                linesAfter = 0
                linesChanged = 0
                encoding = $null
                lineEnding = $null
                backupPath = $null
                summary = $null
            }
            try {
            {{targetPathGuard}}
            {{TextEditPowerShellBody}}
            } catch {
                $__result.ok = $false
                $__result.error = $_.Exception.Message
            }
            {{ResultMarkers.RenderJsonEnvelope("$__result", depth: 4)}}
            """;
    }

    private static (int? from, int? to) ReadLineRange(JsonElement config)
    {
        if (!config.TryGetProperty("lineRange", out var range)) return (null, null);
        if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2)
            throw new InvalidOperationException(
                "Text File Edit: 'lineRange' must be a two-element array [from, to] (1-based, inclusive)");

        var fromEl = range[0];
        var toEl = range[1];
        if (!fromEl.TryGetInt32(out var fromV) || !toEl.TryGetInt32(out var toV))
            throw new InvalidOperationException("Text File Edit: 'lineRange' values must be integers");
        if (fromV < 1 || toV < 1)
            throw new InvalidOperationException("Text File Edit: 'lineRange' values must be ≥ 1");
        if (toV < fromV)
            throw new InvalidOperationException("Text File Edit: 'lineRange' must be ordered [from, to] with to ≥ from");

        return (fromV, toV);
    }

    private static void ValidateOpRequirements(string op, JsonElement config)
    {
        var hasContent = !string.IsNullOrEmpty(config.GetStringOrNull("content"));
        var hasLineNumber = config.GetOptionalPositiveInt("lineNumber") is not null;
        var hasLineRange = config.TryGetProperty("lineRange", out var lr) && lr.ValueKind == JsonValueKind.Array;
        var hasMatchPattern = !string.IsNullOrEmpty(config.GetStringOrNull("matchPattern"));

        switch (op)
        {
            case "append":
            case "prepend":
                if (!hasContent)
                    throw new InvalidOperationException($"Text File Edit '{op}' requires 'content'");
                break;
            case "insert":
                if (!hasContent)
                    throw new InvalidOperationException("Text File Edit 'insert' requires 'content'");
                if (!hasLineNumber)
                    throw new InvalidOperationException("Text File Edit 'insert' requires 'lineNumber' (≥ 1)");
                break;
            case "replaceline":
                if (!hasContent)
                    throw new InvalidOperationException("Text File Edit 'replaceLine' requires 'content'");
                if (!hasLineNumber)
                    throw new InvalidOperationException("Text File Edit 'replaceLine' requires 'lineNumber' (≥ 1)");
                break;
            case "delete":
                var count = (hasLineNumber ? 1 : 0) + (hasLineRange ? 1 : 0) + (hasMatchPattern ? 1 : 0);
                if (count == 0)
                    throw new InvalidOperationException(
                        "Text File Edit 'delete' requires exactly one of 'lineNumber', 'lineRange', or 'matchPattern'");
                if (count > 1)
                    throw new InvalidOperationException(
                        "Text File Edit 'delete' accepts only one of 'lineNumber', 'lineRange', 'matchPattern' — not multiple at once");
                break;
            case "replace":
                if (!hasMatchPattern)
                    throw new InvalidOperationException("Text File Edit 'replace' requires 'matchPattern'");
                if (config.GetStringOrNull("replace") is null)
                    throw new InvalidOperationException(
                        "Text File Edit 'replace' requires 'replace' (use an empty string to delete matches)");
                break;
        }
    }

    private static int ResolveDefaultMaxFileSizeMb(IConfiguration config)
    {
        var raw = config["FileSystemOperation:TextEdit:MaxFileSizeMB"];
        if (string.IsNullOrWhiteSpace(raw)) return 50;
        return int.TryParse(raw, out var v) && v > 0 ? v : 50;
    }

    /// <summary>
    /// Builds a representative leaf name from a path plus the backup suffix so
    /// <see cref="PathGuard.ValidateLeafName"/> can refuse a suffix that slips in a separator,
    /// a traversal segment, or a reserved Windows device name. The real sibling path is
    /// resolved on the PowerShell side.
    /// </summary>
    private static string SyntheticLeafName(string path, string backupSuffix)
    {
        var slash = path.LastIndexOfAny(['\\', '/']);
        var stem = slash >= 0 ? path[(slash + 1)..] : path;
        if (string.IsNullOrEmpty(stem)) stem = "file";
        return stem + backupSuffix;
    }

    protected override ActivityResult PostProcess(ActivityResult raw, JsonElement config)
    {
        if (!TryParseResultEnvelope(raw, ResultMarkers, "Text File Edit", out var doc, out var passthrough))
            return passthrough!;

        using (doc!)
        {
            var root = doc!.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();

            if (!ok)
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

            var parameters = PowerShellOperation.MapObjectFields(root,
                ("operation", "operation"),
                ("path", "path"),
                ("linesBefore", "linesBefore"),
                ("linesAfter", "linesAfter"),
                ("linesChanged", "linesChanged"),
                ("encoding", "encoding"),
                ("lineEnding", "lineEnding"),
                ("backupPath", "backupPath"),
                ("dryRun", "dryRun"));

            var summary = root.TryGetProperty("summary", out var sEl) ? sEl.GetString() : null;
            return new ActivityResult
            {
                Success = true,
                Output = summary ?? "OK",
                ErrorOutput = raw.ErrorOutput,
                Duration = raw.Duration,
                OutputParameters = parameters,
            };
        }
    }

    // PowerShell body spliced into the try { } block in BuildScript. It is a plain constant so
    // the interpolated raw string in BuildScript cannot consume its curly braces. Per-call
    // values are bound to the $__-prefixed PowerShell variables in the wrapper.
    private const string TextEditPowerShellBody = """
        function Read-NpFile {
            param([string]$Path, [string]$EncodingMode, [long]$MaxBytes)
            if (-not (Test-Path -LiteralPath $Path)) { return $null }
            if ((Test-Path -LiteralPath $Path -PathType Container)) {
                throw "Not a file: " + $Path
            }
            $size = (Get-Item -LiteralPath $Path).Length
            if ($size -gt $MaxBytes) {
                throw "File exceeds maxFileSizeMB cap: " + $Path + " (size=" + $size + " bytes, cap=" + $MaxBytes + ")"
            }
            $bytes = [System.IO.File]::ReadAllBytes($Path)
            $enc = $null
            $bom = 0
            if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
                $enc = New-Object System.Text.UTF8Encoding($true); $bom = 3; $detected = 'utf8-bom'
            } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
                $enc = [System.Text.Encoding]::Unicode; $bom = 2; $detected = 'utf16le'
            } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
                $enc = [System.Text.Encoding]::BigEndianUnicode; $bom = 2; $detected = 'utf16be'
            } else {
                $enc = New-Object System.Text.UTF8Encoding($false); $detected = 'utf8'
            }
            switch ($EncodingMode) {
                'utf8'      { $enc = New-Object System.Text.UTF8Encoding($false); $detected = 'utf8' }
                'utf8-bom'  { $enc = New-Object System.Text.UTF8Encoding($true);  $detected = 'utf8-bom' }
                'utf16le'   { $enc = [System.Text.Encoding]::Unicode;             $detected = 'utf16le' }
                'utf16be'   { $enc = [System.Text.Encoding]::BigEndianUnicode;    $detected = 'utf16be' }
                'ascii'     { $enc = [System.Text.Encoding]::ASCII;               $detected = 'ascii' }
                default     { } # auto: leave detection alone
            }
            $text = $enc.GetString($bytes, $bom, $bytes.Length - $bom)
            $hasCrlf = $text.Contains("`r`n")
            $hasLf = $text.IndexOf("`n") -ge 0
            if ($hasCrlf) {
                $ending = 'crlf'
            } elseif ($hasLf) {
                $ending = 'lf'
            } else {
                $ending = 'crlf' # empty / single-line file: default to platform-native
            }
            return [pscustomobject]@{
                Encoding = $enc
                EncodingName = $detected
                LineEnding = $ending
                Text = $text
                ExistedBefore = $true
            }
        }

        function Split-NpLines {
            param([string]$Text)
            if ([string]::IsNullOrEmpty($Text)) { return ,@() }
            # Splits on CRLF first, then LF — preserves both flavors interleaved if present.
            return ,([regex]::Split($Text, "`r`n|`n"))
        }

        function Join-NpLines {
            param([string[]]$Lines, [string]$Ending)
            $sep = if ($Ending -eq 'crlf') { "`r`n" } else { "`n" }
            return [string]::Join($sep, $Lines)
        }

        function Write-NpFile {
            param([string]$Path, [string]$Text, [object]$Encoding, [bool]$DryRun)
            if ($DryRun) { return }
            $tmp = $Path + '.nodepilot-tmp-' + ([guid]::NewGuid().ToString('N'))
            $bytes = $Encoding.GetPreamble() + $Encoding.GetBytes($Text)
            [System.IO.File]::WriteAllBytes($tmp, $bytes)
            Move-Item -LiteralPath $tmp -Destination $Path -Force
        }

        function Backup-NpFile {
            param([string]$Path, [string]$Suffix, [bool]$DryRun)
            if ([string]::IsNullOrEmpty($Suffix)) { return $null }
            $backup = $Path + $Suffix
            if (-not $DryRun) {
                Copy-Item -LiteralPath $Path -Destination $backup -Force
            }
            return $backup
        }

        $__file = Read-NpFile -Path $__path -EncodingMode $__encodingMode -MaxBytes $__maxFileSizeBytes
        $__createdHere = $false
        if ($null -eq $__file) {
            if (-not $__createIfMissing) {
                throw "File does not exist: " + $__path + " (set createIfMissing=true to allow creation)"
            }
            if ($__op -ne 'append' -and $__op -ne 'prepend') {
                throw "createIfMissing is only supported for append/prepend, not '" + $__op + "'"
            }
            $__enc = if ($__encodingMode -eq 'utf8-bom') {
                New-Object System.Text.UTF8Encoding($true)
            } elseif ($__encodingMode -eq 'utf16le') {
                [System.Text.Encoding]::Unicode
            } elseif ($__encodingMode -eq 'utf16be') {
                [System.Text.Encoding]::BigEndianUnicode
            } elseif ($__encodingMode -eq 'ascii') {
                [System.Text.Encoding]::ASCII
            } else {
                New-Object System.Text.UTF8Encoding($false)
            }
            $__file = [pscustomobject]@{
                Encoding = $__enc
                EncodingName = if ($__encodingMode -eq 'auto') { 'utf8' } else { $__encodingMode }
                LineEnding = 'crlf'
                Text = ''
                ExistedBefore = $false
            }
            $__createdHere = $true
        }

        $__finalEnding = if ($__lineEndingMode -eq 'preserve') { $__file.LineEnding } else { $__lineEndingMode }

        $__lines = Split-NpLines -Text $__file.Text
        # Get-Content / Split-NpLines: a trailing newline produces an empty final element
        # which represents "the implicit empty line after the last terminator". We strip it
        # for line-count math and re-add it on write if the original had one.
        $__hadTrailingNewline = $false
        if ($__lines.Length -gt 0 -and $__lines[-1] -eq '' -and ($__file.Text.EndsWith("`n") -or $__file.Text.EndsWith("`r"))) {
            $__hadTrailingNewline = $true
            $__lines = $__lines[0..($__lines.Length - 2)]
        } elseif ($__file.Text -eq '') {
            $__lines = @()
        }
        $__result.linesBefore = $__lines.Length
        $__result.encoding = $__file.EncodingName
        $__result.lineEnding = $__finalEnding

        $__changed = 0
        # [string[]] cast is load-bearing: [regex]::Split returns a 1-element array for
        # single-line content, and PowerShell unwraps a 1-element array assigned out of an
        # if-expression to a scalar string — then $__contentLines[0] is a [char] and .Trim()
        # blows up ("[System.Char] does not contain a method named 'Trim'"). Forcing the
        # array type keeps indexing and .Length correct for the single-line case.
        [string[]]$__contentLines = if ([string]::IsNullOrEmpty($__content)) { @('') } else { [regex]::Split($__content, "`r`n|`n") }

        switch ($__op) {
            'append' {
                # appendIfMissing skip-detection has two modes — the toggle in the UI is
                # exposed as "Trim-exact match" vs "Substring-match" (label in
                # TextFileEditConfig.tsx), so the engine has to honor that semantic split:
                #   true  → trim both sides then compare with -eq (whitespace-tolerant,
                #           strict line equality). Right for /etc/hosts where the user
                #           wants exactly one entry per host.
                #   false → case-insensitive substring search via IndexOf. Right for
                #           patches where the variable column (IP) shouldn't break the
                #           idempotency check ("if any line CONTAINS `hostname.lan`, skip").
                # The previous implementation ran -eq on both sides without trim, which
                # did not match either UI promise and was effectively a no-op toggle.
                $__skip = $false
                if ($__appendIfMissing -and $__contentLines.Length -gt 0) {
                    $__needle = if ($__appendIfMissingExact) { $__contentLines[0].Trim() } else { $__contentLines[0] }
                    foreach ($l in $__lines) {
                        if ($__appendIfMissingExact) {
                            if ($l.Trim() -eq $__needle) { $__skip = $true; break }
                        } else {
                            if ($l.IndexOf($__needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                                $__skip = $true; break
                            }
                        }
                    }
                }
                if (-not $__skip) {
                    $__lines = @($__lines) + $__contentLines
                    $__changed = $__contentLines.Length
                }
            }
            'prepend' {
                $__lines = @($__contentLines) + @($__lines)
                $__changed = $__contentLines.Length
            }
            'insert' {
                if ($__lineNumber -lt 1) { throw "insert: lineNumber must be ≥ 1" }
                $__idx = [Math]::Min($__lineNumber - 1, $__lines.Length)
                $__before = if ($__idx -gt 0) { $__lines[0..($__idx - 1)] } else { @() }
                $__after = if ($__idx -lt $__lines.Length) { $__lines[$__idx..($__lines.Length - 1)] } else { @() }
                $__lines = @($__before) + @($__contentLines) + @($__after)
                $__changed = $__contentLines.Length
            }
            'replaceline' {
                if ($__lineNumber -lt 1 -or $__lineNumber -gt $__lines.Length) {
                    throw "replaceLine: lineNumber $__lineNumber is out of range (file has $($__lines.Length) lines)"
                }
                $__idx = $__lineNumber - 1
                $__before = if ($__idx -gt 0) { $__lines[0..($__idx - 1)] } else { @() }
                $__after = if (($__idx + 1) -lt $__lines.Length) { $__lines[($__idx + 1)..($__lines.Length - 1)] } else { @() }
                $__lines = @($__before) + @($__contentLines) + @($__after)
                $__changed = 1
            }
            'delete' {
                if ($__lineNumber -gt 0) {
                    if ($__lineNumber -gt $__lines.Length) {
                        throw "delete: lineNumber $__lineNumber is out of range (file has $($__lines.Length) lines)"
                    }
                    $__idx = $__lineNumber - 1
                    $__before = if ($__idx -gt 0) { $__lines[0..($__idx - 1)] } else { @() }
                    $__after = if (($__idx + 1) -lt $__lines.Length) { $__lines[($__idx + 1)..($__lines.Length - 1)] } else { @() }
                    $__lines = @($__before) + @($__after)
                    $__changed = 1
                } elseif ($__rangeFrom -gt 0) {
                    if ($__rangeFrom -gt $__lines.Length) {
                        throw "delete: lineRange [$__rangeFrom, $__rangeTo] starts past end of file (file has $($__lines.Length) lines)"
                    }
                    $__effectiveTo = [Math]::Min($__rangeTo, $__lines.Length)
                    $__before = if ($__rangeFrom -gt 1) { $__lines[0..($__rangeFrom - 2)] } else { @() }
                    $__after = if ($__effectiveTo -lt $__lines.Length) { $__lines[$__effectiveTo..($__lines.Length - 1)] } else { @() }
                    $__lines = @($__before) + @($__after)
                    $__changed = $__effectiveTo - $__rangeFrom + 1
                } else {
                    # match-pattern path: filter rather than rebuild — preserves order while
                    # counting removed entries. We literal-match unless useRegex is set.
                    $__re = $null
                    if ($__useRegex) {
                        $__opts = if ($__ignoreCase) { [System.Text.RegularExpressions.RegexOptions]::IgnoreCase } else { [System.Text.RegularExpressions.RegexOptions]::None }
                        $__re = New-Object System.Text.RegularExpressions.Regex($__matchPattern, $__opts)
                    }
                    $__kept = New-Object System.Collections.Generic.List[string]
                    foreach ($l in $__lines) {
                        $__hit = if ($__useRegex) {
                            $__re.IsMatch($l)
                        } elseif ($__ignoreCase) {
                            $l.IndexOf($__matchPattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
                        } else {
                            $l.Contains($__matchPattern)
                        }
                        if ($__hit) { $__changed++ } else { $__kept.Add($l) }
                    }
                    $__lines = $__kept.ToArray()
                }
            }
            'replace' {
                # Replace runs against the whole text (so multi-line regex like `(?s).+` work)
                # and recomputes the line array afterwards. linesChanged here means
                # *occurrence* count, not line-count delta — that's the more useful number
                # for downstream branches (`success && param.linesChanged > 0`).
                $__originalText = Join-NpLines -Lines $__lines -Ending $__finalEnding
                if ($__useRegex) {
                    $__opts = [System.Text.RegularExpressions.RegexOptions]::None
                    if ($__ignoreCase) { $__opts = $__opts -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }
                    $__re = New-Object System.Text.RegularExpressions.Regex($__matchPattern, $__opts)
                    if ($__occurrencesAll) {
                        $__changed = $__re.Matches($__originalText).Count
                        $__newText = $__re.Replace($__originalText, $__replacement)
                    } else {
                        $__changed = if ($__re.IsMatch($__originalText)) { 1 } else { 0 }
                        $__newText = $__re.Replace($__originalText, $__replacement, 1)
                    }
                } else {
                    $__comparison = if ($__ignoreCase) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
                    if ($__occurrencesAll) {
                        # Loop to count and rebuild — String.Replace(String,String,Comparison)
                        # has only existed since .NET Core; do it manually to be portable to
                        # Windows-PowerShell-5.1 targets.
                        $__sb = New-Object System.Text.StringBuilder
                        $__cursor = 0
                        $__pattern = $__matchPattern
                        $__plen = $__pattern.Length
                        while ($true) {
                            $__hit = $__originalText.IndexOf($__pattern, $__cursor, $__comparison)
                            if ($__hit -lt 0) { [void]$__sb.Append($__originalText.Substring($__cursor)); break }
                            [void]$__sb.Append($__originalText.Substring($__cursor, $__hit - $__cursor))
                            [void]$__sb.Append($__replacement)
                            $__cursor = $__hit + $__plen
                            $__changed++
                        }
                        $__newText = $__sb.ToString()
                    } else {
                        $__hit = $__originalText.IndexOf($__matchPattern, $__comparison)
                        if ($__hit -ge 0) {
                            $__newText = $__originalText.Substring(0, $__hit) + $__replacement + $__originalText.Substring($__hit + $__matchPattern.Length)
                            $__changed = 1
                        } else {
                            $__newText = $__originalText
                            $__changed = 0
                        }
                    }
                }
                # Track whether the replacement produced a trailing newline in $__newText —
                # otherwise the Split below strips the implicit empty final element and the
                # materialize step can't tell anymore whether the file should end on `\n`.
                # Re-anchoring $__hadTrailingNewline here fixes both directions:
                #   (a) source had no trailing newline + replace introduced one  → keep it
                #   (b) source had a trailing newline + replace stripped it       → drop it
                # Other operations (append/prepend/insert/delete/replaceLine) preserve the
                # original line-array shape and don't need this re-anchor.
                $__newTextHasTrailingNewline = ($__newText.EndsWith("`n") -or $__newText.EndsWith("`r"))
                $__lines = Split-NpLines -Text $__newText
                if ($__lines.Length -gt 0 -and $__lines[-1] -eq '' -and $__newTextHasTrailingNewline) {
                    $__lines = $__lines[0..($__lines.Length - 2)]
                }
                $__hadTrailingNewline = $__newTextHasTrailingNewline
            }
        }

        $__result.linesAfter = $__lines.Length
        $__result.linesChanged = $__changed

        # Materialize and write. Trailing newline is preserved when the source had one,
        # or always added on append (POSIX convention — `tail -f` etc. expect a final NL).
        $__final = Join-NpLines -Lines $__lines -Ending $__finalEnding
        $__shouldEndNewline = $__hadTrailingNewline -or $__op -eq 'append' -or $__createdHere
        if ($__shouldEndNewline -and $__final.Length -gt 0) {
            $__sep = if ($__finalEnding -eq 'crlf') { "`r`n" } else { "`n" }
            if (-not $__final.EndsWith($__sep)) { $__final += $__sep }
        }

        if ($__file.ExistedBefore -and $__changed -gt 0 -and -not $__dryRun) {
            $__result.backupPath = Backup-NpFile -Path $__path -Suffix $__backupSuffix -DryRun $__dryRun
        } elseif (-not $__file.ExistedBefore) {
            # No backup for a freshly-created file — there's nothing to preserve.
            $__result.backupPath = $null
        }

        Write-NpFile -Path $__path -Text $__final -Encoding $__file.Encoding -DryRun $__dryRun

        $__result.summary = if ($__dryRun) {
            "dryRun: " + $__op + " would change " + $__changed + " line(s) in " + $__path
        } else {
            $__op + ": " + $__changed + " line(s) changed in " + $__path
        }
        """;
}
