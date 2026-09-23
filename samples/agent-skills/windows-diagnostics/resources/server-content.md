# Server-side Configuration Manager content investigation

Use only the explicitly assigned server and its selected read tools. The deployment
assumes the configured service/computer identity has the required WinRM and SMS
Provider rights; never change rights or switch identity in response to an error.
An explicit credential may be selected instead. A provider on a different machine
requires a separately configured member; discovery does not authorize a target hop.

Keep each result small: filter by the known ID/name and explicitly project only
the fields needed below with Select-Object before ConvertTo-Json. Do not serialize
whole CIM objects or Select-Object *: nested CimClass metadata can exhaust the
conversation budget. Re-query a truncated result with fewer fields. Useful projections:
SMS_Package (PackageID,Name,SourceVersion,PkgSourcePath), SMS_DistributionPoint
(PackageID,ServerNALPath,SiteCode), SMS_PackageStatusDistPointsSummarizer
(PackageID,ServerNALPath,State,SourceVersion), SMS_Boundary (BoundaryID,BoundaryType,Value),
SMS_BoundaryGroupMembers (BoundaryID,GroupID), SMS_BoundaryGroup (GroupID,Name),
SMS_BoundaryGroupSiteSystems (GroupID,ServerNALPath,SiteCode), SMS_R_System
(Name,ResourceID,ADSiteName,IPAddresses). A configured source path is not a physical
file-existence check; do not claim the source files were inspected from that field.

Discover the local provider with `Get-CimInstance -Namespace root/SMS -ClassName
SMS_ProviderLocation`. Use its SiteCode for subsequent literal queries in
`root/SMS/site_<code>`. Do not use CIM methods, install/import the console module,
or call distribution/validation/repair actions.

For the package ID obtained from the task or client evidence:

1. Read SMS_Package, filtered by PackageID, to establish existence and SourceVersion.
2. Read SMS_DistributionPoint with the same filter: intended package/DP associations.
3. Read SMS_PackageStatusDistPointsSummarizer with the same filter: deployed status
   and version on each DP. State 0 means installed, 1 pending, 2 retrying, 3 failed,
   4 removal pending, 5 removal retrying, 6 removal failed, 7 updating, 8 monitoring.
   A successful empty query is different from access denied or an unavailable class.
   Absence of both assignment and status for an existing package supports missing
   distribution; a summary alone may lag and does not identify who removed content.
4. Compare the client's relevant address/network with SMS_Boundary, join BoundaryID
   through SMS_BoundaryGroupMembers to SMS_BoundaryGroup, then read matching
   SMS_BoundaryGroupSiteSystems by GroupID. Matching names alone do not prove membership.
   Do not infer every fallback or delivery policy from these basic associations.
   Read BoundaryType and Value: an AD-site boundary is not an IP literal. For it,
   read SMS_R_System filtered by the client Name, including ADSiteName, IPAddresses
   and ResourceID, and correlate its discovered site with the boundary Value.
   Discovery data may be stale; label it as server inventory, not a fresh client
   observation. An empty exact-IP boundary query does not exclude AD-site or subnet
   membership. SMS_BoundaryGroupSiteSystems does not expose a Role property; a null
   projection of that invented property is not missing-role evidence. Existing
   SMS_DistributionPoint objects for other packages on the matching ServerNALPath
   corroborate that the server hosts distributions, without proving this package
   is assigned or healthy there.
5. If distribution is present and current, retain transport, authentication, hash and
   client policy alternatives. A healthy server status is not proof of a successful
   client download. Correlate the exact package, DP, source version and time with a
   separately assigned client member when available. Historical client failure does
   not establish current failure after restoration.

## Inspect representations, not only status

For a content comparison, use the deployed version's inventory and actual files.
Configuration Manager's documented storage model is:

- `PkgLib/<package>.INI` identifies the content IDs and package version.
- `DataLib/<content>/` preserves relative paths; each `<file>.INI` describes the
  original file's size and hash. The INI itself is metadata, not that file.
- The physical file is `FileLib/<first four hash characters>/<full hash>`, without
  an extension. FileLib may span drives; check the configured locations before
  declaring a physical object absent.

Use the selected file/PowerShell tools to list the relevant version's complete
inventory, read its small metadata files, and inspect the corresponding physical
files. Derive each path from observed metadata; do not guess it or hash the INI in
place of the content it describes. Read local source files only when their mapping
to the configured source is verified. A source can have changed since distribution.

Share a compact table keyed by relative path: expected size/hash, actual physical
size/hash and evidence reference. Ask the client member for the corresponding
cache inventory and compare membership as well as each common file's properties.
List concrete differences; then trace whether they affect the failed operation.
Keep whole-package digests separate from individual-file digests. Neither a status
record nor a single matching file establishes the state of all package files.

These are read operations, not ConfigMgr's active validation or redistribution
actions. Do not invoke those actions to fill an evidence gap. Use supported
management mechanisms only in the proposed repair, never edit managed storage.
See [Microsoft's content-library layout](https://learn.microsoft.com/en-us/intune/configmgr/core/plan-design/hierarchy/the-content-library).

Track the stages of the same request separately: waiting for a content location,
receiving a DP, transferring content, hash verification and program execution.
A transport warning during a later successful fallback does not explain an earlier
empty-location/WaitingContent interval. Report the earlier blocker only to the extent
its contemporaneous evidence supports it; current healthy distribution cannot establish
its historical state. If that history is unavailable, leave the historical cause open
while still recognizing a verified current success. A terminal error code is not needed
to identify a demonstrated blocked stage that cannot proceed without its missing input.

For missing distribution, propose distributing this specific content to the intended
eligible DP, waiting for successful matching-version status and checking the original
client download. For failed distribution, obtain the relevant distmgr/pkgxfermgr
evidence before choosing a repair. Never execute the recommendation. The reviewer
must read the decisive server objects independently and preserve unresolved scope.

Microsoft references:
- [Provider discovery](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/misc/sms_providerlocation-server-wmi-class)
- [Boundary membership](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/servers/configure/sms_boundarygroupmembers-server-wmi-class)
- [Distribution status](https://learn.microsoft.com/en-us/intune/configmgr/develop/reference/core/servers/configure/sms_packagestatusdistpointssummarizer-server-wmi-class)
