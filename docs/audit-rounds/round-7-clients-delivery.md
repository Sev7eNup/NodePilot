# Runde 7 – Clients/Delivery, abschließende Integrationsprüfung

Datum: 12. Oktober 2026. Prüfer: Root. Stand nach dem Produktfix #142.
Beide Skills erneut angewandt, mit dem vom Nutzer eingeschränkten Schweregradumfang.
Ergebnis in diesem Teilumfang: **keine neuen oder offenen Critical-/High- oder
Medium-Funde mit konkreter Alltagsauswirkung**.

Nach der eigenständigen [Runde 6](round-6-clients-delivery.md) wurden die folgenden
Integrationsketten erneut am aktuellen Code verfolgt. Es handelt sich um eine
Quellen- und Vertragsprüfung, nicht um einen weiteren bloßen Testsuitelauf.

| Modulgruppe | Abschließend geprüfter Zusammenhang |
| --- | --- |
| CLI/API | Alle Aufrufer von ListExecutionsAsync inventarisiert; ExecList reicht das Benutzerlimit durch. Die API filtert RBAC, Status und Workflow vor Total/Seite, sortiert stabil nach StartedAt/Id und begrenzt auf 200. Konstante CLI-Seitengröße und finale Teilseite passen hierzu. Default-/Vollabruf und Rückgabeformat bleiben erhalten. |
| MCP/API/Core | Beide Execution-Listenaufrufer gegen tatsächliche API-Filter geprüft; neuester Fehler wird nach Status gefiltert, nicht nach einer bereits abgeschnittenen Seite gesucht. Contract-/Node-/Definition-/Version-/Exportpfade gegen gemeinsamen Redactor und Masken-Roundtrip abgeglichen. Keine neue Berechtigung im Adapter. |
| Switcher/CLI/Hosting | Exitcode 5 entsteht ausschließlich bei 503 oder ConnectionRefused. Reconciler benutzt ihn nur für den initialen Read; Prozessrunner reicht den verknüpften Abbruch durch und beendet den Kindprozess. Auth-/Keyfehler, Mutation und Endverifikation bleiben getrennt. Installierter CLI und Switcher stammen aus demselben Artefakt. |
| Desktop/API | Readiness hat Gesamt- und Einzelrequestbudget. Setup-IPC akzeptiert nur das eigene Setupfenster, sendet das Token im Hauptprozess und übernimmt die gemeinsame Cookiesession. Fehler werfen die Übergabe nur bei ungültigem Token weg; ein erfolgreicher Login führt in die App. Service-Neustart validiert den Namen vor dem privilegierten Befehl. |
| Setup/Update/ACL | Inno PrepareToInstall → geschützter Antwort-/Ergebnisvertrag → Apply-Adapter → Updatepfad. Konfigurationsquelle und gewählter Modus bleiben eindeutig; Erfolgsseite erst nach erfolgreichem Ergebnis. ACL-Helfer behalten Administrators/SYSTEM FullControl und den Daten-/Binärrechteunterschied. Überinstallation wird hierdurch nicht ausgesperrt. Kein neuer Installerfix nötig. |
| CI/Betrieb/Fixtures | Secret-Scan läuft auf PR/main/merge_group, verwendet Prüfsumme und fehlertolerante Negativfixtures. CI-/Delivery- und Grafana-Grenzen aus Runde 6 mit aktuellem Diff abgeglichen: keine intervenierenden Produktänderungen. LoadHarness API/Seeder/Scenario: Publish vor Execute, Unterworkflows zuerst, serverseitiges Running-Total, Terminalfehler als fehlgeschlagene Lastiteration. Adminskripte behalten den gemeinsamen HTTP-/Lock-/Publishvertrag. Samples und reine Fixtures risikobasiert gegen diese Verträge eingeordnet. |

## Ergebnisgrenzen

Die Korrekturen schaffen keine zusätzliche Produktfunktion, verändern keine
Datenbank-/Exportformate und verschieben keine Berechtigungsentscheidung aus
dem Server in einen Client. Die vorhandenen gezielten Regressionen bleiben
maßgeblich; keine Produktänderung dieses Teilprüfers seit ihrem grünen Lauf.

Nicht behauptet: vollständige Zeilenprüfung aller Testdaten und Layouts, erneute
Gesamtsuite, laufender Electron-/Browser-/CM1-Test, echter Installer-/Dienstwechsel
oder Lasttest. Diese Einschränkungen sind keine bestätigten offenen Findings.
Das projektweite Ergebnis wird erst nach allen Teilprüfungen in [Runde 7](round-7.md)
zusammengeführt.
