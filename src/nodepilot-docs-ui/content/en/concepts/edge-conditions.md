# Edge conditions

An edge connects two nodes. A condition determines when the target node is executed.

## Setting a condition

1. Select the edge on the canvas.
2. Choose the condition in the properties panel.
3. Save and publish the workflow.

## Basic conditions

| Condition | Effect |
|---|---|
| **Always** | Always execute the target node |
| **On Success** | Execute only after the source node succeeded |
| **On Failure** | Execute only after the source node failed |
| **Custom** | Compare values using your own rules |

## A custom condition

A custom condition consists of:

- a value from a previous activity, a trigger or a global variable,
- a comparison such as `equals`, `not equals`, `greater than`, `less than`, `contains` or `is empty`,
- a value to compare against.

Several comparisons can be combined with **AND**, **OR** and **NOT**.

Example:

```text
{{diskCheck.param.freeGb}} is less than 5
```

The following node runs only if less than 5 GB are free.

## Disabled connections

A disabled edge is not considered. If a node then has no reachable incoming path, it is skipped.
