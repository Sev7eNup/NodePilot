using System.Text.RegularExpressions;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Agents;

// Resolve a share on the bound machine without opening an SMB connection or delegating credentials.
internal sealed record AgentLocalSharePath(string Value)
{
    internal static bool IsValid(string path)
    {
        if (!Regex.IsMatch(path, @"\A\\\\[a-z0-9][a-z0-9.-]{0,252}\\[^\\/:*?""<>|\x00-\x1f]+(?:\\[^/:*?""<>|\x00-\x1f]*)*\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) return false;
        return path[2..].Split('\\').All(part => part is not ("." or "..") && !part.EndsWith('.') && !part.EndsWith(' '));
    }

    internal string Render() => "(Resolve-NodePilotLocalSharePath " + PowerShellOperation.Literal(Value) + ")";

    internal const string Preamble = """
        function Resolve-NodePilotLocalSharePath([string]$path) {
            $parts=$path.Substring(2).Split('\')
            $computer=CimCmdlets\Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
            $names=@('localhost','127.0.0.1',$env:COMPUTERNAME,$computer.Name,$computer.DNSHostName)
            if($computer.Domain){$names+=($computer.DNSHostName+'.'+$computer.Domain)}
            $names+=@(NetTCPIP\Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Microsoft.PowerShell.Utility\Select-Object -ExpandProperty IPAddress)
            if($parts[0] -notin $names){throw 'UNC reads are limited to a share on the bound machine. Delegate to a member bound to the owning server; no SMB connection was attempted.'}
            $shares=@(CimCmdlets\Get-CimInstance -ClassName Win32_Share -ErrorAction Stop | Microsoft.PowerShell.Core\Where-Object Name -EQ $parts[1])
            if($shares.Count -ne 1 -or $shares[0].Type -notin @(0,2147483648)){throw 'A unique local disk share was not found; this is not proof of a missing source directory.'}
            $root=[IO.Path]::GetFullPath($shares[0].Path).TrimEnd('\')
            if($root -notmatch '^[a-zA-Z]:(\\|$)' -or $root.Substring(2).Contains(':')){throw 'Share does not map to a regular local directory.'}
            $suffix=($parts | Microsoft.PowerShell.Utility\Select-Object -Skip 2) -join '\'
            $resolved=[IO.Path]::GetFullPath($root+'\'+$suffix)
            if($resolved.TrimEnd('\') -ne $root -and -not $resolved.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes the local share.'}
            $check=$resolved
            while($check){
                if(Microsoft.PowerShell.Management\Test-Path -LiteralPath $check -ErrorAction Stop){
                    $item=Microsoft.PowerShell.Management\Get-Item -LiteralPath $check -Force -ErrorAction Stop
                    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse points are not allowed in local share reads.'}
                }
                $next=[IO.Path]::GetDirectoryName($check)
                if($next -eq $check){break}; $check=$next
            }
            $resolved
        }
        """;
}
