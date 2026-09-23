# Evidence sources

- Component servicing: `%windir%\Logs\CBS\CBS.log` and relevant rotated CBS logs.
- DISM: `%windir%\Logs\DISM\dism.log`.
- Windows Update: WindowsUpdateClient/Operational event log. ETL conversion with
  `Get-WindowsUpdateLog` creates an output file; do not invoke it in the current
  read-only agent mode. Inspect already available evidence instead.
- Configuration Manager client: `%windir%\CCM\Logs\WUAHandler.log`,
  `UpdatesDeployment.log`, `UpdatesHandler.log`, `ScanAgent.log`, `LocationServices.log`,
  `CAS.log`, `ContentTransferManager.log` and `DataTransferService.log`, as relevant.

Installation paths may differ. Verify existence first. CMTrace-style logs encode time,
date, component and severity in each entry. Preserve that context around the error.
Never assume a particular source exists on every Windows machine.

## Time evidence

Keep file modification time, log-event time and observation time distinct. `files_list`
returns `LastWriteTimeUtc` as ISO 8601 with `Z`; quote it exactly. It does not establish
the timezone of timestamps written inside the file. Shell/skill `timeContext` describes
the target clock at observation, not the timezone of every source on that machine.

Preserve the timestamp and offset embedded in a source record. If a CBS, DISM or other
text line has no offset, report its timezone as unknown until the source's configuration
or documented timestamp convention establishes it. Do not label it UTC just because
the file metadata uses UTC. CMTrace time fields can carry an offset; interpret it only
according to that format's documented convention, not as an arbitrary ISO suffix.

A current machine offset must not be reused for older events across daylight-saving
changes. Repeated or nonexistent local times require additional evidence. Do not infer
cross-source chronology where the timezone or offset is unresolved. Carry these limits
and original timestamps into supervisor/reviewer handoffs.
