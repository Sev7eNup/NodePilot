# Software update evidence

Client and site-provider classes have different keys and status enums. Do not reuse
server CI_ID filters on client classes. Read the schema of an allowed class when a
property is uncertain; do not interpret an invalid query as an unhealthy component:

```powershell
Get-CimClass -Namespace root/ccm/ClientSDK -ClassName CCM_SoftwareUpdate | Select-Object -ExpandProperty CimClassProperties | Select-Object Name,CimType
```

Use separate property arguments (`-Property Name,State`), not a quoted comma-separated
single name (`-Property 'Name,State'`). Project only relevant fields.

| Namespace / class | Keys and useful properties |
|---|---|
| root/SMS/site_<code> / SMS_SoftwareUpdate | CI_ID, CI_UniqueID, ArticleID, LocalizedDisplayName, IsExpired, IsSuperseded |
| root/SMS/site_<code> / SMS_UpdateComplianceStatus | CI_ID, **MachineID**, Status; not ResourceID |
| root/SMS/site_<code> / SMS_CIDeploymentUnknownAssetDetails | MachineID, CI_ID, AssignmentID, AssignmentUniqueID, SoftwareName, CollectionID |
| root/SMS/site_<code> / SMS_UpdatesAssignment | AssignmentID, AssignmentUniqueID, AssignedCIs, TargetCollectionID, StartTime, EnforcementDeadline, UseGMTTimes, OverrideServiceWindows |
| root/ccm/ClientSDK / CCM_SoftwareUpdate | **UpdateID**, ArticleID, Name, ComplianceState, EvaluationState, Deadline, StartTime, ErrorCode |
| root/ccm/SoftwareUpdates/UpdatesStore / CCM_UpdateStatus | **UniqueId**, **Article**, Title, Status, ScanTime, SourceUniqueId |
| root/ccm/Policy/Machine/ActualConfig / CCM_UpdateCIAssignment | AssignmentID, AssignmentName, AssignedCIs, StartTime, EnforcementDeadline, UseGMTTimes, OverrideServiceWindows |

Map the actual GUID/unique-ID values and assignment unique ID between client and
server. Never assume a numeric site CI_ID exists as a client property. Inspect bounded
candidate rows by article or assignment before deciding identity. Empty compliance rows
are not a successful scan: unknown assets can be represented by the deployment-unknown
class instead. Match its assignment to the requested update through AssignedCIs; an
unknown group alone does not prove every update in it is unknown.
Do not filter SMS_CIDeploymentUnknownAssetDetails by the requested update's CI_ID:
an assignment-level row can have CI_ID=0. Query by MachineID and AssignmentUniqueID
or AssignmentID, then use the assignment's AssignedCIs to establish the update mapping.
A CI-filtered empty result does not rule out an Unknown asset for that assignment.

Keep the deployment's Unknown asset status, individual CI compliance and scan health
separate. A later Required/Installed/NotRequired report for an individual CI does not
prove that the latest SUP scan succeeded. Retain a current scan failure until a newer
successful scan for that operation is observed; investigate its available discriminating
checks even if a different status changed. State whether the proposed repair addresses
the proven scan blocker or an independently established reporting fault.

IsLatest and IsSuperseded describe different relationships: latest version of a CI
versus replacement by another CI. Both can be true without proving corrupt metadata.
Do not propose metadata repair from those two flags alone. See the
[CI property definitions](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/compliance/sms_configurationitemlatestbaseclass-server-wmi-class).

A superseded CI is a lifecycle observation, not automatically a misconfiguration.
Do not call its deployment defective or prescribe replacement unless the intended
target version or applicable deployment policy establishes that requirement. If the
requirement is unknown, propose checking it, not changing the assignment. Likewise,
a proven scan blocker does not by itself prove the sole reason for an individual
CI's missing server report: distinguish the demonstrated scan mechanism from its
possible downstream compliance effect and verify reporting after authorized recovery.

SMS_UpdateComplianceStatus uses 0 Unknown, 1 NotRequired, 2 Required, 3 Installed.
Client enums are different; preserve their class and raw value in evidence.
There is no SMS_UpdateScanStatus class on the tested site provider. Obtain current scan
completion from client WUAHandler/ScanAgent logs and UpdatesStore ScanTime. SQL views
named v_UpdateScanStatus do not imply an identically named WMI class.

For Unknown, establish a current failed or absent scan and investigate the failing
endpoint using the communication checks in diagnostic-evidence.md. An error code or
invalid-source/TTL log message alone is not a root cause. Spend calls on discriminating
configuration checks rather than repeatedly reading the same errors.

For Required without installation, prove the specific update is currently missing,
assigned to this client, available, and due (or not yet due). Compare client and server
deadlines, UseGMTTimes and actual machine timezone. Scheduling reads preserve raw DMTF strings:
`20260922022900.000000+***` means September 22 at 02:29, offset unspecified.
With UseGMTTimes=false preserve this local wall clock; do not shift it to the previous
day. Cite the target timezone and avoid inventing an exact UTC instant.
Check UpdatesDeployment/UpdatesHandler
for the corresponding assignment decision. A future deadline can explain no automatic
installation; it does not prove manual installation or content retrieval would succeed.
Historical scan failures do not explain current behavior after a newer successful scan.

References: [site compliance class](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/sum/sms_updatecompliancestatus-server-wmi-class),
[update assignments](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/sum/sms_updatesassignment-server-wmi-class).
