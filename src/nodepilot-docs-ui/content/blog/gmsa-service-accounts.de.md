# gMSA: Dienstkonten ohne manuell verteilte Kennwörter

Ein gruppenverwaltetes Dienstkonto (group Managed Service Account, gMSA) ist ein Active-Directory-Konto für Windows-Dienste, geplante Aufgaben und ähnliche Prozesse. Sein Kennwort erzeugt und wechselt die Domäne selbst. Nur ausdrücklich zugelassene Computer können es abrufen, sodass niemand es kennen oder verteilen muss.

Ein nächtlicher Export soll auf `srv-job01` laufen und seine Ergebnisse auf einer Dateifreigabe ablegen. Dafür braucht der Prozess eine Domänenidentität. Ein persönliches Benutzerkonto wäre an die falsche Zuständigkeit gebunden, ein herkömmliches Dienstkonto würde zusätzlich die Pflege seines Kennworts verlangen. Für diesen begrenzten Auftrag kommt ein gMSA infrage.

Das Beispiel setzt eine Anwendung voraus, die den Betrieb unter einem gMSA unterstützt. Der Kontotyp allein macht eine ungeeignete Anmeldeimplementierung nicht kompatibel.

## gMSA oder MSA?

Neben dem gMSA gibt es das ältere eigenständige verwaltete Dienstkonto, kurz sMSA oder MSA. Auch dessen Kennwort wird automatisch verwaltet, allerdings vom Computer selbst, und das Konto ist auf genau einen domänengebundenen Server beschränkt. Beim gMSA verwaltet der Domänencontroller das Kennwort, und alle ausdrücklich zugelassenen Hosts können es abrufen. Soll ein Auftrag später auf einen anderen Host umziehen oder auf mehreren Servern laufen, ist das gMSA deshalb die tragfähigere Wahl. [Microsoft: Vergleich der Dienstkonten](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/manage-group-managed-service-accounts).

## Zwei Berechtigungen, die getrennt bleiben

Active Directory verwaltet das Kennwort des gMSA. Berechtigte Hosts können es abrufen, sodass die Administration keinen festen Kennwortwert in jeder Dienstkonfiguration nachführen muss. Welche Computer diesen Abruf durchführen dürfen, wird ausdrücklich festgelegt. Microsoft beschreibt dafür unter anderem eine Sicherheitsgruppe mit den betreffenden Computerkonten. [Microsoft: gMSA verwalten](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/manage-group-managed-service-accounts).

Die Abrufberechtigung des Hosts und die Ressourcenrechte des Dienstkontos erfüllen unterschiedliche Aufgaben. Im Exportbeispiel darf `srv-job01` das Konto verwenden. Auf dem Dateiserver erhält dagegen das gMSA die benötigten Freigabe- und NTFS-Rechte im Exportverzeichnis. Aus der ersten Berechtigung folgt die zweite nicht.

Auch „Schreiben“ sollte konkret werden. Muss der Export vorhandene Dateien ersetzen oder nur neue anlegen, und wer entfernt abgelaufene Ergebnisse? Werden diese Aufgaben getrennt, lässt sich der Zugriff enger fassen. Ein zweiter, unabhängiger Dienst sollte das Konto nicht allein deshalb mitbenutzen, weil die Verbindung zur Freigabe damit bereits funktioniert.

## Die Voraussetzungen vor der Dienstumstellung prüfen

Zur AD-Vorbereitung gehört ein vorhandener und wirksamer KDS-Stammschlüssel. Bei seiner erstmaligen Anlage berücksichtigt Microsoft eine Wartezeit von bis zu zehn Stunden für die Replikation. Die dokumentierte Rückdatierung für ein Testsystem mit nur einem Domänencontroller ist kein allgemeiner Einführungsschritt für den Produktivbetrieb. [Microsoft: KDS-Stammschlüssel erstellen](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/create-the-key-distribution-services-kds-root-key).

Ob bereits ein Schlüssel existiert, zeigt `Get-KdsRootKey`. Fehlt er, legt ein Domänenadministrator ihn einmalig pro Domäne an:

```powershell
Get-KdsRootKey
Add-KdsRootKey -EffectiveImmediately
```

Trotz des Parameters gilt die beschriebene Wartezeit für die Replikation. Anschließend werden die zugelassenen Hosts als Gruppe erfasst und das Konto angelegt. Die Befehle ändern Active Directory und brauchen die dafür delegierten Rechte:

```powershell
New-ADGroup -Name 'gmsaExport-Hosts' -GroupScope Global -GroupCategory Security
Add-ADGroupMember -Identity 'gmsaExport-Hosts' -Members 'srv-job01$'

New-ADServiceAccount -Name 'gmsaExport' `
    -DNSHostName 'gmsaExport.contoso.com' `
    -PrincipalsAllowedToRetrieveManagedPassword 'gmsaExport-Hosts'
```

Auf `srv-job01` wird das Konto danach mit `Install-ADServiceAccount -Identity 'gmsaExport'` installiert. [Microsoft: gMSA erstellen und installieren](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/manage-group-managed-service-accounts).

Eine neue Gruppenmitgliedschaft wirkt auf dem Host allerdings erst, wenn er seine Kerberos-Tickets neu bezieht, in der Regel nach einem Neustart. Scheitern Installation oder Test unmittelbar nach dem Anlegen, ist das deshalb noch kein Beleg für eine falsche Konfiguration.

Nach der zentralen Einrichtung wird auf dem vorgesehenen Ausführungshost geprüft, ob das Konto dort betriebsbereit ist. Der folgende lesende Test setzt das ActiveDirectory-Modul sowie das bereits vorbereitete Konto voraus:

```powershell
Test-ADServiceAccount -Identity 'gmsaExport'
```

`True` bestätigt die lokale Verwendbarkeit des verwalteten Kontos im Umfang dieses Tests. Es prüft weder den Exportprozess noch dessen Zugriff auf das Zielverzeichnis. [Microsoft: Test-ADServiceAccount](https://learn.microsoft.com/en-us/powershell/module/activedirectory/test-adserviceaccount).

Erst danach folgt die anwendungsspezifische Umstellung. Die konkrete Dienstkonfiguration muss den Kontotyp unterstützen und die erforderlichen Anmelderechte besitzen. Verlangt ein Produkt zwingend ein manuell eingegebenes Kennwort, sollte die unterstützte Konfiguration mit seiner Dokumentation geklärt werden. Das verwaltete Kennwort auszulesen und in ein gewöhnliches Kennwortfeld zu kopieren, würde die vorgesehene Verwaltung umgehen.

## Die Abnahme findet im Dienst statt

Für den Export ist ein erfolgreicher manueller Schreibversuch unter dem Administratorkonto ohne Aussagekraft. Der Testauftrag muss durch den späteren Dienst gestartet werden und eine eindeutig zuordenbare Testdatei im vorgesehenen Verzeichnis erzeugen. Anschließend wird geprüft, ob Inhalt und Eigentümerschaft den Erwartungen entsprechen und ob andere Verzeichnisse weiterhin geschützt sind.

Zur Betriebsübergabe gehören der Kontoinhaber und die Liste zugelassener Hosts. Beim Austausch von `srv-job01` muss der neue Host vorbereitet und der alte aus der Berechtigung entfernt werden. Eine Kennwortautomatik übernimmt diese organisatorische Pflege nicht.

Wird der Export eingestellt, lässt sich das gMSA zunächst deaktivieren, bevor es gelöscht wird:

```powershell
Set-ADServiceAccount -Identity 'gmsaExport' -Enabled $false
```

Ein Dienst, der das Konto noch verwendet, fällt dann spätestens bei seinem nächsten Start mit einem Anmeldefehler auf. Der Schritt lässt sich mit `-Enabled $true` rückgängig machen, eine Löschung nicht. [Microsoft: Set-ADServiceAccount](https://learn.microsoft.com/en-us/powershell/module/activedirectory/set-adserviceaccount).

Wird der Export über NodePilot angestoßen, müssen dessen Ausführungsidentität und die Identität des eigentlichen Exportdienstes weiterhin getrennt betrachtet werden. Aus der Unterstützung eines gMSA durch den Zieldienst folgt keine allgemeine Unterstützung dieses Kontotyps in beliebigen Zugangsdatenfeldern.

Der NodePilot-Dienst selbst kann ebenfalls unter einem gMSA laufen. Das Setup trägt dafür eine Abhängigkeit zum Netlogon-Dienst ein, damit das verwaltete Kennwort beim Systemstart bereits abrufbar ist.
