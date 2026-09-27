# gMSA: Dienstkonten ohne manuell verteilte Kennwörter

Ein nächtlicher Export soll auf `srv-job01` laufen und seine Ergebnisse auf einer Dateifreigabe ablegen. Dafür braucht der Prozess eine Domänenidentität. Ein persönliches Benutzerkonto wäre an die falsche Zuständigkeit gebunden, ein herkömmliches Dienstkonto würde zusätzlich die Pflege seines Kennworts verlangen. Für diesen begrenzten Auftrag kommt ein gruppenverwaltetes Dienstkonto, ein gMSA, infrage.

Das Beispiel setzt eine Anwendung voraus, die den Betrieb unter einem gMSA unterstützt. Der Kontotyp allein macht eine ungeeignete Anmeldeimplementierung nicht kompatibel.

## Zwei Berechtigungen, die getrennt bleiben

Active Directory verwaltet das Kennwort des gMSA. Berechtigte Hosts können es abrufen, sodass die Administration keinen festen Kennwortwert in jeder Dienstkonfiguration nachführen muss. Welche Computer diesen Abruf durchführen dürfen, wird ausdrücklich festgelegt. Microsoft beschreibt dafür unter anderem eine Sicherheitsgruppe mit den betreffenden Computerkonten. [Microsoft: gMSA verwalten](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/manage-group-managed-service-accounts).

Die Abrufberechtigung des Hosts und die Ressourcenrechte des Dienstkontos erfüllen unterschiedliche Aufgaben. Im Exportbeispiel darf `srv-job01` das Konto verwenden. Auf dem Dateiserver erhält dagegen das gMSA die benötigten Freigabe- und NTFS-Rechte im Exportverzeichnis. Aus der ersten Berechtigung folgt die zweite nicht.

Auch „Schreiben“ sollte konkret werden. Muss der Export vorhandene Dateien ersetzen oder nur neue anlegen, und wer entfernt abgelaufene Ergebnisse? Werden diese Aufgaben getrennt, lässt sich der Zugriff enger fassen. Ein zweiter, unabhängiger Dienst sollte das Konto nicht allein deshalb mitbenutzen, weil die Verbindung zur Freigabe damit bereits funktioniert.

## Die Voraussetzungen vor der Dienstumstellung prüfen

Zur AD-Vorbereitung gehört ein vorhandener und wirksamer KDS-Stammschlüssel. Bei seiner erstmaligen Anlage berücksichtigt Microsoft eine Wartezeit von bis zu zehn Stunden für die Replikation. Die dokumentierte Rückdatierung für ein Testsystem mit nur einem Domänencontroller ist kein allgemeiner Einführungsschritt für den Produktivbetrieb. [Microsoft: KDS-Stammschlüssel erstellen](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/create-the-key-distribution-services-kds-root-key).

Nach der zentralen Einrichtung wird auf dem vorgesehenen Ausführungshost geprüft, ob das Konto dort betriebsbereit ist. Der folgende lesende Test setzt das ActiveDirectory-Modul sowie das bereits vorbereitete Konto voraus:

```powershell
Test-ADServiceAccount -Identity 'gmsaExport'
```

`True` bestätigt die lokale Verwendbarkeit des verwalteten Kontos im Umfang dieses Tests. Es prüft weder den Exportprozess noch dessen Zugriff auf das Zielverzeichnis. [Microsoft: Test-ADServiceAccount](https://learn.microsoft.com/en-us/powershell/module/activedirectory/test-adserviceaccount).

Erst danach folgt die anwendungsspezifische Umstellung. Die konkrete Dienstkonfiguration muss den Kontotyp unterstützen und die erforderlichen Anmelderechte besitzen. Verlangt ein Produkt zwingend ein manuell eingegebenes Kennwort, sollte die unterstützte Konfiguration mit seiner Dokumentation geklärt werden. Das verwaltete Kennwort auszulesen und in ein gewöhnliches Kennwortfeld zu kopieren, würde die vorgesehene Verwaltung umgehen.

## Die Abnahme findet im Dienst statt

Für den Export ist ein erfolgreicher manueller Schreibversuch unter dem Administratorkonto ohne Aussagekraft. Der Testauftrag muss durch den späteren Dienst gestartet werden und eine eindeutig zuordenbare Testdatei im vorgesehenen Verzeichnis erzeugen. Anschließend wird geprüft, ob Inhalt und Eigentümerschaft den Erwartungen entsprechen und ob andere Verzeichnisse weiterhin geschützt sind.

Zur Betriebsübergabe gehören der Kontoinhaber und die Liste zugelassener Hosts. Beim Austausch von `srv-job01` muss der neue Host vorbereitet und der alte aus der Berechtigung entfernt werden. Eine Kennwortautomatik übernimmt diese organisatorische Pflege nicht.

Wird der Export über NodePilot angestoßen, müssen dessen Ausführungsidentität und die Identität des eigentlichen Exportdienstes weiterhin getrennt betrachtet werden. Aus der Unterstützung eines gMSA durch den Zieldienst folgt keine allgemeine Unterstützung dieses Kontotyps in beliebigen Zugangsdatenfeldern.
