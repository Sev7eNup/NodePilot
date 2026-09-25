"""PowerShell parity: a local script step must end like the same script on a target machine.

Four workflows. The two local ones run on every host. The two remote ones run the same
checks over WinRM against the registered machine named in the global
NP_TESTSUITE_REMOTE_MACHINE (name or hostname, with a default credential); without it they
are installed but left disabled.

Covered: templates anywhere in a word, upstream values with non-ASCII text, output as
object text without host/warning lines, exit codes after exit and return, the host's working
directory, the Windows modules of the local 5.1 process, the temp script being gone while
the script runs, a background program not holding the step, startProgram output in the OEM
code page and in UTF-8, and the failures: any error record, native stderr, throw (values
still published), parse errors, quoted words, an unrunnable condition and a program timeout.
"""

import base64

from suitelib import Step, Workflow, RUNS_ROOT
from spec_core import LOCAL, janitor, cid, mkrun, cleanup, assert_step, ok_return, ret, CID

REMOTE = "{{globals.NP_TESTSUITE_REMOTE_MACHINE}}"
REMOTE_REQUIRES = ["globals:NP_TESTSUITE_REMOTE_MACHINE"]
GREETING = "Grüße ✓ C:\\Täst"
CMD = r"C:\Windows\System32\cmd.exe"
POWERSHELL = r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
PING = r"C:\Windows\System32\PING.EXE"


def encoded(script):
    return "-NoProfile -NonInteractive -EncodedCommand " + base64.b64encode(
        script.encode("utf-16-le")).decode("ascii")


def case(cid_, dimension, value, **extra):
    entry = {"id": cid_, "dimension": dimension, "value": value}
    entry.update(extra)
    return entry


def seed(target, work_dir):
    """Publishes the values the variants feed back through templates."""
    return Step("seed", "Seed: values for the data bus", "runScript",
                {"engine": "auto", "timeoutSeconds": 30,
                 "script": "$dir = " + work_dir + "\n"
                           "$n = '50'\n"
                           "$name = 'report 1'\n"
                           "$greeting = '" + GREETING + "'\n"
                           "$hostName = $env:COMPUTERNAME\n"},
                target_machine=target)


# --- script bodies shared by the local and the remote workflow ---------------------------

WORDS = r"""
$mid = Write-Output C:\t\{{seed.param.n}}.txt
$start = Write-Output {{seed.param.n}}\app.txt
$ext = Write-Output {{seed.param.name}}.log
$two = Write-Output {{seed.param.n}}-{{seed.param.name}}.log
$upper = Write-Output {{seed.param.name}}.ToUpper()
$len = [string]({{seed.param.greeting}}.Length)
New-Item -ItemType Directory -Force -Path {{seed.param.dir}} | Out-Null
Set-Content -LiteralPath {{seed.param.dir}}\f-{{seed.param.n}}.txt -Value {{seed.param.greeting}} -Encoding UTF8
$read = (Get-Content -LiteralPath {{seed.param.dir}}\f-{{seed.param.n}}.txt -Encoding UTF8 -Raw).Trim()
$names = (Get-ChildItem -LiteralPath {{seed.param.dir}}).Name -join ','
"""

ROUND_TRIP = """
$echo = {{seed.param.greeting}}
$aliasEcho = $greeting
$fromHost = {{seed.param.hostName}}
$ranOn = $env:COMPUTERNAME
$literal = 'Größe'
$edition = [string]$PSVersionTable.PSEdition
$guid = (New-Guid).Guid
$hash = (Get-FileHash -InputStream ([IO.MemoryStream]::new())).Hash
Write-Output ($echo + '|' + $literal)
"""

OUTPUT_FORMAT = ("[pscustomobject]@{ a = 1 }\nWrite-Host 'host-line'\nWrite-Warning 'warn-line'\n"
                 "Write-Information 'info-line'\n'plain'\n42\n")

RETURN_NATIVE = "$x = 'a'\ncmd /c exit 3\nreturn\n$y = 'b'\n"


def startprogram_umlauts(prefix, target):
    return [
        Step(prefix + "c", "startProgram: cmd writes OEM", "startProgram",
             {"filePath": CMD, "arguments": "/c echo Größe", "timeoutSeconds": 60},
             target_machine=target,
             cases=[case("parity.%s.startProgram.oem-cmd" % prefix, "startProgram.stdout",
                         "umlauts from cmd (OEM code page)")]),
        Step(prefix + "p", "startProgram: powershell writes OEM", "startProgram",
             {"filePath": POWERSHELL, "arguments": encoded("'Größe'"), "timeoutSeconds": 60},
             target_machine=target,
             cases=[case("parity.%s.startProgram.oem-powershell" % prefix,
                         "startProgram.stdout", "umlauts from Windows PowerShell (OEM)")]),
        Step(prefix + "u", "startProgram: program writes UTF-8", "startProgram",
             {"filePath": POWERSHELL,
              "arguments": encoded("[Console]::OutputEncoding = [Text.Encoding]::UTF8; 'Größe'"),
              "timeoutSeconds": 60},
             target_machine=target,
             cases=[case("parity.%s.startProgram.utf8" % prefix, "startProgram.stdout",
                         "umlauts from a program that writes UTF-8")]),
    ]


STARTPROGRAM_ASSERT = """
foreach ($pair in @(@('{p}c', {{{{{p}c.param.stdout}}}}), @('{p}p', {{{{{p}p.param.stdout}}}}), @('{p}u', {{{{{p}u.param.stdout}}}}))) {{
  if ($pair[1].Trim() -ne 'Größe') {{ throw "startProgram $($pair[0]): expected 'Größe', got '$($pair[1])'" }}
}}
"""

WORDS_ASSERT = """
if ({p}.mid -ne 'C:\\t\\50.txt') {{ throw "template in a word: '$({p}.mid)'" }}
if ({p}.start -ne '50\\app.txt') {{ throw "template at a word start: '$({p}.start)'" }}
if ({p}.ext -ne 'report 1.log') {{ throw "template before .ext: '$({p}.ext)'" }}
if ({p}.two -ne '50-report 1.log') {{ throw "two templates in a word: '$({p}.two)'" }}
if ({p}.upper -ne 'REPORT 1') {{ throw "method call on a template: '$({p}.upper)'" }}
if ({p}.len -ne '{len}') {{ throw "member access in expression mode: '$({p}.len)'" }}
if ({p}.read -ne $greeting) {{ throw "file written through word templates read back '$({p}.read)'" }}
if ({p}.names -ne 'f-50.txt') {{ throw "file name from a word template: '$({p}.names)'" }}
"""


def words_params(step):
    fields = ["mid", "start", "ext", "two", "upper", "len", "read", "names"]
    return "$%s = @{ %s }" % (step, "; ".join(
        "%s = {{%s.param.%s}}" % (f, step, f) for f in fields))


# --- local ---------------------------------------------------------------------------------

def local_positive():
    work_dir = "Join-Path (Join-Path '" + RUNS_ROOT + "' " + CID + ") 'words'"
    steps = [
        janitor(), cid(), mkrun(), seed(LOCAL, work_dir),
        Step("p0", "runScript: templates in words", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "script": WORDS},
             target_machine=LOCAL,
             cases=[case("parity.local.template.word", "runScript.template",
                         "anywhere in a word, one argument")]),
        Step("p1", "runScript: umlauts and Windows modules", "runScript",
             {"engine": "auto", "timeoutSeconds": 60,
              "script": ROUND_TRIP + "$job = [string](Start-Job { 40 + 2 } | Wait-Job | Receive-Job)\n"},
             target_machine=LOCAL,
             cases=[case("parity.local.roundtrip", "runScript.encoding",
                         "upstream umlauts, New-Guid, Get-FileHash, Start-Job")]),
        Step("p2", "runScript: output is object text", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "script": OUTPUT_FORMAT},
             target_machine=LOCAL,
             cases=[case("parity.local.output", "runScript.output",
                         "ToString per object, no host/warning/information lines")]),
        Step("p3", "runScript: return after exit 3", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "script": RETURN_NATIVE},
             target_machine=LOCAL,
             cases=[case("parity.local.return", "runScript.exitCode",
                         "last native code after a top-level return")]),
        Step("p4", "runScript: exit 5", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "script": "$x = 'a'\nexit 5\n"},
             target_machine=LOCAL,
             cases=[case("parity.local.exit", "runScript.exitCode", "exit N in the 5.1 process")]),
        Step("p5", "runScript: isolated exit 6", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "isolated": True,
              "script": "$x = 'a'\nexit 6\n"},
             target_machine=LOCAL,
             cases=[case("parity.local.isolated-exit", "runScript.exitCode",
                         "exit N in an isolated process")]),
        Step("p6", "runScript: working directory (auto)", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "script": "$cwd = (Get-Location).Path\n"},
             target_machine=LOCAL,
             cases=[case("parity.local.cwd", "runScript.workingDirectory",
                         "the host's directory, as in the pool")]),
        Step("p7", "runScript: working directory (runspace)", "runScript",
             {"engine": "runspace", "timeoutSeconds": 30, "script": "$cwd = (Get-Location).Path\n"},
             target_machine=LOCAL),
        Step("p8", "runScript: temp script gone while running", "runScript",
             {"engine": "auto", "timeoutSeconds": 60,
              "script": "$cidText = " + CID + "\n"
                        "$left = [string]@(Get-ChildItem ([IO.Path]::GetTempPath()) -Filter 'nodepilot_*.ps1' |\n"
                        "  Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue) -like ('*' + $cidText + '*') }).Count\n"},
             target_machine=LOCAL,
             cases=[case("parity.local.tempfile", "runScript.tempScript",
                         "deleted as soon as PowerShell read it")]),
        Step("p9", "runScript: start a background program", "runScript",
             {"engine": "auto", "timeoutSeconds": 60,
              "script": "$p = Start-Process -FilePath '" + PING + "' -ArgumentList '-n 30 127.0.0.1' -NoNewWindow -PassThru\n"
                        "$programId = [string]$p.Id\n"},
             target_machine=LOCAL,
             cases=[case("parity.local.background", "runScript.background",
                         "a program started without a window does not hold the step",
                         assertedVia="p10")]),
        Step("p10", "runScript: program still running, stop it", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": "$programId = {{p9.param.programId}}\n"
                        "$stillRunning = [string][bool](Get-Process -Id $programId -ErrorAction SilentlyContinue)\n"
                        "Stop-Process -Id $programId -ErrorAction SilentlyContinue\n"},
             target_machine=LOCAL),
        Step("p11", "runScript: stderr merged inside the call", "runScript",
             {"engine": "auto", "timeoutSeconds": 60,
              "script": "$o = cmd /c \"dir C:\\nope-np-parity 2>&1\"\n$after = 'y'\n"},
             target_machine=LOCAL,
             cases=[case("parity.local.stderr-merged", "runScript.stderr",
                         "cmd /c \"tool 2>&1\" keeps the step green")]),
        Step("p12", "runScript: progress turned back on", "runScript",
             {"engine": "auto", "timeoutSeconds": 60,
              "script": "$ProgressPreference = 'Continue'\nWrite-Progress -Activity 'p' -Status 's'\n$done = 'y'\n"},
             target_machine=LOCAL,
             cases=[case("parity.local.progress", "runScript.error",
                         "no serialized progress in the error text")]),
    ] + startprogram_umlauts("s", LOCAL) + [
        Step("w0", "waitForCondition: script in Windows PowerShell", "waitForCondition",
             {"conditionType": "script", "intervalSeconds": 1, "timeoutSeconds": 20,
              "script": "$PSVersionTable.PSEdition -eq 'Desktop' -and [bool](Get-Command New-Guid)"},
             target_machine=LOCAL,
             cases=[case("parity.local.waitForCondition", "waitForCondition.engine",
                         "script conditions run in Windows PowerShell 5.1")]),
        cleanup(),
        assert_step(
            "$greeting = '" + GREETING + "'\n"
            + words_params("p0") + "\n"
            + WORDS_ASSERT.format(p="$p0", len=len(GREETING))
            + """
$echo = {{p1.param.echo}}
if ($echo -ne $greeting -or {{p1.param.aliasEcho}} -ne $greeting) { throw "upstream umlauts: '$echo'" }
if ({{p1.param.literal}} -ne 'Größe') { throw "script literal: '{{p1.param.literal}}'" }
if ({{p1.param.edition}} -ne 'Desktop') { throw "local default engine is not Windows PowerShell" }
if ({{p1.param.guid}} -notmatch '^[0-9a-f-]{36}$') { throw "New-Guid missing in the local 5.1 process" }
if ({{p1.param.job}} -ne '42') { throw "Start-Job in the local 5.1 process: '{{p1.param.job}}'" }
$lines = @(({{p2.output}}) -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (($lines -join '|') -ne '@{a=1}|plain|42') { throw "output format: '$($lines -join '|')'" }
if ({{p3.param.exitCode}} -ne '3' -or {{p3.param.x}} -ne 'a') { throw "return after exit 3: exitCode '{{p3.param.exitCode}}'" }
if ({{p4.param.exitCode}} -ne '5' -or {{p4.param.x}} -ne 'a') { throw "exit 5: '{{p4.param.exitCode}}'" }
if ({{p5.param.exitCode}} -ne '6' -or {{p5.param.x}} -ne 'a') { throw "isolated exit 6: '{{p5.param.exitCode}}'" }
if ({{p6.param.cwd}} -ne {{p7.param.cwd}}) { throw "working directory: auto '{{p6.param.cwd}}' vs runspace '{{p7.param.cwd}}'" }
if ({{p8.param.left}} -ne '0') { throw "the temp script still exists while the script runs" }
if ({{p10.param.stillRunning}} -ne 'True') { throw "the step waited for the background program" }
if ({{p11.param.after}} -ne 'y' -or {{p11.param.exitCode}} -ne '1') { throw "merged stderr: after '{{p11.param.after}}' exit '{{p11.param.exitCode}}'" }
if ({{p12.error}} -match 'CLIXML|<Objs') { throw "progress leaked into the error text" }
"""
            + STARTPROGRAM_ASSERT.format(p="s")
            + "if ({{w0.param.lastResult}} -ne 'true') { throw 'waitForCondition script' }\n"
            + "$assertOk = 'script parity'\n"),
        ok_return("script parity", with_cid=True),
    ]
    return Workflow(
        37, "script-parity", "[TestSuite] script parity",
        "A local script step ends like the same script on a target machine: templates in "
        "words, umlauts, Windows modules, output format, exit codes, working directory, the "
        "temp script, background programs, startProgram encodings and waitForCondition.",
        "positive", "continuous", "A", steps, max_runtime=150)


def local_negative():
    steps = [
        janitor(), cid(),
        _neg("n0", "runScript: non-terminating error", "runScript",
             {"engine": "auto", "timeoutSeconds": 60,
              "script": "$ErrorActionPreference = 'Continue'\nGet-Item C:\\nope-np-parity\n$x = '1'\n"},
             "parity.local.fail.non-terminating", "runScript.success",
             "any error record fails the step", "nope-np-parity"),
        _neg("n1", "runScript: Write-Error -ErrorAction Continue", "runScript",
             {"engine": "auto", "timeoutSeconds": 60,
              "script": "Write-Error 'parity-soft-error' -ErrorAction Continue\n$x = 'a'\n"},
             "parity.local.fail.write-error", "runScript.success",
             "Write-Error under Continue", "parity-soft-error"),
        _neg("n2", "runScript: native stderr", "runScript",
             {"engine": "auto", "timeoutSeconds": 60,
              "script": "cmd /c \"echo parity-native-warn 1>&2\"\n$after = 'y'\n"},
             "parity.local.fail.native-stderr", "runScript.stderr",
             "native stderr ends the script", "parity-native-warn"),
        _neg("n3", "runScript: throw", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "script": "$x = 'kept'\nthrow 'parity-boom'\n"},
             "parity.local.fail.throw", "runScript.success", "throw, values still published",
             "parity-boom"),
        _neg("n4", "runScript: parse error", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "script": "$x = 'ran'\nif (\n"},
             "parity.local.fail.parse", "runScript.success", "the script never runs", ""),
        _neg("n5", "runScript: return after exit 3, only 0 accepted", "runScript",
             {"engine": "auto", "timeoutSeconds": 60, "successExitCodes": "0",
              "script": "cmd /c exit 3\nreturn\n"},
             "parity.local.fail.return-exit", "runScript.successExitCodes",
             "the native code survives a return", ""),
        _neg("n6", "runScript: quoted word after a template", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": "Write-Output " + CID + "\\'x'\n"},
             "parity.local.fail.quoted-word", "runScript.template",
             "a word starting with a template may not add its own quotes", "contains quotes"),
        _neg("n7", "waitForCondition: condition cannot run", "waitForCondition",
             {"conditionType": "script", "script": "$true ? 1 : 0", "intervalSeconds": 1,
              "timeoutSeconds": 60},
             "parity.local.fail.condition", "waitForCondition.script",
             "fails at once, not after the timeout", "could not be evaluated"),
        _neg("n8", "startProgram: timeout", "startProgram",
             {"filePath": PING, "arguments": "-n 30 127.0.0.1", "timeoutSeconds": 3},
             "parity.local.fail.program-timeout", "startProgram.timeoutSeconds",
             "reported by the script with partial output", "timed out", LOCAL),
        Step("chk", "Check: what the failures left behind", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": "if ({{n0.error}} -match 'nodepilot_|At C:') { throw 'error text carries the wrapper' }\n"
                        "if ({{n3.param.x}} -ne 'kept') { throw 'throw lost the assigned value' }\n"
                        "if (({{n3.error}}).Trim() -ne 'parity-boom') { throw \"throw error text: '{{n3.error}}'\" }\n"
                        "if ('{{n2.param.after}}' -eq 'y') { throw 'native stderr did not stop the script' }\n"
                        "if ({{n7.param.attempts}} -ne '1') { throw 'the condition was polled more than once' }\n"
                        "if ({{n8.param.stdout}} -notmatch '127\\.0\\.0\\.1') { throw 'program timeout lost the output' }\n"
                        "$checked = 'ok'\n"},
             target_machine=LOCAL),
        ret({"contract": "negative", "area": "script parity"}),
    ]
    for s in steps:
        if s.activity in ("runScript",) and s.target_machine is None:
            s.target_machine = LOCAL
    return Workflow(
        84, "script-parity", "[TestSuite-Neg] script parity",
        "Failures a local script step shares with a remote one: any error record, native "
        "stderr, throw with its values kept, parse errors, the exit code after return, a "
        "quoted word, an unrunnable condition and a program timeout.",
        "negative", "continuous", "B", steps, max_runtime=180)


def _neg(sid, label, activity, config, case_id, dimension, value, error_contains,
         target=None):
    return Step(sid, label, activity, config, target_machine=target,
                cases=[{"id": case_id, "dimension": dimension, "value": value,
                        "expectedFailure": {"stepId": sid, "errorContains": error_contains}}])


# --- remote --------------------------------------------------------------------------------

def remote_positive():
    # Seeded locally, so every remote step proves the local -> remote data bus; the folder is
    # a path on the target.
    work_dir = "'C:\\Windows\\Temp\\np-parity-' + " + CID
    steps = [
        cid(), seed(LOCAL, work_dir),
        Step("r0", "remote runScript: templates in words", "runScript",
             {"engine": "auto", "timeoutSeconds": 120,
              "script": WORDS + "Remove-Item -LiteralPath {{seed.param.dir}} -Recurse -Force\n"},
             target_machine=REMOTE,
             cases=[case("parity.remote.template.word", "runScript.template",
                         "anywhere in a word, over WinRM")]),
        Step("r1", "remote runScript: umlauts and modules", "runScript",
             {"engine": "auto", "timeoutSeconds": 120, "script": ROUND_TRIP},
             target_machine=REMOTE,
             cases=[case("parity.remote.roundtrip", "runScript.encoding",
                         "upstream umlauts and Windows modules over WinRM")]),
        Step("r2", "remote runScript: output is object text", "runScript",
             {"engine": "auto", "timeoutSeconds": 120, "script": OUTPUT_FORMAT},
             target_machine=REMOTE,
             cases=[case("parity.remote.output", "runScript.output",
                         "the reference the local output is held to")]),
        Step("r3", "remote runScript: return after exit 3", "runScript",
             {"engine": "auto", "timeoutSeconds": 120, "script": RETURN_NATIVE},
             target_machine=REMOTE,
             cases=[case("parity.remote.return", "runScript.exitCode",
                         "last native code after a top-level return")]),
    ] + startprogram_umlauts("t", REMOTE) + [
        Step("l0", "local runScript: remote program output", "runScript",
             {"engine": "runspace", "timeoutSeconds": 30,
              "script": "$typed = {{tc.param.stdout}}\n"},
             target_machine=LOCAL,
             cases=[case("parity.remote.databus", "runScript.template",
                         "a remote program's output read back in the local pool")]),
        Step("w0", "remote waitForCondition: script", "waitForCondition",
             {"conditionType": "script", "intervalSeconds": 2, "timeoutSeconds": 60,
              "script": "$PSVersionTable.PSEdition -eq 'Desktop' -and [bool](Get-Command New-Guid)"},
             target_machine=REMOTE,
             cases=[case("parity.remote.waitForCondition", "waitForCondition.engine",
                         "script condition over WinRM")]),
        assert_step(
            "$greeting = '" + GREETING + "'\n"
            + words_params("r0") + "\n"
            + WORDS_ASSERT.format(p="$r0", len=len(GREETING))
            + """
if ({{r1.param.fromHost}} -ne {{seed.param.hostName}}) { throw "local value on the target: '{{r1.param.fromHost}}'" }
if ({{r1.param.ranOn}} -eq {{seed.param.hostName}}) { throw 'the remote steps ran on the NodePilot host itself' }
if ({{r1.param.echo}} -ne $greeting -or {{r1.param.aliasEcho}} -ne $greeting) { throw "remote umlauts: '{{r1.param.echo}}'" }
if ({{r1.param.literal}} -ne 'Größe') { throw "remote script literal: '{{r1.param.literal}}'" }
if ({{r1.param.guid}} -notmatch '^[0-9a-f-]{36}$') { throw 'New-Guid missing on the target' }
$lines = @(({{r2.output}}) -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (($lines -join '|') -ne '@{a=1}|plain|42') { throw "remote output format: '$($lines -join '|')'" }
if ({{r3.param.exitCode}} -ne '3' -or {{r3.param.x}} -ne 'a') { throw "remote return after exit 3: '{{r3.param.exitCode}}'" }
if (({{l0.param.typed}}).Trim() -ne 'Größe') { throw "remote program output in the local pool: '{{l0.param.typed}}'" }
"""
            + STARTPROGRAM_ASSERT.format(p="t")
            + "if ({{w0.param.lastResult}} -ne 'true') { throw 'remote waitForCondition script' }\n"
            + "$assertOk = 'script parity remote'\n"),
        ok_return("script parity remote", with_cid=True),
    ]
    return Workflow(
        38, "script-parity-remote", "[TestSuite] script parity remote",
        "The reference side of the parity checks: the same scripts over WinRM on the machine "
        "named in NP_TESTSUITE_REMOTE_MACHINE, plus startProgram encodings on the target and "
        "its output read back locally.",
        "positive", "integration", "C", steps, max_runtime=600, requires=REMOTE_REQUIRES)


def remote_negative():
    steps = [
        _neg("n0", "remote runScript: native stderr", "runScript",
             {"engine": "auto", "timeoutSeconds": 120,
              "script": "cmd /c \"echo parity-native-warn 1>&2\"\n$after = 'y'\n"},
             "parity.remote.fail.native-stderr", "runScript.stderr",
             "native stderr ends the script over WinRM", "parity-native-warn", REMOTE),
        _neg("n1", "remote runScript: throw", "runScript",
             {"engine": "auto", "timeoutSeconds": 120, "script": "$x = 'kept'\nthrow 'parity-boom'\n"},
             "parity.remote.fail.throw", "runScript.success",
             "throw over WinRM, values still published", "parity-boom", REMOTE),
        _neg("n2", "remote runScript: non-terminating error", "runScript",
             {"engine": "auto", "timeoutSeconds": 120,
              "script": "$ErrorActionPreference = 'Continue'\nGet-Item C:\\nope-np-parity\n$x = '1'\n"},
             "parity.remote.fail.non-terminating", "runScript.success",
             "any error record fails the step over WinRM", "nope-np-parity", REMOTE),
        _neg("n3", "remote startProgram: timeout", "startProgram",
             {"filePath": PING, "arguments": "-n 30 127.0.0.1", "timeoutSeconds": 3},
             "parity.remote.fail.program-timeout", "startProgram.timeoutSeconds",
             "reported by the script, not by a stopped pipeline", "timed out", REMOTE),
        _neg("n4", "remote waitForCondition: condition cannot run", "waitForCondition",
             {"conditionType": "script", "script": "$true ? 1 : 0", "intervalSeconds": 1,
              "timeoutSeconds": 8},
             "parity.remote.fail.condition", "waitForCondition.script",
             "keeps polling and names the last error", "Last error", REMOTE),
        Step("chk", "Check: what the failures left behind", "runScript",
             {"engine": "auto", "timeoutSeconds": 30,
              "script": "if ('{{n0.param.after}}' -eq 'y') { throw 'native stderr did not stop the remote script' }\n"
                        "if ({{n1.param.x}} -ne 'kept') { throw 'remote throw lost the assigned value' }\n"
                        "if (({{n1.error}}).Trim() -ne 'parity-boom') { throw \"remote throw error text: '{{n1.error}}'\" }\n"
                        "if ({{n3.param.stdout}} -notmatch '127\\.0\\.0\\.1') { throw 'remote program timeout lost the output' }\n"
                        "$checked = 'ok'\n"},
             target_machine=LOCAL),
        ret({"contract": "negative", "area": "script parity remote"}),
    ]
    return Workflow(
        86, "script-parity-remote", "[TestSuite-Neg] script parity remote",
        "The reference failures over WinRM: native stderr, throw with its values kept, "
        "a non-terminating error, a program timeout and an unrunnable condition.",
        "negative", "integration", "C", steps, max_runtime=300, requires=REMOTE_REQUIRES)


def workflows():
    return [local_positive(), local_negative(), remote_positive(), remote_negative()]
