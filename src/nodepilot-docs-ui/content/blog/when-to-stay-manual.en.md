# Which tasks can deliberately remain manual

A process performed twice a year may be a good candidate for automation. It may equally deserve a carefully written procedure. Frequency alone says little while the effort involved and the consequences of an error remain unknown.

For an infrequent certificate replacement, an automated inventory could be useful. It could collect certificate bindings and expiry dates while the application owner retains the decision about when to switch. That decision may depend on an announced load test or an acceptance process still in progress, neither of which is represented in the technical system.

Manual work at this point is a deliberately assigned decision. To keep it dependable, the required information and the person authorized to act must be known. A vague instruction such as “check if necessary” leaves the next operator to define the task as well.

## Maintenance effort belongs in the calculation

Suppose an occasional change takes an hour each time. Full automation would require several days and would need reassessment after every major application update. These figures are an illustrative calculation, not measurements. They already show why execution time saved is an insufficient basis on its own.

There may still be good reasons to automate. An error could cause a lengthy outage, or the task might demand precision that is difficult to maintain through manual repetition. Conversely, a technically small change can contain operational decisions whose rules shift with every request. Automation then becomes a representation of changing assumptions that requires continuing maintenance.

For these tasks, separating preparation from judgment is useful. An inventory can be repeated and compared. A proposed change can then be submitted for review with its specific target systems. Only after that decision does a bounded process carry out the approved work.

This shifts the manual activity. The administrator spends less time gathering information and more time assessing a traceable proposal. Approval must not become a routine click. The person approving needs to see which change will be triggered and which observed state the proposal refers to.

## A manual task also needs a completion criterion

Documentation is often the weak point in an infrequent change. The instructions describe the intervention but end before verification. The next operator then knows which button to press without knowing how to recognize the intended result.

A useful procedure therefore includes concrete acceptance criteria. For certificate replacement, this might mean connecting through the name that will actually be used, checking the certificate bound to the service and its trust chain. Successfully importing a certificate into the store describes only an intermediate step.

Stopping and handing over the task belong in the procedure too. If a prerequisite is missing, the operator needs to know which state remains and whom to inform. This clarity also helps later automation because previously implicit decisions have become explicit rules.

The decision to retain a manual step can be revisited. If requests become more frequent, the procedure stabilizes, or the same follow-up work keeps recurring, the basis changes. Until then, a small, well-understood process may provide more value than a complete implementation whose maintenance nobody owns.

NodePilot can connect the repeatable technical steps. Deciding which operational judgment remains with a person is still part of designing how the service is run.
