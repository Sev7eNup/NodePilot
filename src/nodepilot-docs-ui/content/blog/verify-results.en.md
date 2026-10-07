# A green status does not prove the job is done

The installer returns `3010`. Software deployment marks the machine as failed, although Windows Installer classifies the operation as successful with a restart required. On another server, the same job returns `0` while the application continues to use its old configuration. Both indicators reflect the signal being evaluated. Neither fully establishes whether the job achieved its purpose.

The application and version numbers in the following examples are fictional. The underlying question applies to any process whose outcome exists outside the process executing it.

## Three claims with different scopes

For a deployment of ContosoAgent 4.2, operations might require the following evidence:

| Claim | Suitable evidence | Remaining question |
|---|---|---|
| The installer has finished | Documented exit code from the process that was started | Is a restart still required? |
| The intended version is present | A product-specific version query on the target | Does the running process use that version? |
| The application performs its intended function | A defined functional check with an expected result | Does the finding apply to every affected instance? |

These checks belong at different points in time. If a necessary restart must wait until the next maintenance window, deployment remains operationally incomplete until the subsequent check. An additional state such as “restart pending” describes this more accurately than a final success verdict.

Windows Installer distinguishes, among other codes, `0` for success, `3010` for success requiring a restart, and `1641` for a restart already initiated. These meanings apply to Windows Installer. They should not be transferred to other programs without verification. [Microsoft: Windows Installer return codes](https://learn.microsoft.com/en-us/windows/win32/msi/error-codes).

The orchestration layer needs an explicit mapping. An unknown code should retain its original number and prompt investigation. Treating it as “probably successful” removes precisely the information that will be needed later.

## Evidence should not merely confirm itself

Copying a file and then observing only that the copy command reported no error provides weak acceptance evidence. Examining the destination tells more: does its content match the approved file, and has the application actually adopted the change?

Even a version file can mislead if it is updated independently of the running service. An application status query that reports the loaded version would suit this example. The appropriate check must follow from the application's behavior. A generic HTTP `200` response is insufficient when the route merely confirms that the web server is reachable.

Backups make this distinction particularly clear. In SQL Server, `RESTORE VERIFYONLY` checks whether a backup set is complete and readable, but it does not restore the database or verify the structure of all data it contains. A planned restore exercise addresses different questions, including required keys, dependencies, and the recovery time actually achieved. [Microsoft: RESTORE VERIFYONLY](https://learn.microsoft.com/en-us/sql/t-sql/statements/restore-statements-verifyonly-transact-sql).

## A result that supports the next decision

The completion report should connect each observation to its time and target. “Version 4.2 confirmed on srv-app01 after restart” provides firmer evidence than “installation successful”. If the check fails to run, the state remains unknown. Missing evidence does not automatically prove an application failure.

Resuming work also requires knowing which changes have already taken place. If the software was installed and only the functional check was interrupted, the next attempt should first complete that check. Running the installer again would be a separate decision.

In NodePilot, these checks can occupy their own steps after the change. Technical completion of an Activity and operational acceptance of its result then each have a traceable place in the workflow.
