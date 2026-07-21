# Datenbus & Variablen

Steps produzieren Outputs, die im **Datenbus** landen. Downstream-Steps referenzieren sie per `{{…}}`-Template, das der `VariableResolver` vor der Ausführung auflöst.

## Die vier Tails

| Template | Bedeutung |
|---|---|
| `{{varName.output}}` | Stdout |
| `{{varName.error}}` | Stderr |
| `{{varName.success}}` | Step-Erfolg (`"true"` / `"false"`) |
| `{{varName.param.xxx}}` | OutputParameter |

Dazu kommen **Globale Variablen**:

| Template | Bedeutung |
|---|---|
| `{{globals.NAME}}` | Globale Variable (Admin/Op lesen, Admin schreibt) |

## Variablenname

Das `varName` ist der `outputVariable`-Wert des referenzierten Steps. Ist kein `outputVariable` gesetzt, wird die Step-ID verwendet:

```
{{step-123.output}}
```

## Contract-Garantie

**Nur diese vier Tails** (`output`, `error`, `success`, `param.X`) werden aufgelöst — andere Tails bleiben als Literal stehen. Unresolved Templates liefern granulare Diagnostik (StepRunner T-7.1) statt eines stillen Fehlers.

## Strukturierter Output (`runScript`)

`runScript` captured automatisch deklarierte Variablen als `param.*`:

```powershell
$hostName = $env:COMPUTERNAME
```

→ downstream verfügbar als `{{step.param.hostName}}`.

## Auto-Quoting

`{{step.output}}` wird als **Single-Quoted String** in das Script eingesetzt. Daher im Script direkt schreiben:

```powershell
$x = {{step.output}}
```

**nicht**

```powershell
$x = '{{step.output}}'   # falsch — doppelte Quoting
```

## Trigger-Variablen

Trigger-Daten landen als `{{manual.<name>}}` im Run und zusätzlich als `param.*` des Trigger-Nodes (`{{<triggerVar>.param.<name>}}`). Es gibt **kein** `trigger.*`-Namespace — `{{trigger.file.path}}` bleibt ein unresolvetes Literal. Details: [Trigger](../triggers).