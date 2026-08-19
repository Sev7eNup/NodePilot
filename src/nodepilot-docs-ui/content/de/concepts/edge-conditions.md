# Edge-Bedingungen

Eine Edge verbindet zwei Nodes. Eine Bedingung legt fest, wann der Ziel-Node ausgeführt wird.

## Bedingung festlegen

1. Edge auf der Canvas auswählen.
2. Im Properties-Panel die Bedingung auswählen.
3. Workflow speichern und veröffentlichen.

## Grundbedingungen

| Bedingung | Wirkung |
|---|---|
| **Always** | Ziel-Node immer ausführen |
| **On Success** | Nur nach erfolgreichem Ausgangs-Node ausführen |
| **On Failure** | Nur nach fehlgeschlagenem Ausgangs-Node ausführen |
| **Custom** | Werte mit eigenen Regeln vergleichen |

## Eigene Bedingung

Eine eigene Bedingung besteht aus:

- einem Wert aus einer vorherigen Activity, einem Trigger oder einer globalen Variable,
- einem Vergleich wie `gleich`, `ungleich`, `größer`, `kleiner`, `enthält` oder `ist leer`,
- einem Vergleichswert.

Mehrere Vergleiche lassen sich mit **UND**, **ODER** und **NICHT** kombinieren.

Beispiel:

```text
{{diskCheck.param.freeGb}} ist kleiner als 5
```

Der nachfolgende Node läuft nur, wenn weniger als 5 GB frei sind.

## Deaktivierte Verbindungen

Eine deaktivierte Edge wird nicht berücksichtigt. Besitzt ein Node danach keinen erreichbaren eingehenden Pfad, wird er übersprungen.
