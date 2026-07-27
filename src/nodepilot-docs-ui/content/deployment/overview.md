# Betriebsarten

NodePilot unterstützt drei Betriebsarten. Die Auswahl bestimmt Installation, Netzwerkzugriff, Datenbank, Dienstkonto und verfügbare Enterprise-Funktionen. Workflow-Engine und Workflow-Format bleiben gleich.

## Auswahl in einer Minute

| Anforderung | Geeignete Betriebsart |
|---|---|
| Entwicklung am NodePilot-Quellcode | **Installation aus Quellcode** |
| Lokale Automatisierung auf einem Windows-11-Einzelplatz | **Desktop-App** |
| Zugriff durch mehrere Personen | **Windows-Server-Deployment** |
| Eingehende Webhooks oder externe REST-Aufrufe | **Windows-Server-Deployment** |
| LDAP, Windows SSO, OIDC oder SCIM | **Windows-Server-Deployment** |
| Active/Passive-Hochverfügbarkeit | **Windows-Server-Deployment** |

## Technischer Vergleich

| Merkmal | Quellcode-Installation | Windows-Server | Desktop-App |
|---|---|---|---|
| Zweck | Entwicklung und Test | Produktiver Team-Betrieb | Produktiver Einzelplatz |
| Betriebssystem | Windows | Windows Server 2022/2025 | Windows 11 x64 |
| Installation | Quellcode, manuelle Prozesse | Signiertes ZIP und PowerShell-Skripte | Signierter Inno-Setup-Installer |
| Backend | `dotnet run` | Windows-Dienst | Windows-Dienst |
| Oberfläche | Vite-Dev-Server | Vom Backend ausgelieferte SPA | Vom Backend ausgelieferte SPA in Electron |
| Datenbank | Lokales PostgreSQL | Externer SQL Server 2022 oder PostgreSQL 16+ | Mitgeliefertes PostgreSQL 16 |
| Netzwerk | Lokale Entwicklungsports | HTTPS im Netzwerk | Nur Loopback |
| Dienstkonto | Interaktiver Benutzer | LocalSystem oder gMSA | LocalSystem |
| TLS | Optional im lokalen Test | Zertifikat aus `LocalMachine\My` | Self-signed Loopback-Zertifikat mit Pinning |
| Eingehende Webhooks und externe Trigger-API | Lokal testbar | Unterstützt | Nicht erreichbar |
| Zeitplan-, Datei-, Datenbank- und Eventlog-Trigger | Bei laufenden Entwicklungsprozessen | Verfügbar | Verfügbar |
| Ausgehende Verbindungen, zum Beispiel WinRM, REST, SQL und SMTP | Verfügbar | Verfügbar | Verfügbar |
| Betrieb ohne geöffnete Oberfläche | Nur bei laufenden Entwicklungsprozessen | Ja | Ja |
| Enterprise-Authentifizierung | Technisch testbar | Unterstützter Zielpfad | Nicht verfügbar |
| Hochverfügbarkeit | Nein | Active/Passive möglich | Nein |
| Anleitung | [Installation](../getting-started/installation) | [Windows-Server-Deployment](./production) | [Desktop-App](./desktop) |

Bei der Desktop-App betrifft **Nur Loopback** ausschließlich eingehende Verbindungen. Automatische Trigger und ausgehende Verbindungen bleiben verfügbar. Das Schließen des Fensters beendet laufende Workflows nicht. Lokale `runScript`-Activities laufen unter `LocalSystem`; Remote-WinRM benötigt gespeicherte Zugangsdaten.

## Nicht unterstützte Deployment-Formen

Linux, Container, Kubernetes, Helm, systemd, IIS-Hosting und Cloud-spezifische Managed-App-Pakete sind keine unterstützten Produktionsziele. Das unterstützte Serverziel ist ein Windows-Dienst mit direktem Kestrel-HTTPS. Das dokumentierte Active/Passive-Szenario verwendet einen vorgeschalteten Load Balancer.

## Wechsel der Betriebsart

Ein Wechsel erfolgt über ein System-Configuration-Backup: Backup erstellen, Zielsystem neu installieren, Backup wiederherstellen und Zielsystem prüfen. Das direkte Verschieben eines Installationsverzeichnisses wird nicht unterstützt.

Das Backup enthält Konfiguration, Workflows und verschlüsselte Secrets. Ausführungshistorie, Audit-Daten und Statistiken erfordern zusätzlich ein natives Datenbank-Backup. Details enthält [Import, Export und Backup](../import-export).
