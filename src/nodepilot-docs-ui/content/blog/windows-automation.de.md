# Automatisierung im Windows-Betrieb: Welche Arbeit den Aufwand lohnt

Eine regelmäßige Zustandsprüfung kann ein guter erster Automatisierungsauftrag sein. Ihr Ergebnis ist klar umrissen, und schon das Zusammenführen der Informationen erspart wiederkehrende Handarbeit. Eine vollständige Anwendungsbereitstellung verlangt dagegen die Abstimmung mehrerer Beteiligter. Welcher dieser Abläufe zuerst umgesetzt wird, sollte vom erwarteten Nutzen und dem Aufwand für seinen späteren Betrieb abhängen.

Für diese Abwägung zählt der manuelle Aufwand, der nach der Umsetzung übrig bleibt. Eine kurze Laufzeit verliert ihren Wert, wenn anschließend regelmäßig jemand unklare Teilergebnisse rekonstruieren muss. Die Zahl solcher Nacharbeiten ist deshalb ebenso aussagekräftig wie die eingesparte Zeit bei einer erfolgreichen Ausführung.

## Der gewünschte Zustand als Ausgangspunkt

Vor der Umsetzung müssen Zielsysteme und Zuständigkeit feststehen. Dazu gehört eine Erfolgsbedingung, die über „Skript ausgeführt“ hinausgeht. Bei einer Konfigurationsänderung könnte Erfolg bedeuten, dass die vorgesehene Version auf dem Ziel liegt und die Anwendung danach eine festgelegte Funktionsprüfung besteht.

Für die Verteilung einer Konfigurationsdatei auf zwei Anwendungsserver genügt es nicht, eine Rechnerliste in einer Schleife abzuarbeiten. Zunächst muss geklärt sein, ob beide Instanzen gleichzeitig verändert werden dürfen und wie die verbleibende Instanz währenddessen den Betrieb übernimmt. Ist ein Betrieb mit gemischten Konfigurationsständen nicht zulässig, kommt eine schrittweise Umstellung unter Umständen gar nicht infrage.

Ein möglicher Ablauf für eine Anwendung, die eine solche Umstellung unterstützt, sieht folgendermaßen aus:

| Phase | Prüfung oder Aktion | Verhalten bei Abweichung |
|---|---|---|
| Vorbereitung | Freigegebene Konfigurationsversion und Zielrechner bestimmen | Bei unvollständigen Angaben nicht starten |
| Vorprüfung | Erreichbarkeit, Berechtigungen und verbleibende Kapazität prüfen | Änderungen zurückstellen |
| Änderung | Eine Instanz aus dem Verkehr nehmen, bisherigen Stand sichern und neue Konfiguration bereitstellen | Den erreichten Zustand festhalten |
| Funktionsprüfung | Instanz starten und eine fachlich geeignete Anfrage ausführen | Weitere Zielsysteme zunächst nicht verändern |
| Fortsetzung | Geprüfte Instanz wieder aufnehmen und den zweiten Server bearbeiten | Ergebnis je Server getrennt ausweisen |

Der Ablauf macht sichtbar, welche Entscheidungen bisher ein Administrator während der Arbeit getroffen hat. Diese Entscheidungen müssen entweder als Regel beschrieben oder an einer definierten Stelle weiterhin von einem Menschen getroffen werden. Ein Wartungsfenster allein beantwortet beispielsweise nicht, ob die verbleibende Instanz gerade ausreichend Kapazität besitzt.

## Der zweite Lauf gehört zum Entwurf

Ein Auftrag kann erfolgreich auf dem Ziel angekommen sein, obwohl die Rückmeldung den ausführenden Rechner nicht mehr erreicht. Nach einem Timeout ist deshalb zunächst unbekannt, ob die Änderung stattgefunden hat. Ein sofortiger Neustart des gesamten Ablaufs kann bereits erledigte Arbeit erneut ausführen.

Hier hilft Idempotenz: Eine wiederholte Ausführung soll denselben gewünschten Zustand herstellen, ohne zusätzliche Nebenwirkungen zu erzeugen. Für das Konfigurationsbeispiel bedeutet das, die vorhandene Version zu prüfen und eine bereits abgeschlossene Änderung zu erkennen. Ein erneuter Dienstneustart wäre dagegen eine zusätzliche Betriebsunterbrechung. Microsoft erläutert diesen Zusammenhang zwischen Wiederholungen und möglichen Mehrfachausführungen im [Retry Pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/retry).

Nicht jeder Fehler ist vorübergehend. Eine kurzzeitig nicht verfügbare Schnittstelle kann einen begrenzten Wiederholungsversuch rechtfertigen, eine verweigerte Berechtigung verlangt zunächst eine Korrektur. Für wiederholbare Schritte sollten maximale Versuche und Wartezeiten festgelegt sein. Die Wiederholung braucht außerdem eine zuständige Ebene, damit sich Versuche im Skript und in der übergeordneten Ablaufsteuerung nicht unbemerkt vervielfachen.

Wenn die erste Instanz bereits umgestellt wurde und die zweite ausfällt, beschreibt „fehlgeschlagen“ den Zustand nur unzureichend. Der Bericht muss erkennen lassen, welcher Server welchen Konfigurationsstand besitzt und welche Schritte tatsächlich abgeschlossen sind. Andernfalls beginnt die Wiederaufnahme mit einer erneuten Bestandsaufnahme von Hand.

Ein Rollback ist dabei keine universelle Rückspultaste. Eine alte Datei wiederherzustellen kann sinnvoll sein, reicht aber nicht aus, wenn die Anwendung inzwischen Daten verändert oder externe Aktionen ausgelöst hat. Solche Folgen benötigen eigene Gegenmaßnahmen. Microsoft beschreibt dieses Prinzip als [Compensating Transaction](https://learn.microsoft.com/en-us/azure/architecture/patterns/compensating-transaction): Bereits ausgeführte Schritte werden durch passende Folgeaktionen ausgeglichen, deren Verhalten ebenfalls geplant sein muss.

Für die Wiederaufnahme muss nachvollziehbar bleiben, mit welchen Eingaben und welcher Skriptversion der Auftrag gestartet wurde. Ebenso sind die bereits abgeschlossenen Schritte festzuhalten. Verwendet der zweite Versuch zwischenzeitlich geänderten Code, handelt es sich fachlich möglicherweise um einen neuen Auftrag. Dieser Unterschied gehört in den Ausführungsverlauf.

## Mit dem späteren Betriebskonto arbeiten

Ein Skript, das unter einem persönlichen Administratorkonto funktioniert, ist noch nicht für den unbeaufsichtigten Betrieb vorbereitet. Zielzugriffe müssen unter der späteren Ausführungsidentität getestet werden. Dazu gehören neben lokalen Rechten auch Berechtigungen auf Freigaben und den beteiligten Schnittstellen.

Wo die verwendete Anwendung es unterstützt, können gruppenverwaltete Dienstkonten die Kennwortverwaltung übernehmen. Bei einem gMSA verwaltet Windows das Kennwort, während festgelegt wird, welche Systeme das Konto verwenden dürfen. Die erforderlichen Ressourcenrechte müssen weiterhin gezielt vergeben werden. [Microsoft: Group Managed Service Accounts](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/group-managed-service-accounts-overview).

Für die Konfigurationsverteilung sollte das Konto genau die benötigten Zielpfade und Dienste erreichen können. Ein Konto mit umfassenden Domänenrechten nur deshalb einzusetzen, weil damit alle bisherigen Zugriffsfehler verschwinden, verschiebt das Problem in den späteren Betrieb. Jeder weitere Ablauf würde denselben weitreichenden Zugriff erben.

## Ergebnisse, auf die sich der nächste Schritt verlassen kann

PowerShell unterscheidet zwischen verschiedenen Fehlerarten. Ein nicht terminierender Fehler kann ausgegeben werden, während die Verarbeitung weiterläuft. Ein umgebendes `try/catch` allein fängt solche Fehler nicht ab. Mit `-ErrorAction Stop` lässt sich ein solcher Cmdlet-Fehler in einen terminierenden Fehler umwandeln, auf den die Fehlerbehandlung reagieren kann. Native Programme besitzen wiederum eigene Exitcodes, die PowerShell über `$LASTEXITCODE` bereitstellt. Ihre Bedeutung muss anhand des jeweiligen Programms ausgewertet werden. [Microsoft: Fehlerbehandlung in PowerShell](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_error_handling).

Für den Betrieb folgt daraus eine klare Schnittstelle zwischen Skript und Ablaufsteuerung. Ein Schritt sollte sein Ergebnis so zurückgeben, dass Erfolg, fehlende Voraussetzungen und tatsächliche Fehler unterscheidbar sind. Eine Fehlermeldung nur in eine Textdatei zu schreiben und anschließend einen erfolgreichen Abschluss zu melden, lässt den nächsten Schritt auf einer falschen Annahme weiterarbeiten.

Auch fachlicher Erfolg braucht eine eigene Prüfung. Ein laufender Windows-Dienst belegt seinen Prozesszustand, aber noch nicht, dass die Anwendung die neue Konfiguration geladen hat oder eine Anfrage korrekt bearbeitet. Für das Verteilungsbeispiel gehört deshalb eine passende Anwendungsprüfung hinter den Neustart.

## Die Übergabe an den Betrieb

Automatisierung benötigt einen Verantwortlichen, der auf Fehler reagieren und Änderungen bewerten kann. Zum Testumfang gehören deshalb auch unterbrochene Verbindungen, bereits vorhandene Zielzustände und fehlende Berechtigungen. Ebenso sollte geprüft werden, was passiert, wenn derselbe Auftrag zweimal gestartet wird oder eine Ausführung länger dauert als ihr Zeitintervall.

Beim beschriebenen Verteilungsprozess dürfen zwei Aufträge nicht gleichzeitig dieselbe Konfiguration austauschen. Die Begrenzung muss sich auf die betroffene Anwendung oder Ressource beziehen. Eine Parallelitätsgrenze für ein einzelnes Skript genügt nicht, wenn ein zweiter Ablauf dieselben Server unabhängig davon verändert.

Für die spätere Auswertung sollten Protokolle neben Zeitstempel und Zielsystem eine gemeinsame Auftragskennung sowie das geprüfte Ergebnis enthalten. So bleibt nachvollziehbar, welche Ausführungen Nacharbeit erfordert haben und an welcher Stelle der Ablauf angepasst werden muss.

Aus den beschriebenen Anforderungen ergibt sich auch die Auswahl des Werkzeugs. Ein einzelner zeitgesteuerter Auftrag kann mit der Windows-Aufgabenplanung auskommen. Sobald mehrere Systeme voneinander abhängen oder Teilergebnisse den weiteren Verlauf bestimmen, muss die Ablaufsteuerung diese Zusammenhänge ausdrücklich abbilden. Daran ist eine zusätzliche Plattform zu bewerten.

Für die übergeordnete Ablaufsteuerung kann beispielsweise NodePilot vorhandene PowerShell-Schritte zu Workflows verbinden und deren Ausführung nachvollziehbar machen. Bei der Übergabe sollte feststehen, wer einen abgebrochenen Auftrag bewertet und über seine Wiederaufnahme entscheidet.
