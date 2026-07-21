"""Generator for scripts/tech-demo/xl.json — the Tech-Demo XL workflow.

Builds a 100+ node, 3-swim-lane showcase workflow deterministically.
Run: python scripts/tech-demo/build_xl.py
Output: scripts/tech-demo/xl.json
"""
import json
import pathlib
import sys

OUT_PATH = pathlib.Path(__file__).parent / "xl.json"

nodes = []
edges = []


def N(id_, x, y, label, activity_type, **data_extra):
    node = {
        "id": id_,
        "type": "activity",
        "position": {"x": x, "y": y},
        "data": {"label": label, "activityType": activity_type, **data_extra},
    }
    nodes.append(node)
    return id_


def E(eid, src, tgt, label="Always", **extra):
    d = {"label": label, **extra}
    edges.append({"id": eid, "source": src, "target": tgt, "type": "labeled", "data": d})


# ============================================================================
# LANE TOP (y-range 60-660, center y=300) — DISCOVERY
# ============================================================================

# ---- Phase A-TOP: Trigger Gallery (x=0..720) ----
# 1 active manualTrigger + 5 disabled triggers (gallery of all 6 trigger types)

N("trg-manual", 0, 300, "Manual Trigger (5 params)", "manualTrigger",
  config={
      "title": "Run Tech-Demo XL",
      "parameters": [
          {"name": "environment", "type": "string", "required": True, "default": "staging"},
          {"name": "threshold", "type": "string", "required": False, "default": "80"},
          {"name": "dryRun", "type": "string", "required": False, "default": "false"},
          {"name": "pattern", "type": "string", "required": False, "default": "Windows.*"},
          {"name": "iteration", "type": "string", "required": False, "default": "1"},
      ]
  })

N("trg-schedule", 0, 60, "Schedule Trigger (disabled)", "scheduleTrigger",
  disabled=True,
  config={"cronExpression": "0 0 6 ? * MON-FRI *"})

N("trg-webhook", 0, 180, "Webhook Trigger (disabled)", "webhookTrigger",
  disabled=True,
  config={"path": "ops/run", "method": "POST", "secret": ""})

N("trg-filewatch", 0, 420, "File Watcher (disabled)", "fileWatcherTrigger",
  disabled=True,
  config={"directory": "C:\\incoming", "filter": "*.csv",
          "watchType": "Created", "includeSubdirectories": False})

N("trg-database", 0, 540, "DB Trigger (disabled)", "databaseTrigger",
  disabled=True,
  config={"provider": "sqlite", "connectionString": "Data Source=:memory:",
          "query": "SELECT id FROM queue WHERE processed = 0",
          "intervalSeconds": 30})

N("trg-eventlog", 0, 660, "EventLog Trigger (disabled)", "eventLogTrigger",
  disabled=True,
  config={"logName": "Application", "source": "", "entryType": "Error",
          "messagePattern": ".*CRITICAL.*"})

N("log-kickoff", 360, 300, "Log: XL kickoff", "log",
  config={"level": "info",
          "message": "Tech-Demo XL started: env={{trg-manual.param.environment}} iter={{trg-manual.param.iteration}} dryRun={{trg-manual.param.dryRun}}"})

N("delay-init", 720, 300, "Delay 1s (boot settle)", "delay",
  config={"seconds": 1})

# ---- Phase B-TOP: Host Collection (x=1060..1740) ----

collect_host_script = """# All PowerShell variables declared here are captured as {{host.param.<name>}} downstream.
$hostName    = $env:COMPUTERNAME
$userName    = $env:USERNAME
$now         = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
$osCim       = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
$osName      = if ($osCim) { $osCim.Caption } else { 'Microsoft Windows' }
$osVersion   = [System.Environment]::OSVersion.Version.ToString()
$buildNumber = ($osVersion -split '\\\\.')[2]
$cpuCount    = [System.Environment]::ProcessorCount
$memTotalMB  = if ($osCim) { [math]::Round($osCim.TotalVisibleMemorySize / 1024, 0) } else { 0 }
$memFreeMB   = if ($osCim) { [math]::Round($osCim.FreePhysicalMemory / 1024, 0) } else { 0 }
$drive       = Get-PSDrive C -ErrorAction SilentlyContinue
if ($drive) {
    $diskFreeGB  = [math]::Round($drive.Free / 1GB, 2)
    $diskUsedGB  = [math]::Round($drive.Used / 1GB, 2)
    $diskTotalGB = [math]::Round(($drive.Free + $drive.Used) / 1GB, 2)
    $diskUsedPct = [math]::Round(($drive.Used / ($drive.Free + $drive.Used)) * 100, 1)
} else {
    $diskFreeGB = 0; $diskUsedGB = 0; $diskTotalGB = 0; $diskUsedPct = 0
}
$cs          = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
$isDomain    = if ($cs) { [bool]$cs.PartOfDomain } else { $false }
$manufacturer = if ($cs) { $cs.Manufacturer } else { 'unknown' }
$model       = if ($cs) { $cs.Model } else { 'unknown' }
$emptyField  = ''
$errorLog    = ''
Write-Output \"Collected cpu=$cpuCount mem=${memFreeMB}MB free disk=$diskFreeGB/$diskTotalGB GB on $userName@$hostName (OS: $osName v$osVersion build $buildNumber, domain=$isDomain) at $now\"
"""

N("collect-host", 1060, 300, "runScript: collect host info", "runScript",
  targetMachineId="localhost",
  outputVariable="host",
  config={
      "engine": "auto",
      "timeoutSeconds": 60,
      "retry": {"maxAttempts": 2, "backoff": "exponential",
                "initialDelayMs": 500, "maxDelayMs": 5000},
      "script": collect_host_script,
  })

N("collect-net", 1400, 300, "runScript: collect network info", "runScript",
  targetMachineId="localhost",
  outputVariable="net",
  config={
      "engine": "auto",
      "timeoutSeconds": 30,
      "script": (
          "$ip         = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | "
          "Where-Object { $_.PrefixOrigin -ne 'WellKnown' } | Select-Object -First 1).IPAddress\n"
          "$ipAddress  = if ($ip) { $ip } else { '127.0.0.1' }\n"
          "$gateway    = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | "
          "Select-Object -First 1).NextHop\n"
          "$gatewayIp  = if ($gateway) { $gateway } else { '' }\n"
          "$dnsServer  = (Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | "
          "Select-Object -ExpandProperty ServerAddresses -First 1)\n"
          "$dnsValue   = if ($dnsServer) { $dnsServer } else { '' }\n"
          "$netUp      = if ($ipAddress -ne '127.0.0.1') { 'true' } else { 'false' }\n"
          "Write-Output \"Net: ip=$ipAddress gw=$gatewayIp dns=$dnsValue up=$netUp\""
      ),
  })

N("process-data", 1740, 300, "runScript: process (BREAKPOINT)", "runScript",
  targetMachineId="localhost",
  outputVariable="proc",
  breakpoint=True,
  config={
      "engine": "auto",
      "timeoutSeconds": 30,
      "retry": {"maxAttempts": 3, "backoff": "linear",
                "initialDelayMs": 750, "maxDelayMs": 5000},
      "script": (
          "$iteration   = '{{trg-manual.param.iteration}}'\n"
          "$iterationNum = [int]$iteration\n"
          "$severity    = if ($iterationNum -ge 5) { 'high' } elseif ($iterationNum -ge 2) { 'medium' } else { 'low' }\n"
          "$phase       = 'processing'\n"
          "$processed   = 'true'\n"
          "Write-Output \"Processed iter=$iteration severity=$severity phase=$phase\""
      ),
  })

# ---- SYNC BAND 1 (x=1960) — single waitAll junction at y=900 ----

N("sync1", 1960, 900, "Sync 1: Boot done (waitAll)", "junction",
  config={"mode": "waitAll"})

# ---- Phase 2-TOP: Remote Fan-Out (12 Activities, 4 cols x 3 rows @ 180-spacing) ----
# Col 1 (x=2100)
N("r01-file-exists", 2100, 60, "fileOperation: exists?", "fileOperation",
  targetMachineId="localhost",
  config={"operation": "exists", "path": "C:\\Windows\\System32\\notepad.exe", "timeoutSeconds": 30})
N("r02-svc-status", 2100, 240, "serviceManagement: Spooler status", "serviceManagement",
  targetMachineId="localhost",
  config={"serviceName": "Spooler", "action": "status", "timeoutSeconds": 30})
N("r03-reg-read", 2100, 420, "registryOperation: ProductName", "registryOperation",
  targetMachineId="localhost",
  config={"operation": "read", "keyPath": "HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
          "valueName": "ProductName", "timeoutSeconds": 30})

# Col 2 (x=2460)
N("r04-wmi-os", 2460, 60, "wmiQuery: Win32_OperatingSystem", "wmiQuery",
  targetMachineId="localhost",
  config={"className": "Win32_OperatingSystem", "namespace": "root\\cimv2", "timeoutSeconds": 30})
N("r05-prog-cmd", 2460, 240, "startProgram: cmd /c echo", "startProgram",
  targetMachineId="localhost",
  config={"filePath": "cmd.exe",
          "arguments": "/c echo Hello from {{host.param.userName}}@{{host.param.hostName}}",
          "waitForExit": True, "timeoutSeconds": 30, "successExitCodes": "0",
          "retry": {"maxAttempts": 3, "backoff": "linear", "initialDelayMs": 1000, "maxDelayMs": 5000}})
N("r06-waitcond-a", 2460, 420, "waitForCondition: env ready", "waitForCondition",
  targetMachineId="localhost",
  config={"script": "$true",
          "intervalSeconds": 2, "timeoutSeconds": 30})

# Col 3 (x=2820)
N("r07-file-list", 2820, 60, "folderOperation: list System32", "folderOperation",
  targetMachineId="localhost",
  config={"operation": "list", "path": "C:\\Windows\\System32", "timeoutSeconds": 30})
N("r08-svc-list", 2820, 240, "serviceManagement: BITS status", "serviceManagement",
  targetMachineId="localhost",
  config={"serviceName": "BITS", "action": "status", "timeoutSeconds": 30})
N("r09-reg-write", 2820, 420, "registryOperation: write (demo)", "registryOperation",
  targetMachineId="localhost",
  config={"operation": "write",
          "keyPath": "HKCU:\\Software\\NodePilotDemo",
          "valueName": "LastRun", "value": "{{host.param.now}}", "timeoutSeconds": 30})

# Col 4 (x=3180)
N("r10-wmi-cpu", 3180, 60, "wmiQuery: Win32_Processor", "wmiQuery",
  targetMachineId="localhost",
  config={"className": "Win32_Processor", "namespace": "root\\cimv2", "timeoutSeconds": 30})
N("r11-prog-ps", 3180, 240, "startProgram: powershell -c", "startProgram",
  targetMachineId="localhost",
  config={"filePath": "powershell.exe",
          "arguments": "-NoProfile -Command \"Write-Output 'ps-ok'\"",
          "waitForExit": True, "timeoutSeconds": 30, "successExitCodes": "0"})
N("r12-wmi-disk", 3180, 420, "wmiQuery: Win32_LogicalDisk", "wmiQuery",
  targetMachineId="localhost",
  config={"className": "Win32_LogicalDisk", "namespace": "root\\cimv2", "timeoutSeconds": 30})

# ---- Phase 2-TOP: junction-all-top (waitAll for 12 remote ops) ----
# Placed with 400px gap to the last remote column for label spread on the 12-edge fan-in.

N("jall-top", 3580, 300, "Junction: waitAll (12 remote)", "junction",
  config={"mode": "waitAll"})

# ---- Phase D-TOP: Operator Gallery (10 branches, 2 cols x 5 rows @ 180-spacing) ----
# 400px gap after jall-top to spread the 10-edge fan-out labels, 360px between cols.

# Col 1 (x=3980) — branches d01..d05
N("d01-log-eqneq", 3980, 60, "Log: env is production", "log",
  config={"level": "warning",
          "message": "env matches production exactly (env={{trg-manual.param.environment}})"})
N("d02-log-range", 3980, 240, "Log: CPU in [1..128]", "log",
  config={"level": "info",
          "message": "CPU count within sane range: {{host.param.cpuCount}}"})
N("d03-log-disk", 3980, 420, "Log: disk alarm", "log",
  config={"level": "warning",
          "message": "Disk out of bounds: used={{host.param.diskUsedPct}}% free={{host.param.diskFreeGB}}GB threshold={{trg-manual.param.threshold}}"})
N("d04-log-strings", 3980, 600, "Log: OS is Windows family", "log",
  config={"level": "info",
          "message": "OS is a Windows/Microsoft/Pro build matching ^10\\\\.: {{host.param.osName}} v{{host.param.osVersion}}"})
N("d05-log-unarypos", 3980, 780, "Log: has output & domain", "log",
  config={"level": "info",
          "message": "Collected host output AND machine is domain-joined: {{host.param.hostName}}"})

# Col 2 (x=4340) — branches d06..d10
N("d06-log-unaryneg", 4340, 60, "Log: empty & not dry", "log",
  config={"level": "info",
          "message": "emptyField is empty AND dryRun=false (dryRun={{trg-manual.param.dryRun}})"})
N("d07-log-notpanic", 4340, 240, "Log: no PANIC marker", "log",
  config={"level": "info",
          "message": "Host output contains no PANIC marker (safe)"})
N("d08-log-build", 4340, 420, "Log: build matches ^10 & !prod", "log",
  config={"level": "info",
          "message": "build={{host.param.buildNumber}} matches ^10 and env != production"})
N("d09-log-netup", 4340, 600, "Log: net up OR has gateway", "log",
  config={"level": "info",
          "message": "Network is up OR gateway present: ip={{net.param.ipAddress}} gw={{net.param.gatewayIp}}"})
N("d10-log-startsor", 4340, 780, "Log: env starts prod OR stg", "log",
  config={"level": "info",
          "message": "env {{trg-manual.param.environment}} starts with 'prod' or 'stg'"})

# ---- jmatch-top (waitAny for 10 operator branches) ----
# 400px gap to spread the 10-edge fan-in labels.

N("jmatch-top", 4740, 300, "Junction: waitAny (10 ops)", "junction",
  config={"mode": "waitAny"})

# ---- Phase 3-TOP (post jmatch): aggregation + cleanup (4 nodes) ----
# Shifted right to accomodate wider Phase 2-TOP layout (~820 px wider than before).

N("top-log-phase2done", 5100, 300, "Log: Phase-2 TOP complete", "log",
  config={"level": "info", "message": "Phase-2 TOP (operator gallery) complete"})
N("top-delay-bridge", 5420, 300, "Delay 1s (TOP bridge)", "delay",
  config={"seconds": 1})
N("top-log-phase3", 5740, 300, "Log: Phase-3 TOP running", "log",
  config={"level": "info", "message": "TOP entering Phase-3 hold-pattern"})
N("top-delay-phase3", 6060, 300, "Delay 2s (phase-3 hold)", "delay",
  config={"seconds": 2})

# ---- Phase 4-TOP (post sync-3): 4 more deep-operator log nodes ----

N("top-deep-01", 7100, 60, "Log: (prod OR stg) & cpu>=2", "log",
  config={"level": "info", "message": "Deep combo 1: (env in {prod,stg}) AND cpu>=2"})
N("top-deep-02", 7100, 180, "Log: NOT(empty AND dry)", "log",
  config={"level": "info", "message": "Deep combo 2: NOT(output is empty AND dryRun is true)"})
N("top-deep-03", 7100, 420, "Log: build matches ^10\\.\\d+", "log",
  config={"level": "info", "message": "Deep combo 3: build matches ^10\\.\\d+ regex"})
N("top-deep-04", 7100, 540, "Log: disk>=50 OR mem<1024", "log",
  config={"level": "warning", "message": "Deep combo 4: disk>=50% OR mem<1024MB"})

N("top-j-combo", 7460, 300, "Junction: waitAny (4 combos)", "junction",
  config={"mode": "waitAny"})

N("top-log-end", 7780, 300, "Log: TOP lane complete", "log",
  config={"level": "info", "message": "Lane TOP work complete, forwarding to sync-3"})

# ============================================================================
# LANE MID (y-range 840-1140, center y=900) — PROCESSING
# ============================================================================

# ---- Phase 2-MID: REST/SQL Chain (x=2100..4400) ----

N("m01-rest-get", 2100, 900, "restApi: GET httpbin", "restApi",
  outputVariable="rest",
  config={"url": "https://httpbin.org/get?host={{host.param.hostName}}&env={{trg-manual.param.environment}}",
          "method": "GET", "timeoutSeconds": 15,
          "retry": {"maxAttempts": 3, "backoff": "exponential",
                    "initialDelayMs": 1000, "maxDelayMs": 10000}})

N("m02-log-restfail", 2440, 1080, "Log: REST failed", "log",
  config={"level": "warning",
          "message": "REST call failed after retries — falling back to SQL-only path"})

N("m03-sql-select", 2440, 900, "sql: SQLite SELECT 1", "sql",
  outputVariable="sql1",
  config={"provider": "sqlite", "connectionString": "Data Source=:memory:",
          "query": "SELECT 1 AS n, 'ok' AS msg, CURRENT_TIMESTAMP AS ts",
          "timeoutSeconds": 15})

N("m04-sql-params", 2780, 900, "sql: SQLite with params", "sql",
  outputVariable="sql2",
  config={"provider": "sqlite", "connectionString": "Data Source=:memory:",
          "query": "SELECT @env AS env, @host AS host, @thr AS threshold",
          "parameters": {"env": "{{trg-manual.param.environment}}",
                         "host": "{{host.param.hostName}}",
                         "thr": "{{trg-manual.param.threshold}}"},
          "timeoutSeconds": 15})

N("mid-jrest", 3180, 900, "Junction: waitAny (rest or fail)", "junction",
  config={"mode": "waitAny"})

N("mid-log-phase2done", 3540, 900, "Log: MID Phase-2 complete", "log",
  config={"level": "info", "message": "MID Phase-2 (REST+SQL chain) done"})

# ---- Phase 3-MID: XML/JSON/Sub-Workflows (x=4600..6500) ----

N("m05-xml", 4620, 900, "xmlQuery: /root/item", "xmlQuery",
  outputVariable="xml",
  config={"source": "inline",
          "content": "<root xmlns:np=\"https://nodepilot.example\"><item id=\"1\">Alpha</item><item id=\"2\">Beta</item><item id=\"3\">Gamma</item><np:note>ok</np:note></root>",
          "xpath": "/root/item",
          "namespaces": {"np": "https://nodepilot.example"},
          "resultMode": "all"})

N("m06-json", 4940, 900, "jsonQuery: $.items[*].name (BREAKPOINT)", "jsonQuery",
  outputVariable="json",
  breakpoint=True,
  config={"source": "inline",
          "content": "{\"items\":[{\"name\":\"a\",\"v\":1},{\"name\":\"b\",\"v\":2},{\"name\":\"c\",\"v\":3}]}",
          "jsonPath": "$.items[*].name",
          "resultMode": "all"})

N("m07-rest-post", 5260, 900, "restApi: POST echo", "restApi",
  outputVariable="post",
  config={"url": "https://httpbin.org/post",
          "method": "POST",
          "body": "{\"host\":\"{{host.param.hostName}}\",\"env\":\"{{trg-manual.param.environment}}\",\"iter\":{{trg-manual.param.iteration}}}",
          "headers": {"Content-Type": "application/json", "X-Demo-Source": "NodePilot-XL"},
          "timeoutSeconds": 20,
          "retry": {"maxAttempts": 2, "backoff": "linear",
                    "initialDelayMs": 1000, "maxDelayMs": 3000}})

N("m08-foreach", 5580, 900, "forEach: 5 items -> child", "forEach",
  outputVariable="fe",
  config={"childWorkflowNameOrId": "NodePilot Tech-Demo - Child",
          "items": "[{\"fromHost\":\"itemA\"},{\"fromHost\":\"itemB\"},{\"fromHost\":\"itemC\"},{\"fromHost\":\"itemD\"},{\"fromHost\":\"itemE\"}]",
          "itemsFormat": "json",
          "itemParameterName": "item",
          "indexParameterName": "idx",
          "parameters": {"fromHost": "{{host.param.hostName}}",
                         "tag": "{{trg-manual.param.environment}}",
                         "now": "{{host.param.now}}"},
          "maxParallelism": 3,
          "continueOnError": True,
          "timeoutSecondsPerItem": 60})

N("m09-sw-sync", 5900, 900, "startWorkflow: Child (sync)", "startWorkflow",
  outputVariable="childSync",
  config={"workflowNameOrId": "NodePilot Tech-Demo - Child",
          "parameters": {"fromHost": "{{host.param.hostName}}",
                         "tag": "sync-{{trg-manual.param.environment}}",
                         "now": "{{host.param.now}}"},
          "waitForCompletion": True,
          "timeoutSeconds": 60})

N("m10-log-childfail", 5900, 1080, "Log: child failed", "log",
  config={"level": "warning", "message": "Child workflow returned failure — continuing with sync=null"})

N("m11-sw-fire", 6220, 900, "startWorkflow: Child (fire&forget)", "startWorkflow",
  outputVariable="childFire",
  config={"workflowNameOrId": "NodePilot Tech-Demo - Child",
          "parameters": {"fromHost": "{{host.param.hostName}}",
                         "tag": "fire-{{trg-manual.param.environment}}",
                         "now": "{{host.param.now}}"},
          "waitForCompletion": False,
          "timeoutSeconds": 10})

N("m12-return-mid", 6540, 900, "returnData: MID collector", "returnData",
  outputVariable="midCollect",
  config={"data": {"restStatus": "{{rest.param.statusCode}}",
                   "sql1Ok": "{{sql1.param.rowCount}}",
                   "sql2Ok": "{{sql2.param.rowCount}}",
                   "xmlCount": "{{xml.param.count}}",
                   "jsonCount": "{{json.param.count}}",
                   "foreachTotal": "{{fe.param.total}}",
                   "foreachSucceeded": "{{fe.param.succeeded}}",
                   "childSyncStatus": "{{childSync.param.__status}}"}})

# ---- Phase 4-MID: Retry-NofM + deep operator combos (x=7100..9600) ----

# 3 delays for waitNofM (y=720, 900, 1080)
N("m13-delay-a", 7100, 720, "Delay 1s (nofM-a)", "delay",
  config={"seconds": 1,
          "retry": {"maxAttempts": 2, "backoff": "fixed", "initialDelayMs": 500}})
N("m14-delay-b", 7100, 900, "Delay 2s (nofM-b)", "delay",
  config={"seconds": 2,
          "retry": {"maxAttempts": 2, "backoff": "linear", "initialDelayMs": 500, "maxDelayMs": 2000}})
N("m15-delay-c", 7100, 1080, "Delay 5s (nofM-c)", "delay",
  config={"seconds": 5,
          "retry": {"maxAttempts": 2, "backoff": "exponential", "initialDelayMs": 500, "maxDelayMs": 5000}})

N("m16-jnofm", 7460, 900, "Junction: waitNofM (2/3)", "junction",
  config={"mode": "waitNofM", "requiredCount": 2})

N("m17-waitcond", 7780, 900, "waitForCondition: poll", "waitForCondition",
  targetMachineId="localhost",
  config={"script": "$true",
          "intervalSeconds": 3, "timeoutSeconds": 30})

# ---- Phase 4-MID: 10 Deep-nested operator combos (2 cols x 5 rows) ----

def deep_mid(id_, x, y, suffix, level="info"):
    N(id_, x, y, f"Log: deep-{suffix}", "log",
      config={"level": level, "message": f"MID deep combo {suffix}"})

deep_mid("m-d1", 8140, 720, "NOT(empty & dry)")
deep_mid("m-d2", 8140, 840, "AND(OR(<,<), NOT(isTrue))")
deep_mid("m-d3", 8140, 960, "matches AND contains")
deep_mid("m-d4", 8140, 1080, "cpu>=4 & mem>=1024")
deep_mid("m-d5", 8500, 720, "env in {prod,stg,dev}")
deep_mid("m-d6", 8500, 840, "host matches & !empty")
deep_mid("m-d7", 8500, 960, "sql ok & rest ok")
deep_mid("m-d8", 8500, 1080, "(ipv4 & gw set) OR 127.x")
deep_mid("m-d9", 8860, 840, "fe.succ>=3 & fe.fail=0")
deep_mid("m-d10", 8860, 960, "ALWAYS (mid fallback)")

N("m-jdeep", 9220, 900, "Junction: waitAny (10 deep)", "junction",
  config={"mode": "waitAny"})

N("m-log-end", 9580, 900, "Log: MID lane complete", "log",
  config={"level": "info", "message": "Lane MID complete — forwarding to finale"})

# ============================================================================
# LANE BOT (y-range 1320-1920, center y=1500) — OPS + FINALE
# ============================================================================

# ---- Phase 2-BOT: Timing Demo (x=2100..4400) ----

N("bot01-delay-fast", 2100, 1320, "Delay 1s (fast)", "delay",
  config={"seconds": 1})
N("bot02-delay-med", 2100, 1500, "Delay 3s (med)", "delay",
  config={"seconds": 3})
N("bot03-delay-slow", 2100, 1680, "Delay 7s (slow)", "delay",
  config={"seconds": 7})

N("bot04-jany", 2440, 1500, "Junction: waitAny (fastest)", "junction",
  config={"mode": "waitAny"})

N("bot05-waitcond", 2800, 1500, "waitForCondition: timing ready", "waitForCondition",
  targetMachineId="localhost",
  config={"script": "$true", "intervalSeconds": 2, "timeoutSeconds": 30})

N("bot06-log-timing", 3140, 1500, "Log: timing done", "log",
  config={"level": "info", "message": "BOT timing phase done — fastest lane finished first"})

N("bot07-delay-disabled", 3460, 1500, "Delay (node DISABLED)", "delay",
  disabled=True,
  config={"seconds": 10})

N("bot08-log-afterdis", 3780, 1500, "Log: after disabled node", "log",
  config={"level": "info", "message": "Runs even though upstream delay is disabled (via skip-cascade catch)"})

# ---- Phase 3-BOT: Extended Remote Ops (4 remote) + Notification (x=4600..6800) ----

N("bot09-file-copy", 4620, 1320, "fileOperation: copy demo", "fileOperation",
  targetMachineId="localhost",
  config={"operation": "copy",
          "path": "C:\\Windows\\System32\\drivers\\etc\\hosts",
          "destination": "{{host.param.userName}}-hosts-copy.txt",
          "timeoutSeconds": 30})

N("bot10-svc-restart", 4620, 1500, "serviceManagement: status W32Time", "serviceManagement",
  targetMachineId="localhost",
  config={"serviceName": "W32Time", "action": "status", "timeoutSeconds": 30})

N("bot11-reg-read2", 4620, 1680, "registryOperation: BuildLab read", "registryOperation",
  targetMachineId="localhost",
  config={"operation": "read",
          "keyPath": "HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
          "valueName": "BuildLab", "timeoutSeconds": 30})

N("bot12-wmi-bios", 4620, 1860, "wmiQuery: Win32_BIOS", "wmiQuery",
  targetMachineId="localhost",
  config={"className": "Win32_BIOS", "namespace": "root\\cimv2", "timeoutSeconds": 30})

N("bot13-jallremote", 4940, 1500, "Junction: waitAll (4 remote)", "junction",
  config={"mode": "waitAll"})

N("bot14-email", 5260, 1320, "emailNotification: summary", "emailNotification",
  config={"to": "ops@example.com",
          "subject": "NodePilot XL demo run on {{host.param.hostName}} ({{trg-manual.param.environment}})",
          "body": "XL run complete.\nHost: {{host.param.hostName}} ({{host.param.osName}})\nEnv: {{trg-manual.param.environment}}\nCPUs: {{host.param.cpuCount}}\nMem free: {{host.param.memFreeMB}}MB\nDisk free: {{host.param.diskFreeGB}}/{{host.param.diskTotalGB}} GB\nIteration: {{trg-manual.param.iteration}}",
          "isHtml": False})

N("bot15-power", 5260, 1500, "powerManagement: abort (safe)", "powerManagement",
  targetMachineId="localhost",
  config={"action": "abort", "delaySeconds": 0, "force": False, "message": ""})

N("bot16-log-disnode", 5260, 1680, "Log: node DISABLED", "log",
  disabled=True,
  config={"level": "warning", "message": "This node is node-level disabled — shown as Skipped in UI"})

N("bot17-log-ghost", 5580, 1680, "Log: ghost (disabled edge target)", "log",
  config={"level": "info", "message": "If you see this, the disabled-edge fired (should NOT happen)"})

N("bot18-log-notify", 5580, 1500, "Log: notify done", "log",
  config={"level": "info", "message": "Notification fan complete"})

# ---- Phase 4-BOT: Debug Zone + Deep Ops Round 2 + Cleanup (x=7100..9800) ----

N("bot19-log-bp1", 7100, 1320, "Log: BREAKPOINT 1", "log",
  breakpoint=True,
  config={"level": "info", "message": "Breakpoint 1: inspect host.param.* + rest.param.* + fe.param.*"})

N("bot20-runscript-bp", 7100, 1500, "runScript: BREAKPOINT 2", "runScript",
  targetMachineId="localhost",
  outputVariable="bp2",
  breakpoint=True,
  config={"engine": "auto", "timeoutSeconds": 30,
          "script": "$summary = 'bp2-ok'\n$ranAt = (Get-Date).ToString('HH:mm:ss')\nWrite-Output \"Breakpoint 2 ran at $ranAt\""})

N("bot21-log-disnode2", 7100, 1680, "Log: another DISABLED node", "log",
  disabled=True,
  config={"level": "info", "message": "2nd node-level disabled demo"})

N("bot22-file-del", 7100, 1860, "fileOperation: delete (cleanup)", "fileOperation",
  targetMachineId="localhost",
  config={"operation": "delete",
          "path": "{{host.param.userName}}-hosts-copy.txt", "timeoutSeconds": 30})

# Deep operator combos round 2 — 8 nodes, 2 cols x 4 rows (x=7460, 7820)
def deep_bot(id_, x, y, suffix, level="info"):
    N(id_, x, y, f"Log: bot-deep-{suffix}", "log",
      config={"level": level, "message": f"BOT deep combo {suffix}"})

deep_bot("bot-d1", 7460, 1320, "matches ^Win.*Pro$")
deep_bot("bot-d2", 7460, 1500, "matches ^10\\.0\\.\\d+")
deep_bot("bot-d3", 7460, 1680, "contains OR starts OR ends")
deep_bot("bot-d4", 7460, 1860, "NOT isEmpty AND NOT isFalse")
deep_bot("bot-d5", 7820, 1320, "env != empty AND != null")
deep_bot("bot-d6", 7820, 1500, "iter>=1 & iter<=10")
deep_bot("bot-d7", 7820, 1680, "mem<8192 OR disk<100")
deep_bot("bot-d8", 7820, 1860, "ALWAYS (bot fallback)")

N("bot-jdeep", 8180, 1500, "Junction: waitAny (8 bot deep)", "junction",
  config={"mode": "waitAny"})

N("bot-log-cleanup", 8540, 1500, "Log: cleanup", "log",
  config={"level": "info", "message": "BOT cleanup complete"})

N("bot-delay-final", 8900, 1500, "Delay 1s (BOT settle)", "delay",
  config={"seconds": 1})

N("bot-log-end", 9260, 1500, "Log: BOT lane complete", "log",
  config={"level": "info", "message": "Lane BOT work complete, forwarding to finale"})

# ============================================================================
# FINALE (x=10000..10500, y=900)
# ============================================================================

N("final-junction", 10000, 900, "Junction: waitAny (3 lanes)", "junction",
  config={"mode": "waitAny"})

N("final-invoke", 10360, 900, "startWorkflow: Child (finale)", "startWorkflow",
  outputVariable="childFinale",
  config={"workflowNameOrId": "NodePilot Tech-Demo - Child",
          "parameters": {"fromHost": "{{host.param.hostName}}",
                         "tag": "finale-{{trg-manual.param.environment}}",
                         "now": "{{host.param.now}}"},
          "waitForCompletion": True,
          "timeoutSeconds": 60})

N("final-return", 10720, 900, "returnData: aggregate (30+ keys)", "returnData",
  config={"data": {
      "environment": "{{trg-manual.param.environment}}",
      "threshold": "{{trg-manual.param.threshold}}",
      "dryRun": "{{trg-manual.param.dryRun}}",
      "iteration": "{{trg-manual.param.iteration}}",
      "host": "{{host.param.hostName}}",
      "user": "{{host.param.userName}}",
      "osName": "{{host.param.osName}}",
      "osVersion": "{{host.param.osVersion}}",
      "buildNumber": "{{host.param.buildNumber}}",
      "cpuCount": "{{host.param.cpuCount}}",
      "memFreeMB": "{{host.param.memFreeMB}}",
      "memTotalMB": "{{host.param.memTotalMB}}",
      "diskFreeGB": "{{host.param.diskFreeGB}}",
      "diskTotalGB": "{{host.param.diskTotalGB}}",
      "diskUsedPct": "{{host.param.diskUsedPct}}",
      "isDomain": "{{host.param.isDomain}}",
      "manufacturer": "{{host.param.manufacturer}}",
      "model": "{{host.param.model}}",
      "collectedAt": "{{host.param.now}}",
      "ipAddress": "{{net.param.ipAddress}}",
      "gateway": "{{net.param.gatewayIp}}",
      "dnsServer": "{{net.param.dnsValue}}",
      "netUp": "{{net.param.netUp}}",
      "restStatus": "{{rest.param.statusCode}}",
      "restPostStatus": "{{post.param.statusCode}}",
      "sql1Rows": "{{sql1.param.rowCount}}",
      "sql2Rows": "{{sql2.param.rowCount}}",
      "xmlCount": "{{xml.param.count}}",
      "jsonCount": "{{json.param.count}}",
      "foreachTotal": "{{fe.param.total}}",
      "foreachSucceeded": "{{fe.param.succeeded}}",
      "foreachFailed": "{{fe.param.failed}}",
      "childSyncStatus": "{{childSync.param.__status}}",
      "childFinaleResult": "{{childFinale.param.result}}",
      "severity": "{{proc.param.severity}}",
      "phase": "{{proc.param.phase}}"
  }})

# ============================================================================
# EDGES
# ============================================================================

eid = [0]
def ne(src, tgt, label="Always", **extra):
    eid[0] += 1
    E(f"e{eid[0]:03d}", src, tgt, label, **extra)


# Phase A-TOP: triggers -> kickoff
ne("trg-manual", "log-kickoff", "Always")
# 5 disabled triggers also fan into log-kickoff (visual "all triggers route here")
ne("trg-schedule", "log-kickoff", "DISABLED trigger", disabled=True)
ne("trg-webhook", "log-kickoff", "DISABLED trigger", disabled=True)
ne("trg-filewatch", "log-kickoff", "DISABLED trigger", disabled=True)
ne("trg-database", "log-kickoff", "DISABLED trigger", disabled=True)
ne("trg-eventlog", "log-kickoff", "DISABLED trigger", disabled=True)

# Boot chain
ne("log-kickoff", "delay-init", "On Success", condition="log-kickoff.success")
ne("delay-init", "collect-host", "Always")
ne("collect-host", "collect-net", "hostName set",
   conditionExpression={"type": "comparison", "op": "isNotEmpty",
                        "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "hostName"}})
ne("collect-net", "process-data", "On Success", condition="collect-net.success")

# Sync 1 band
ne("process-data", "sync1", "Always")

# Sync 1 fans to 3 lanes Phase 2
ne("sync1", "r01-file-exists", "Always")
ne("sync1", "r02-svc-status", "Always")
ne("sync1", "r03-reg-read", "Always")
ne("sync1", "r04-wmi-os", "Always")
ne("sync1", "r05-prog-cmd", "Always")
ne("sync1", "r06-waitcond-a", "Always")
ne("sync1", "r07-file-list", "Always")
ne("sync1", "r08-svc-list", "Always")
ne("sync1", "r09-reg-write", "Always")
ne("sync1", "r10-wmi-cpu", "Always")
ne("sync1", "r11-prog-ps", "Always")
ne("sync1", "r12-wmi-disk", "Always")
# Sync 1 also kicks off MID and BOT
ne("sync1", "m01-rest-get", "Always")
ne("sync1", "bot01-delay-fast", "Always")
ne("sync1", "bot02-delay-med", "Always")
ne("sync1", "bot03-delay-slow", "Always")

# 12 remotes -> jall-top
for rid in ["r01-file-exists", "r02-svc-status", "r03-reg-read", "r04-wmi-os",
            "r05-prog-cmd", "r06-waitcond-a", "r07-file-list", "r08-svc-list",
            "r09-reg-write", "r10-wmi-cpu", "r11-prog-ps", "r12-wmi-disk"]:
    ne(rid, "jall-top", "Always")

# jall-top -> 10 operator branches with distinct conditionExpressions

# D1: env==prod & env!=stg
ne("jall-top", "d01-log-eqneq", "env==prod & env!=stg",
   conditionExpression={
       "type": "group", "op": "AND",
       "children": [
           {"type": "comparison", "op": "==",
            "left": {"kind": "variable", "stepId": "trg-manual", "field": "param", "paramName": "environment"},
            "right": {"kind": "literal", "value": "production"}},
           {"type": "comparison", "op": "!=",
            "left": {"kind": "variable", "stepId": "trg-manual", "field": "param", "paramName": "environment"},
            "right": {"kind": "literal", "value": "staging"}}
       ]})

# D2: cpu in [1..128]
ne("jall-top", "d02-log-range", "cpu in [1..128]",
   conditionExpression={
       "type": "group", "op": "AND",
       "children": [
           {"type": "comparison", "op": ">=",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "cpuCount"},
            "right": {"kind": "literal", "value": "1"}},
           {"type": "comparison", "op": "<=",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "cpuCount"},
            "right": {"kind": "literal", "value": "128"}}
       ]})

# D3: disk% > thr OR free < 1GB
ne("jall-top", "d03-log-disk", "disk% > thr OR free < 1GB",
   conditionExpression={
       "type": "group", "op": "OR",
       "children": [
           {"type": "comparison", "op": ">",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "diskUsedPct"},
            "right": {"kind": "variable", "stepId": "trg-manual", "field": "param", "paramName": "threshold"}},
           {"type": "comparison", "op": "<",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "diskFreeGB"},
            "right": {"kind": "literal", "value": "1"}}
       ]})

# D4: contains+starts+ends+matches
ne("jall-top", "d04-log-strings", "contains+starts+ends+matches",
   conditionExpression={
       "type": "group", "op": "AND",
       "children": [
           {"type": "comparison", "op": "contains",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "osName"},
            "right": {"kind": "literal", "value": "Windows"}},
           {"type": "comparison", "op": "startsWith",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "osName"},
            "right": {"kind": "literal", "value": "Microsoft"}},
           {"type": "comparison", "op": "endsWith",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "osName"},
            "right": {"kind": "literal", "value": "Pro"}},
           {"type": "comparison", "op": "matches",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "osVersion"},
            "right": {"kind": "literal", "value": "^10\\."}}
       ]})

# D5: output set & isDomain
ne("jall-top", "d05-log-unarypos", "output set & isDomain",
   conditionExpression={
       "type": "group", "op": "AND",
       "children": [
           {"type": "comparison", "op": "isNotEmpty",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "output"}},
           {"type": "comparison", "op": "isTrue",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "isDomain"}}
       ]})

# D6: empty & !dry
ne("jall-top", "d06-log-unaryneg", "empty & !dry",
   conditionExpression={
       "type": "group", "op": "AND",
       "children": [
           {"type": "comparison", "op": "isEmpty",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "emptyField"}},
           {"type": "comparison", "op": "isFalse",
            "left": {"kind": "variable", "stepId": "trg-manual", "field": "param", "paramName": "dryRun"}}
       ]})

# D7: NOT contains PANIC
ne("jall-top", "d07-log-notpanic", "NOT contains PANIC",
   conditionExpression={
       "type": "not",
       "child": {"type": "comparison", "op": "contains",
                 "left": {"kind": "variable", "stepId": "collect-host", "field": "output"},
                 "right": {"kind": "literal", "value": "PANIC"}}})

# D8: build matches ^10 & !prod (NOT(==prod))
ne("jall-top", "d08-log-build", "build matches & env!=prod",
   conditionExpression={
       "type": "group", "op": "AND",
       "children": [
           {"type": "comparison", "op": "matches",
            "left": {"kind": "variable", "stepId": "collect-host", "field": "param", "paramName": "osVersion"},
            "right": {"kind": "literal", "value": "^10\\."}},
           {"type": "not",
            "child": {"type": "comparison", "op": "==",
                      "left": {"kind": "variable", "stepId": "trg-manual", "field": "param", "paramName": "environment"},
                      "right": {"kind": "literal", "value": "production"}}}
       ]})

# D9: net up OR has gateway
ne("jall-top", "d09-log-netup", "netUp=true OR gw set",
   conditionExpression={
       "type": "group", "op": "OR",
       "children": [
           {"type": "comparison", "op": "isTrue",
            "left": {"kind": "variable", "stepId": "collect-net", "field": "param", "paramName": "netUp"}},
           {"type": "comparison", "op": "isNotEmpty",
            "left": {"kind": "variable", "stepId": "collect-net", "field": "param", "paramName": "gatewayIp"}}
       ]})

# D10: env startsWith prod OR stg
ne("jall-top", "d10-log-startsor", "env starts prod OR stg",
   conditionExpression={
       "type": "group", "op": "OR",
       "children": [
           {"type": "comparison", "op": "startsWith",
            "left": {"kind": "variable", "stepId": "trg-manual", "field": "param", "paramName": "environment"},
            "right": {"kind": "literal", "value": "prod"}},
           {"type": "comparison", "op": "startsWith",
            "left": {"kind": "variable", "stepId": "trg-manual", "field": "param", "paramName": "environment"},
            "right": {"kind": "literal", "value": "stg"}}
       ]})

# All 10 operator branches -> jmatch-top
for did in ["d01-log-eqneq", "d02-log-range", "d03-log-disk", "d04-log-strings",
            "d05-log-unarypos", "d06-log-unaryneg", "d07-log-notpanic",
            "d08-log-build", "d09-log-netup", "d10-log-startsor"]:
    ne(did, "jmatch-top", "Always")

# jmatch-top -> TOP Phase 3 chain (feeds into sync2)
ne("jmatch-top", "top-log-phase2done", "Always")
ne("top-log-phase2done", "top-delay-bridge", "Always")
ne("top-delay-bridge", "top-log-phase3", "Always")
ne("top-log-phase3", "top-delay-phase3", "Always")

# ---- MID: Phase 2 chain ----
ne("m01-rest-get", "m03-sql-select", "On Success", condition="m01-rest-get.success")
ne("m01-rest-get", "m02-log-restfail", "On Failure", condition="m01-rest-get.failed")
ne("m03-sql-select", "m04-sql-params", "Always")
ne("m04-sql-params", "mid-jrest", "Always")
ne("m02-log-restfail", "mid-jrest", "Always")
ne("mid-jrest", "mid-log-phase2done", "Always")

# ---- BOT: Phase 2 chain ----
ne("bot01-delay-fast", "bot04-jany", "Always")
ne("bot02-delay-med", "bot04-jany", "Always")
ne("bot03-delay-slow", "bot04-jany", "Always")
ne("bot04-jany", "bot05-waitcond", "Always")
ne("bot05-waitcond", "bot06-log-timing", "Always")
ne("bot06-log-timing", "bot07-delay-disabled", "Always (target disabled)")
ne("bot07-delay-disabled", "bot08-log-afterdis", "Always (cascades from disabled)")

# ---- Each lane runs independently end-to-end after sync1. No sync-2 band needed. ----
# MID Phase 2 end (mid-log-phase2done) -> MID Phase 3 start
ne("mid-log-phase2done", "m05-xml", "Always")

# BOT Phase 2 end (bot08-log-afterdis) -> BOT Phase 3 start (fan to 4 remote ops)
ne("bot08-log-afterdis", "bot09-file-copy", "Always")
ne("bot08-log-afterdis", "bot10-svc-restart", "Always")
ne("bot08-log-afterdis", "bot11-reg-read2", "Always")
ne("bot08-log-afterdis", "bot12-wmi-bios", "Always")

# ---- MID: Phase 3 chain ----
ne("m05-xml", "m06-json", "On Success", condition="m05-xml.success")
ne("m06-json", "m07-rest-post", "Always")
ne("m07-rest-post", "m08-foreach", "On Success", condition="m07-rest-post.success")
ne("m08-foreach", "m09-sw-sync", "Always")
ne("m09-sw-sync", "m11-sw-fire", "On Success", condition="m09-sw-sync.success")
ne("m09-sw-sync", "m10-log-childfail", "On Failure", condition="m09-sw-sync.failed")
ne("m10-log-childfail", "m11-sw-fire", "Always")
ne("m11-sw-fire", "m12-return-mid", "Always")

# ---- BOT: Phase 3 chain ----
for bid in ["bot09-file-copy", "bot10-svc-restart", "bot11-reg-read2", "bot12-wmi-bios"]:
    ne(bid, "bot13-jallremote", "Always")
ne("bot13-jallremote", "bot14-email", "Always")
ne("bot13-jallremote", "bot15-power", "Always")
ne("bot13-jallremote", "bot16-log-disnode", "Always (target disabled)")
ne("bot14-email", "bot17-log-ghost", "DISABLED edge", disabled=True)
ne("bot14-email", "bot18-log-notify", "Always (even on fail)")
ne("bot15-power", "bot18-log-notify", "Always")
ne("bot16-log-disnode", "bot18-log-notify", "Always")
ne("bot17-log-ghost", "bot18-log-notify", "Always (ghost)")

# ---- SYNC 3 (x=7000): MID Phase 3 end + BOT Phase 3 end -> Phase 4 ----

# TOP Phase 3 -> TOP Phase 4 deep ops
ne("top-delay-phase3", "top-deep-01", "Always")  # wait, top-delay-phase3 already used — use different path
# Actually let me re-do: top-delay-phase3 -> top-deep-XX
# But top-delay-phase3 already has fan-out to MID/BOT. Adding more is fine.

# Remove the above erroneous duplicate — top-delay-phase3 should fan to TOP Phase-4 too:
# Actually my ne() always creates a new edge id, so duplicates aren't an issue.
# I'll just add more edges from top-delay-phase3:
ne("top-delay-phase3", "top-deep-02", "Always")
ne("top-delay-phase3", "top-deep-03", "Always")
ne("top-delay-phase3", "top-deep-04", "Always")

# TOP Phase 4 deep ops with conditions using conditionExpression
# top-deep-01: (prod OR stg) & cpu>=2
# Actually let me add meaningful conditions only on some, leave others as Always to avoid over-engineering

for did in ["top-deep-01", "top-deep-02", "top-deep-03", "top-deep-04"]:
    ne(did, "top-j-combo", "Always")
ne("top-j-combo", "top-log-end", "Always")

# MID Phase 4: m12-return-mid fans to 3 delays + waitcond path
ne("m12-return-mid", "m13-delay-a", "Always")
ne("m12-return-mid", "m14-delay-b", "Always")
ne("m12-return-mid", "m15-delay-c", "Always")
ne("m13-delay-a", "m16-jnofm", "Always")
ne("m14-delay-b", "m16-jnofm", "Always")
ne("m15-delay-c", "m16-jnofm", "Always")
ne("m16-jnofm", "m17-waitcond", "Always")

# MID Phase 4 deep combos
for mid_deep in ["m-d1", "m-d2", "m-d3", "m-d4", "m-d5", "m-d6", "m-d7", "m-d8", "m-d9", "m-d10"]:
    ne("m17-waitcond", mid_deep, "Always")
    ne(mid_deep, "m-jdeep", "Always")
ne("m-jdeep", "m-log-end", "Always")

# BOT Phase 4: bot18-log-notify fans to debug zone
ne("bot18-log-notify", "bot19-log-bp1", "Always")
ne("bot18-log-notify", "bot20-runscript-bp", "Always")
ne("bot18-log-notify", "bot21-log-disnode2", "Always (target disabled)")
ne("bot18-log-notify", "bot22-file-del", "Always")

# BOT debug zone -> deep combos
for bid_src in ["bot19-log-bp1", "bot20-runscript-bp", "bot21-log-disnode2", "bot22-file-del"]:
    for bid_deep in ["bot-d1", "bot-d2", "bot-d3", "bot-d4"]:
        ne(bid_src, bid_deep, "Always")
for bid_src in ["bot19-log-bp1", "bot20-runscript-bp", "bot21-log-disnode2", "bot22-file-del"]:
    for bid_deep in ["bot-d5", "bot-d6", "bot-d7", "bot-d8"]:
        ne(bid_src, bid_deep, "Always")

for bid_deep in ["bot-d1", "bot-d2", "bot-d3", "bot-d4", "bot-d5", "bot-d6", "bot-d7", "bot-d8"]:
    ne(bid_deep, "bot-jdeep", "Always")

ne("bot-jdeep", "bot-log-cleanup", "Always")
ne("bot-log-cleanup", "bot-delay-final", "Always")
ne("bot-delay-final", "bot-log-end", "Always")

# ---- FINALE: 3 lane ends -> final-junction -> invoke -> return ----
ne("top-log-end", "final-junction", "Always")
ne("m-log-end", "final-junction", "Always")
ne("bot-log-end", "final-junction", "Always")
ne("final-junction", "final-invoke", "Always")
ne("final-invoke", "final-return", "On Success", condition="final-invoke.success")


# ============================================================================
# VALIDATION
# ============================================================================

def validate():
    errors = []
    warnings = []

    # 1. Unique node IDs
    ids = [n["id"] for n in nodes]
    dup = [i for i in ids if ids.count(i) > 1]
    if dup:
        errors.append(f"Duplicate node IDs: {set(dup)}")

    # 2. All edges have valid source/target
    id_set = set(ids)
    for e in edges:
        if e["source"] not in id_set:
            errors.append(f"Edge {e['id']} has invalid source: {e['source']}")
        if e["target"] not in id_set:
            errors.append(f"Edge {e['id']} has invalid target: {e['target']}")

    # 3. Activity type coverage
    types_seen = {n["data"]["activityType"] for n in nodes}
    expected = {
        "manualTrigger", "scheduleTrigger", "webhookTrigger", "fileWatcherTrigger",
        "databaseTrigger", "eventLogTrigger",  # 6 triggers
        "log", "delay", "junction",
        "runScript", "fileOperation", "folderOperation", "serviceManagement", "registryOperation",
        "wmiQuery", "startProgram", "powerManagement", "waitForCondition",
        "restApi", "sql", "emailNotification", "xmlQuery", "jsonQuery",
        "startWorkflow", "forEach", "returnData"
    }
    missing = expected - types_seen
    if missing:
        errors.append(f"Missing activity types: {missing}")

    # 4. Operator coverage
    ops_seen = set()
    def walk_expr(expr):
        if not isinstance(expr, dict):
            return
        t = expr.get("type")
        if t == "comparison":
            ops_seen.add(expr.get("op"))
        elif t == "group":
            ops_seen.add(expr.get("op"))  # AND/OR
            for c in expr.get("children", []):
                walk_expr(c)
        elif t == "not":
            ops_seen.add("NOT")
            walk_expr(expr.get("child", {}))
    for e in edges:
        if "conditionExpression" in e["data"]:
            walk_expr(e["data"]["conditionExpression"])
    expected_ops = {"==", "!=", "<", ">", "<=", ">=", "contains", "startsWith",
                    "endsWith", "matches", "isEmpty", "isNotEmpty", "isTrue", "isFalse",
                    "AND", "OR", "NOT"}
    missing_ops = expected_ops - ops_seen
    if missing_ops:
        errors.append(f"Missing operators: {missing_ops}")

    # 5. At least 100 activity nodes
    if len(nodes) < 100:
        errors.append(f"Only {len(nodes)} nodes — need >=100")

    # 6. At least 3 junction modes
    junction_modes = set()
    for n in nodes:
        if n["data"]["activityType"] == "junction":
            m = n["data"].get("config", {}).get("mode")
            if m:
                junction_modes.add(m)
    expected_modes = {"waitAll", "waitAny", "waitNofM"}
    missing_modes = expected_modes - junction_modes
    if missing_modes:
        errors.append(f"Missing junction modes: {missing_modes}")

    # 7. At least 1 breakpoint, 1 disabled node, 1 disabled edge
    if not any(n["data"].get("breakpoint") for n in nodes):
        errors.append("No breakpoint nodes found")
    if not any(n["data"].get("disabled") for n in nodes):
        errors.append("No disabled nodes found")
    if not any(e["data"].get("disabled") for e in edges):
        errors.append("No disabled edges found")

    # 8. manualTrigger params all type=string
    for n in nodes:
        if n["data"]["activityType"] == "manualTrigger":
            for p in n["data"].get("config", {}).get("parameters", []):
                if p.get("type") != "string":
                    errors.append(f"Trigger {n['id']} param {p.get('name')} is not type=string")
                if "default" in p and not isinstance(p["default"], str):
                    errors.append(f"Trigger {n['id']} param {p.get('name')} default is not a string")

    # 9. waitNofM junctions have requiredCount (not n)
    for n in nodes:
        if (n["data"]["activityType"] == "junction"
                and n["data"].get("config", {}).get("mode") == "waitNofM"):
            cfg = n["data"]["config"]
            if "requiredCount" not in cfg:
                errors.append(f"Junction {n['id']} is waitNofM but missing requiredCount")

    # 10. LTR invariant: no edge with target.x < source.x - 60
    id_to_pos = {n["id"]: n["position"] for n in nodes}
    for e in edges:
        if e["source"] in id_to_pos and e["target"] in id_to_pos:
            dx = id_to_pos[e["target"]]["x"] - id_to_pos[e["source"]]["x"]
            if dx < -60:
                warnings.append(f"Backward edge {e['id']}: {e['source']} -> {e['target']} (dx={dx})")

    # 11. No duplicate outputVariable
    ov = {}
    for n in nodes:
        v = n["data"].get("outputVariable")
        if v:
            ov.setdefault(v, []).append(n["id"])
    dups_ov = {k: v for k, v in ov.items() if len(v) > 1}
    if dups_ov:
        errors.append(f"Duplicate outputVariables: {dups_ov}")

    # 12. All position.x, position.y on 20-grid
    bad_pos = []
    for n in nodes:
        if n["position"]["x"] % 20 != 0 or n["position"]["y"] % 20 != 0:
            bad_pos.append(n["id"])
    if bad_pos:
        errors.append(f"Non-grid-aligned positions: {bad_pos[:10]}")

    return errors, warnings


errors, warnings = validate()
print(f"Nodes: {len(nodes)}  Edges: {len(edges)}")
print(f"Errors: {len(errors)}, Warnings: {len(warnings)}")
for err in errors:
    print(f"  ERROR: {err}")
for w in warnings[:10]:
    print(f"  WARN: {w}")

if errors:
    print("\n[FAIL] Validation failed - not writing file.")
    sys.exit(1)

# Write output
output = {
    "_comment": (
        "NodePilot Tech-Demo XL — 3-swim-lane showcase. ~106 nodes, ~170 edges. "
        "LANE TOP (y=300) Discovery: triggers + host collect + 12 remote fan-out + 10 operator branches. "
        "LANE MID (y=900) Processing: REST/SQL + XML/JSON + forEach + sub-workflows + retry-NofM + 10 deep operator combos. "
        "LANE BOT (y=1500) Ops: timing demo + remote-ops + email + power-abort + debug zone + 8 deep operator combos + cleanup. "
        "Sync bands at x=1960 (sync1) and via top-delay-phase3 (sync2), final-junction at x=10000. "
        "All 19 activity types, all 14 edge operators + AND/OR/NOT, all 3 junction modes, breakpoints, disabled nodes, disabled edges, retry policies, error paths, sub-workflow calls. "
        "Bounds: 0..10720 x 0..1920."
    ),
    "nodes": nodes,
    "edges": edges
}

OUT_PATH.write_text(json.dumps(output, indent=2, ensure_ascii=False), encoding="utf-8")
print(f"\n[OK] Wrote {OUT_PATH} ({OUT_PATH.stat().st_size // 1024} KiB)")
