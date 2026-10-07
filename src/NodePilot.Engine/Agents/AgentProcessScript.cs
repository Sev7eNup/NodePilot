using System.Text;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Agents;

internal static class AgentProcessScript
{
    internal static string Build(string shell, string command, string? directory, string? bashPath, int timeoutSeconds = 300)
    {
        if (command.Length > 64_000) throw new ArgumentException("Shell command exceeds 64,000 characters.");
        var shellCommand = shell switch
        {
            "powershell" => "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); " + command,
            "cmd" => "@\"%SystemRoot%\\System32\\chcp.com\" 65001 >nul\r\n" + command,
            _ => command
        };
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(shellCommand));
        var file = shell switch
        {
            "powershell" => "$env:SystemRoot + '\\System32\\WindowsPowerShell\\v1.0\\powershell.exe'",
            "cmd" => "$env:SystemRoot + '\\System32\\cmd.exe'",
            "bash" when !string.IsNullOrWhiteSpace(bashPath) && Path.IsPathFullyQualified(bashPath) => PowerShellOperation.Literal(bashPath),
            "bash" => "[IO.Path]::Combine($env:ProgramFiles,'Git','bin','bash.exe')",
            _ => throw new ArgumentException("Unsupported agent shell.")
        };
        return $$"""
            $ErrorActionPreference='Stop'
            $observedAt=[DateTimeOffset]::Now
            $timeContext=@{observedAtUtc=$observedAt.UtcDateTime.ToString('o',[Globalization.CultureInfo]::InvariantCulture);targetTimeZoneId=[TimeZoneInfo]::Local.Id;observedUtcOffset=$observedAt.ToString('zzz',[Globalization.CultureInfo]::InvariantCulture)}
            $command=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{encoded}}'))
            $scriptPath=$null
            $psi=New-Object Diagnostics.ProcessStartInfo
            $psi.FileName={{file}}
            if(-not [IO.File]::Exists($psi.FileName)){throw 'The selected shell is not installed on the target.'}
            $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true
            $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
            $psi.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
            $psi.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
            $dir={{PowerShellOperation.Literal(directory ?? "")}}
            if($dir){$psi.WorkingDirectory=$dir}
            $shell='{{shell}}'
            if($shell -eq 'powershell'){
                $psi.Arguments='-NoLogo -NoProfile -NonInteractive -EncodedCommand {{encoded}}'
                # The embedded host can expose only its PowerShell SDK modules to child processes.
                $windowsModules=[IO.Path]::Combine([IO.Path]::GetDirectoryName($psi.FileName),'Modules')
                $psi.EnvironmentVariables['PSModulePath']=$windowsModules+[IO.Path]::PathSeparator+$psi.EnvironmentVariables['PSModulePath']
            }
            else {
                if($shell -eq 'bash'){
                    foreach($key in @($psi.EnvironmentVariables.Keys)){
                        if($key -in @('BASH_ENV','ENV','SHELLOPTS','BASHOPTS') -or $key.StartsWith('BASH_FUNC_')){$psi.EnvironmentVariables.Remove($key)}
                    }
                }
                $extension=if($shell -eq 'cmd'){'.cmd'}else{'.sh'}
                $scriptPath=[IO.Path]::Combine([IO.Path]::GetTempPath(),'NodePilot-Agent-Command-'+[Guid]::NewGuid().ToString('N')+$extension)
                [IO.File]::WriteAllText($scriptPath,$command,(New-Object Text.UTF8Encoding($false)))
                if($shell -eq 'cmd'){$psi.Arguments='/d /s /c ""'+$scriptPath+'""'}
                else {$psi.Arguments='--noprofile --norc "'+$scriptPath.Replace('\','/')+'"'}
            }
            $p=New-Object Diagnostics.Process; $p.StartInfo=$psi
            $out=New-Object Text.StringBuilder; $err=New-Object Text.StringBuilder
            $truncated=$false; $timedOut=$false; $code=$null
            try {
                [void]$p.Start()
                $streams=@(
                    @{Reader=$p.StandardOutput; Buffer=[char[]]::new(4096); Text=$out; Pending=$null},
                    @{Reader=$p.StandardError; Buffer=[char[]]::new(4096); Text=$err; Pending=$null}
                )
                foreach($s in $streams){$s.Pending=$s.Reader.ReadAsync($s.Buffer,0,$s.Buffer.Length)}
                $clock=[Diagnostics.Stopwatch]::StartNew(); $exitAt=$null
                while($true){
                    foreach($s in $streams){
                        if($null -ne $s.Pending -and $s.Pending.IsCompleted){
                            $n=$s.Pending.GetAwaiter().GetResult()
                            if($n -eq 0){$s.Pending=$null}
                            else {
                                $take=[Math]::Min($n,16000-$s.Text.Length)
                                if($take -gt 0){[void]$s.Text.Append($s.Buffer,0,$take)}
                                if($take -lt $n){$truncated=$true}
                                $s.Pending=$s.Reader.ReadAsync($s.Buffer,0,$s.Buffer.Length)
                            }
                        }
                    }
                    if($p.HasExited){
                        if($null -eq $exitAt){$exitAt=$clock.ElapsedMilliseconds}
                        if(($streams | Where-Object {$null -ne $_.Pending}).Count -eq 0 -or ($clock.ElapsedMilliseconds-$exitAt) -ge 2000){break}
                    }
                    if($clock.ElapsedMilliseconds -ge {{timeoutSeconds * 1000}}){$timedOut=$true;break}
                    [Threading.Thread]::Sleep(10)
                }
                if($p.HasExited){$code=$p.ExitCode}
            } finally {
                try { if(-not $p.HasExited){ & "$env:SystemRoot\System32\taskkill.exe" /PID $p.Id /T /F 2>$null | Out-Null } } catch {}
                $p.Dispose()
                if($scriptPath -and [IO.File]::Exists($scriptPath)){[IO.File]::Delete($scriptPath)}
            }
            @{exitCode=$code;stdout=$out.ToString();stderr=$err.ToString();truncated=$truncated;timedOut=$timedOut;timeContext=$timeContext}|ConvertTo-Json -Depth 3 -Compress
            """;
    }
}
