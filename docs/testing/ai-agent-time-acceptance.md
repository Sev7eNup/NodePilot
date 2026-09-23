# Timestamp evidence correction — 2026-09-19

Branch: `feature/ai-agent-activities`.

## Reproduction and change

The earlier CLIENT1 journal returned `LastWriteTimeUtc` through Windows PowerShell's
legacy `/Date(milliseconds)/` JSON format. Its CBS specialist reported an incorrect UTC
file time and then labeled offset-free log text UTC; subsequent members repeated that
label. These are separate failures: metadata serialization and unsupported interpretation.

Four new regression cases failed before correction. File metadata now formats the
actual target FileInfo.LastWriteTimeUtc directly as invariant round-trip ISO 8601. It
does not parse epoch strings or use the API server's timezone. Full fractional precision
is retained, including the original CBS timestamp `2026-09-19T08:56:00.7399345Z`.

Process results carry the target's observation UTC timestamp, timezone ID and offset
at that observation. Raw log text and shell stdout remain unchanged. Shared agent
instructions and the Windows example skill distinguish those metadata from event time,
preserve source offsets and keep unknown source zones unresolved through delegation.
No timestamp-normalization tool, timezone registry or target-clock change was introduced.

## Automated verification

- 175 selected Engine tests pass, including file timestamps under German/English
  culture, full tick precision and distinct UTC instants at the Pacific autumn overlap.
- 19 AI runtime/configuration tests pass.
- The target process clock fields are checked against actual Windows PowerShell output;
  raw source text is preserved.
- API builds successfully and the updated dev API is healthy on localhost:5000.

The file tests use a simulated remote transport that executes the real generated
PowerShell locally. They are not a new cross-timezone WinRM test.

## Actual model team run

Workflow: `1ca2a02a-4e41-430b-bad5-1264dbc074ca`.
Execution: `863a856a-aaea-48e3-ab1d-2d9b1768f4fe`.
Duration: 35.1 seconds; 8 model calls, 13 tool calls, 2 delegations.

Supervisor, Researcher and Reviewer completed the test. Researcher and Reviewer each
read actual synthetic fixture files. The final structured result independently matched:

| Evidence | Verified result |
|---|---|
| CBS file modification metadata | `2026-09-19T08:56:00.7399345Z`, unchanged precision |
| Offset-free CBS event | Original `2026-09-19 01:53:40`; timezone unknown |
| Ordering that CBS event against another source | Not established |
| Explicit offset event | `2026-09-19T01:53:40-07:00`, unchanged |
| Repeated autumn hour with explicit offsets | `01:30-07:00` before `01:30-08:00` |
| Offset-free Pacific autumn overlap | Ambiguous |
| Pacific spring gap | Invalid local time |
| Legacy epoch string with no suitable conversion tool | Unresolved rather than guessed |

The fixture's Pacific zone was explicitly established for the DST file only. The
controller independently checked overlap/gap classification using Windows TimeZoneInfo.
The expected answers were not placed in model prompts or response-schema constants.

Evidence: `.runlogs/agent-live/time-evidence-team-result.json` and `time-fixtures/`.
Fixture contents were unchanged. The workflow was disabled and the temporary dev
service-identity setting restored. The installed service, VMs and machine timezones
were not changed. Full CLIENT1 blind-team reacceptance remains separate; this model
test is evidence for these cases, not a guarantee of all future narrative reasoning.

## Reference semantics

The implementation uses [round-trip date/time formatting](https://learn.microsoft.com/en-us/dotnet/standard/base-types/standard-date-and-time-format-strings#the-round-trip-o-o-format-specifier).
Microsoft documents [legacy JSON date semantics](https://learn.microsoft.com/en-us/dotnet/framework/wcf/feature-details/stand-alone-json-serialization)
and the need to consider [ambiguous local times](https://learn.microsoft.com/en-us/dotnet/api/system.timezoneinfo.isambiguoustime).
