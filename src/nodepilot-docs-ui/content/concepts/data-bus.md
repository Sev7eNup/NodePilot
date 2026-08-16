# Datenbus & Variablen

Activities können Ergebnisse für nachfolgende Activities bereitstellen. Ein Zugriff erfolgt mit `{{…}}`.

## Verfügbare Werte

| Vorlage | Bedeutung |
|---|---|
| `{{hostInfo.output}}` | Standardausgabe |
| `{{hostInfo.error}}` | Fehlerausgabe |
| `{{hostInfo.success}}` | Erfolg als `true` oder `false` |
| `{{hostInfo.param.name}}` | Benannter Ausgabewert |
| `{{globals.NAME}}` | Globale Variable |
| `{{manual.NAME}}` | Eingabe eines Triggers |

`hostInfo` ist die **Output Variable** der vorherigen Activity. Ohne Output Variable wird die Node-ID verwendet.

Ein `{{manual.NAME}}`, das der Lauf nicht führt, lässt den Schritt mit „Unknown trigger input(s)" fehlschlagen — der Platzhalter läuft nicht still als Text mit.

## Sichtbarkeit: nur Vorgänger

Eine Activity kann nur Ergebnisse von Vorgängern verwenden. Zwischen dem erzeugenden Node und der verwendenden Activity muss ein Pfad bestehen.

```text
        ┌──► B  ("Hole Benutzername")
Start ──┤
        └──► C ──► D  ("Schreibe {{B.output}}")
```

`D` kann `B` nicht lesen, weil kein Pfad von `B` zu `D` führt. Für den Zugriff müssen die Zweige vor `D` zusammengeführt werden, zum Beispiel mit einer Junction.

## Verwendung in PowerShell

Variablen werden direkt in das Skript eingesetzt:

```powershell
$computerName = {{hostInfo.output}}
```

Zusätzliche Anführungszeichen sind nicht erforderlich.

Für die Fehlerbehandlung gibt es einen Unterschied zwischen `runScript` und allen anderen Activities:

- **Andere Activities:** Eine nicht auflösbare Variable bricht den Schritt ab und nennt die betroffene Referenz.
- **`runScript` und Custom Activities:** Diese lösen ihre Vorlagen selbst auf, weil ein `{{…}}` auch gewollter Skripttext sein kann. Ein Tippfehler oder ein unbekannter Schritt bleibt deshalb als Text im Skript stehen und lässt den Schritt **nicht** fehlschlagen. Abgebrochen wird die Referenz auf einen Schritt, der zum Workflow gehört, aber außerhalb des eigenen Vorgängerpfads liegt — unabhängig davon, ob dieser Schritt bereits fertig ist.

Ein Schritt, der grün ist, aber `{{…}}` in der Ausgabe zeigt, ist deshalb fast immer ein Schreibfehler im Variablennamen.
