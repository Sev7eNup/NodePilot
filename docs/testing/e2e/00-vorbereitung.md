# NodePilot E2E Tests — Workflow Designer

Umfassende Test-Anleitung für den Workflow Designer mit Playwright MCP. Diese Datei dokumentiert alle zu testenden Szenarien. Beim Ausführen der Tests diese Checkliste durcharbeiten.

**Verwendung:** `guck bitte in E2ETests.md und führe die E2E-Tests durch`

---

## Vorbereitung & Setup

Backend-Regressionen für Agenten (07.10.2026): `AgentRuntimeTests` prüft den
reservierten Abschluss bei paralleler Modellbudget-Erschöpfung, erhaltene Befunde
und offene Reviews ohne Werkzeugwiederholung. `AgentSkillTests` prüft die tatsächliche
CMD/Bash-Dispatch-Befehlsform bei gleichnamigen Paketdateien; die ungeprüfte Paketdatei
wird im Test nicht ausgeführt. Diese Sicherheits-/Runtime-Prüfungen sind keine UI-Mocks.

### 1. Umgebung starten

```bash
# Terminal 1: Backend starten
cd src/NodePilot.Api && dotnet run --urls "http://localhost:5000"

# Terminal 2: Frontend starten
cd src/nodepilot-ui && npm run dev
```

**Prüfpunkte:**
- [ ] Backend läuft auf `http://localhost:5000`
- [ ] Frontend läuft auf `http://localhost:5173`
- [ ] API ist erreichbar: `GET http://localhost:5000/healthz/live` → 200
- [ ] Frontend lädt ohne Fehler

### 2. Browser starten & Login

1. Öffne `http://localhost:5173`
2. Erstes Mal: Admin-Account wird erstellt (E-Mail + Passwort)
3. Login mit Admin-Credentials
4. Dashboard sollte leer oder mit Demo-Workflows geladen sein

**Prüfpunkte:**
- [ ] Login erfolgreich
- [ ] Dashboard ist sichtbar
- [ ] "New Workflow" Button ist sichtbar
- [ ] Keine Console-Fehler (F12 → Console)

---

