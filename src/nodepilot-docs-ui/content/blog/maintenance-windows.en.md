# A maintenance window is an operating agreement

At 21:58, a job starts whose previous run took twelve minutes. At 22:00, the maintenance window begins. Both times are configured correctly. Whether the job may continue is still unresolved.

The clock cannot answer that question. An agreement is needed between the application owner and those operating the automation. First, the meaning of the window must be explicit: does it permit changes during that period, or suspend selected automation while maintenance takes place? Both are commonly called maintenance windows.

In the following example, 22:00 to 23:00 is a blackout period because a database is undergoing maintenance. The export job must not use it during that time. The times and process are fictional.

## The decision comes before 22:00

If no export connection may remain active at 22:00, blocking new starts from that moment is insufficient. Work already running must have finished or reached a suitable stopping point beforehand. The process needs a lead time that accounts for its usual duration and observed outliers.

A fixed assumption of twelve minutes would leave too little room when data volumes vary widely. For this export, operations might admit no new runs after 21:40 and check for active executions at 21:55. These are proposed rules for this example, not generally applicable defaults.

If a run remains active, a named owner must be able to decide what happens. An export that only reads data and initially writes to a temporary file may allow different treatment from a job that has already posted transactions. The cancellation procedure therefore belongs to the process itself.

## Missed starts leave work to resolve

After 23:00, the blackout is over. That alone does not mean every missed start should be replayed.

A recurring status report often needs only a current observation. Replaying ten reports would query the same present state repeatedly. An export covering successive data intervals, however, must account for every interval still missing. The operational meaning of the job determines the appropriate behavior.

For handover, the agreement could be recorded as follows:

| Situation | Rule in the export example |
|---|---|
| New start from 21:40 | Defer it and record the outstanding work |
| Active run at 21:55 | Notify the owner and inspect its state |
| Database maintenance overruns | Explicitly extend the blackout |
| Database is released for use | Test connectivity and the required function |
| Backlog is processed | Handle missing intervals in order with limited concurrency |

A scheduled end should not replace functional approval. If maintenance overruns, an automatic restart at 23:00 would otherwise occur during the change still in progress.

## What the documented agreement needs

A useful rule identifies the affected resources. If only individual workflows are suspended, another process may still reach the same database. The mapping should therefore start with the application and then cover its consumers.

Recurring windows also need a documented time zone and treatment of clock changes. A time without a time zone is particularly inadequate for teams operating across regions. The place where extensions or exceptions are recorded must also be known in advance.

The agreement can be written independently of the tool used. If it is implemented with NodePilot, each start mechanism then needs verification: which starts are actually blocked, and what happens to work already running or deferred? The calendar entry alone does not provide that evidence.
