# Data bus & variables

Activities can make results available to subsequent activities. They are accessed with `{{…}}`.

## Available values

| Template | Meaning |
|---|---|
| `{{hostInfo.output}}` | Standard output |
| `{{hostInfo.error}}` | Error output |
| `{{hostInfo.success}}` | Success as `true` or `false` |
| `{{hostInfo.param.name}}` | A named output value |
| `{{globals.NAME}}` | A global variable |
| `{{manual.NAME}}` | A trigger input |

`hostInfo` is the **output variable** of the preceding activity. Without an output variable, the node ID is used.

A `{{manual.NAME}}` the run does not carry makes the step fail with "Unknown trigger input(s)" — the placeholder does not silently travel on as text.

## Visibility: predecessors only

An activity can only use results from its predecessors. There must be a path between the producing node and the consuming activity.

```text
        ┌──► B  ("Get user name")
Start ──┤
        └──► C ──► D  ("Write {{B.output}}")
```

`D` cannot read `B`, because there is no path from `B` to `D`. To access it, the branches have to be merged before `D`, for example with a junction.

## Use in PowerShell

Variables are substituted straight into the script:

```powershell
$computerName = {{hostInfo.output}}
```

No additional quotes are needed.

For error handling there is a difference between `runScript` and every other activity:

- **Other activities:** an unresolvable variable aborts the step and names the reference concerned.
- **`runScript` and custom activities:** these resolve their templates themselves, because a `{{…}}` can also be intentional script text. A typo or an unknown step therefore stays in the script as text and does **not** make the step fail. What *is* aborted is a reference to a step that belongs to the workflow but lies outside the step's own predecessor path — regardless of whether that step has already finished.

A step that is green but shows `{{…}}` in its output is therefore almost always a misspelled variable name.
