using System.Text;
using System.Text.Json;
using NodePilot.Core.Agents;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Agents;

internal static class AgentFileTools
{
    internal static string ValidatePath(string path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\?", StringComparison.Ordinal)
            || path.StartsWith("\\\\.", StringComparison.Ordinal) || path.Skip(2).Contains(':'))
            throw new UnauthorizedAccessException("A regular absolute file path is required.");
        var full = Path.GetFullPath(path);
        if (!roots.Any(root => Path.IsPathFullyQualified(root) &&
            (full.Equals(Path.GetFullPath(root).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
             || full.StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            throw new UnauthorizedAccessException("Path is outside this tool's allowed paths.");
        return full;
    }

    private static string Preamble(string path) => $$"""
        $ErrorActionPreference='Stop'
        $p={{PowerShellOperation.Literal(path)}}
        $check=$p
        while($check){
            if(Test-Path -LiteralPath $check){
                $item=Get-Item -LiteralPath $check -Force
                if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse points are not allowed by this file tool.'}
            }
            $next=[IO.Path]::GetDirectoryName($check)
            if($next -eq $check){break}; $check=$next
        }
        """;

    internal static async Task<string> ListAsync(AgentTarget target, string path, string filter, CancellationToken ct)
    {
        if (filter.Length > 128 || filter.Contains('/') || filter.Contains('\\')) throw new ArgumentException("Invalid file filter.");
        return await target.ExecuteAsync(Preamble(path) + "\nGet-ChildItem -LiteralPath $p -Filter " + PowerShellOperation.Literal(filter)
            + " -Force | Select-Object -First 100 FullName,Length,@{Name='LastWriteTimeUtc';Expression={$_.LastWriteTimeUtc.ToString('o',[Globalization.CultureInfo]::InvariantCulture)}},PSIsContainer | ConvertTo-Json -Compress", ct);
    }

    internal static async Task<byte[]> ReadBlockAsync(AgentTarget target, string path, long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count is < 1 or > AgentArtifactStore.TransferBlockBytes) throw new ArgumentException("Invalid file range.");
        var response = await target.ExecuteAsync(Preamble(path) + $$"""

            $f=[IO.File]::Open($p,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            try {
                [void]$f.Seek({{offset}},[IO.SeekOrigin]::Begin)
                $b=[byte[]]::new({{count}}); $n=$f.Read($b,0,$b.Length)
                [Convert]::ToBase64String($b,0,$n)
            } finally {$f.Dispose()}
            """, ct);
        if (response.Length > count * 2 + 1024) throw new IOException("Transfer block is larger than requested.");
        var bytes = Convert.FromBase64String(response.Trim());
        if (bytes.Length > count) throw new IOException("Transfer block is larger than requested.");
        return bytes;
    }

    internal static async Task<AgentArtifactStore.Artifact> CollectAsync(AgentTarget target, AgentArtifactStore store, string path, CancellationToken ct)
    {
        var lengthText = await target.ExecuteAsync(Preamble(path) + "\n(Get-Item -LiteralPath $p -Force).Length", ct);
        if (!long.TryParse(lengthText.Trim(), out var length)) throw new IOException("Target did not return a file length.");
        return await store.CollectAsync(target.Hostname + ":" + path, length,
            (offset, count, token) => ReadBlockAsync(target, path, offset, count, token), ct,
            token => ReadPrefixHashAsync(target, path, length, token));
    }

    // Validate the observed prefix rather than the current whole file so append-only logs remain collectable.
    private static async Task<byte[]> ReadPrefixHashAsync(AgentTarget target, string path, long length, CancellationToken ct)
    {
        var response = await target.ExecuteAsync(Preamble(path) + $$"""

            $f=[IO.File]::Open($p,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $hash=[Security.Cryptography.SHA256]::Create()
            try {
                $remaining=[long]{{length}}
                if($f.Length -lt $remaining){throw 'Source was truncated during collection.'}
                $b=[byte[]]::new({{AgentArtifactStore.TransferBlockBytes}})
                while($remaining -gt 0){
                    $n=$f.Read($b,0,[int][Math]::Min($b.Length,$remaining))
                    if($n -eq 0){throw 'Source was truncated during collection.'}
                    [void]$hash.TransformBlock($b,0,$n,$b,0)
                    $remaining-=$n
                }
                [void]$hash.TransformFinalBlock([byte[]]::new(0),0,0)
                [Convert]::ToBase64String($hash.Hash)
            } finally {$hash.Dispose();$f.Dispose()}
            """, ct);
        var bytes = Convert.FromBase64String(response.Trim());
        if (bytes.Length != 32) throw new IOException("Target did not return a valid source hash.");
        return bytes;
    }

    internal static async Task<string> ReadAsync(AgentTarget target, string path, long offset, CancellationToken ct)
    {
        // Small lookahead distinguishes a page boundary from EOF. Raw collection continues
        // using ReadBlockAsync unchanged; text pages advance only past complete characters.
        var bytes = await ReadBlockAsync(target, path, offset, 8196, ct);
        var header = offset == 0 ? bytes : await ReadBlockAsync(target, path, 0, 2, ct);
        var encoding = header.Length >= 2 && header[0] == 0xff && header[1] == 0xfe ? Encoding.Unicode
            : header.Length >= 2 && header[0] == 0xfe && header[1] == 0xff ? Encoding.BigEndianUnicode : Encoding.UTF8;
        var count = Math.Min(bytes.Length, 8192);
        if (encoding != Encoding.UTF8 && offset % 2 != 0)
            throw new ArgumentException("UTF-16 text offsets must be at a two-byte character boundary.");
        if (bytes.Length > count)
        {
            if (encoding == Encoding.UTF8)
            {
                var start = count - 1;
                while (start >= 0 && (bytes[start] & 0xc0) == 0x80) start--;
                if (start >= 0)
                {
                    var lead = bytes[start];
                    var width = lead is >= 0xc2 and <= 0xdf ? 2
                        : lead is >= 0xe0 and <= 0xef ? 3 : lead is >= 0xf0 and <= 0xf4 ? 4 : 1;
                    if (count - start < width) count = start;
                }
            }
            else
            {
                count -= count % 2;
                var last = encoding == Encoding.Unicode
                    ? bytes[count - 2] | bytes[count - 1] << 8
                    : bytes[count - 2] << 8 | bytes[count - 1];
                if (last is >= 0xd800 and <= 0xdbff) count -= 2;
            }
        }
        return JsonSerializer.Serialize(new { path, offset, nextOffset = offset + count, text = encoding.GetString(bytes, 0, count) });
    }

    internal static async Task WriteAsync(AgentTarget target, string path, string content, CancellationToken ct)
    {
        if (content.Length > 32_000) throw new ArgumentException("File writes are limited to 32,000 characters per call.");
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        await target.ExecuteAsync(Preamble(path) + "\n[IO.File]::WriteAllBytes($p,[Convert]::FromBase64String('" + data + "'))", ct);
    }

    internal static Task<string> SearchAsync(AgentTarget target, string path, string query, CancellationToken ct, string order = "first")
    {
        if (query.Length is < 1 or > 1024) throw new ArgumentException("Search text must contain 1–1024 characters.");
        return target.ExecuteAsync(BuildSearchScript(path, query, order), ct);
    }

    // Search on the target before transferring raw logs. ReadLine would allocate unbounded
    // strings for minified JSON/log records, so keep overlapping 8K character fragments.
    internal static string BuildSearchScript(string path, string query, string order = "first")
    {
        if (order is not ("first" or "last")) throw new ArgumentException("Search order must be first or last.");
        return Preamble(path) + $$"""

        $q={{PowerShellOperation.Literal(query)}}
        $order={{PowerShellOperation.Literal(order)}}
        if([IO.Directory]::Exists($p)){throw 'files_search requires a file path, not a directory. Use files_list to select a file first.'}
        $file=[IO.File]::Open($p,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader=[IO.StreamReader]::new($file,[Text.Encoding]::UTF8,$true,8192)
        $fragment=[Text.StringBuilder]::new()
        $state=@{Count=0L;Length=0;Rows=[Collections.Generic.Queue[string]]::new()}
        $record={param($text,$at,$line,$fragmentStart,$lineEnd)
            $state.Count++
            if($order -eq 'first' -and $state.Count -gt 20){return}
            $before=[Math]::Min(160,1100-$q.Length)
            $start=if($text.Length -le 1100){0}else{[Math]::Max(0,$at-$before)}; $length=[Math]::Min(1100,$text.Length-$start)
            $clippedBefore=($fragmentStart+$start) -gt 0; $clippedAfter=($start+$length -lt $text.Length -or -not $lineEnd)
            $row=$p+':'+$line+': '+$(if($clippedBefore){'[earlier characters omitted] '})+$text.Substring($start,$length)+$(if($clippedAfter){' [later characters omitted]'})
            if($order -eq 'first' -and $state.Length+$row.Length -gt 14000){return}
            $state.Rows.Enqueue($row); $state.Length+=$row.Length
            while($state.Rows.Count -gt 20 -or $state.Length -gt 14000){$state.Length-=$state.Rows.Dequeue().Length}
        }
        $buffer=[char[]]::new(8192); $line=1L; $lastMatch=0L; $fragmentStart=0L
        # Bound work even if the source keeps growing; byte length is a conservative character budget.
        $remaining=$file.Length; $stopped=$false
        try {
            while($remaining -gt 0 -and -not $stopped -and ($n=$reader.Read($buffer,0,[int][Math]::Min($buffer.Length,$remaining))) -gt 0){
                $remaining-=$n
                $chunk=[string]::new($buffer,0,$n); $i=0
                while($i -lt $n){
                    $newline=$chunk.IndexOf([char]10,$i)
                    $available=if($newline -ge 0){$newline-$i}else{$n-$i}
                    $take=[int][Math]::Min($available,8192-$fragment.Length)
                    [void]$fragment.Append($chunk,$i,$take); $i+=$take
                    $lineEnd=$i -eq $newline
                    if($lineEnd -or $fragment.Length -ge 8192){
                        $text=$fragment.ToString(); $at=$text.IndexOf($q,[StringComparison]::OrdinalIgnoreCase)
                        if($at -ge 0 -and $lastMatch -ne $line){
                            & $record $text $at $line $fragmentStart $lineEnd
                            $lastMatch=$line
                        }
                        if($lineEnd){[void]$fragment.Clear(); $line++; $i++; $fragmentStart=0L}
                        else{$remove=[Math]::Max(0,$fragment.Length-$q.Length);[void]$fragment.Remove(0,$remove);$fragmentStart+=$remove}
                    }
                    if($order -eq 'first' -and $state.Count -gt $state.Rows.Count){$stopped=$true; break}
                }
            }
            if(-not $stopped -and $fragment.Length -gt 0 -and $lastMatch -ne $line){
                $text=$fragment.ToString(); $at=$text.IndexOf($q,[StringComparison]::OrdinalIgnoreCase)
                if($at -ge 0){
                    & $record $text $at $line $fragmentStart $true
                }
            }
            $scanLimited=$remaining -eq 0 -and -not $reader.EndOfStream
            'Search order='+$order+'; returned='+$state.Rows.Count+'; truncated='+($state.Count -gt $state.Rows.Count)+'; scanLimited='+$scanLimited+'. File order is not event time. Marked character omissions require reading the original source context.'
            if($state.Rows.Count -eq 0){'No matching text found in the scanned portion.'}else{$state.Rows.ToArray()}
            if($stopped){'More matches exist. Use order=last for current appended logs, or narrow the query. Do not infer current health from these first matches.'}
        } finally {$reader.Dispose(); $file.Dispose()}
        """;
    }
}
