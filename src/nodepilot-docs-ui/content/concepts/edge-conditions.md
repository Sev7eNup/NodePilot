# Edge-Bedingungen

Edges steuern, ob der Target-Node ausgeführt wird. Die Bedingung lebt in `edge.data`.

## Shortcut-Conditions

| `condition` | Bedeutung |
|---|---|
| `stepId.success` | Nur wenn der Source-Step erfolgreich war |
| `stepId.failed` | Nur wenn der Source-Step fehlgeschlagen ist |
| `null` / leer | Immer (unconditional) |
| `disabled: true` | Edge wird übersprungen — Target-Node wird nicht zum Root |

## Strukturierte Conditions (`conditionExpression`)

Für komplexe Bedingungen dient ein AST mit drei Node-Typen. Der Diskriminator für den Node-Typ heißt **`type`**, der für den Operanden-Typ heißt **`kind`**:

| `type` | Form |
|---|---|
| `comparison` | `{ "type": "comparison", "left": OPERAND, "op": OP, "right": OPERAND? }` |
| `group` | `{ "type": "group", "op": "AND"\|"OR", "children": [...] }` |
| `not` | `{ "type": "not", "child": {...} }` |

> Achtung: Der Operator steht immer im Feld **`op`** (nicht `operator`/`logic`), der Operand-Typ in **`kind`** (nicht `type`). Falsche Feldnamen werden vom Evaluator ignoriert und still mit Defaults belegt — ein `operator: ">"` wird z. B. als `==` ausgewertet, ein `type: "variable"` als leerer Literal. Die Bedingung scheint gültig, evaluiert aber falsch.

### Vergleichs-Operatoren (`op`)

`==`, `!=`, `<`, `>`, `<=`, `>=` (numerisch wenn beide Seiten als Zahl parsebar, sonst String), `contains`, `startsWith`, `endsWith`, `matches` (Regex), sowie unär `isEmpty`, `isNotEmpty`, `isTrue`, `isFalse` (dann ohne `right`).

### Operanden

Jeder Operand ist `{ "kind": "variable" | "literal", … }`:

- **`literal`** — `{ "kind": "literal", "value": "5" }`. Ein Inline-Wert; `value` darf `{{globals.X}}`/`{{manual.X}}`/`{{step.output}}`-Templates enthalten, die vor dem Vergleich aufgelöst werden.
- **`variable`** — referenziert einen Datenbus-Pfad **strukturiert**, nicht als `{{...}}`-String:
  `{ "kind": "variable", "stepId": "diskCheck", "field": "param", "paramName": "freeGb" }`.
  `field` ist `output` / `error` / `success` / `param`; `paramName` nur bei `field: "param"`. `stepId` darf auch der `outputVariable`-Name des Steps sein.
  Optional `source: "global" | "manual"` (statt `stepId`/`field`) mit flachem `name`: referenziert `{{globals.NAME}}` bzw. `{{manual.NAME}}`.

> Safe-fail: Unauflösbare Variablen werden zum leeren String; Vergleiche dagegen liefern `false` (außer `!=` und `isEmpty`/`isNotEmpty`).

## Beispiel

```json
{
  "type": "group",
  "op": "AND",
  "children": [
    { "type": "comparison", "op": ">",
      "left":  { "kind": "variable", "stepId": "diskCheck", "field": "param", "paramName": "freeGb" },
      "right": { "kind": "literal",  "value": "5" } },
    { "type": "not",
      "child": { "type": "comparison", "op": "isEmpty",
                 "left": { "kind": "variable", "stepId": "diskCheck", "field": "param", "paramName": "drive" } } }
  ]
}
```

Liest als: „freier Speicher > 5 **und** Laufwerksname nicht leer."

## Node-Level `disabled`

`data.disabled: true` auf einem Node → Node wird `Skipped`; Downstream ohne andere Quellen ebenfalls. Alle eingehenden Edges disabled → Target-Node wird `Skipped`.