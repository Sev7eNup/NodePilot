# NodePilot Security Audit — 2026-05-15

> **Remediation status (2026-06-24):** The Medium and Low findings below are closed on branch
> `security/eliminate-vulnerabilities`. M-1, M-5, M-7, L-1, L-2, L-3, L-4, L-8 were already resolved
> in the intervening month (verified against current code). M-2, M-3, M-4, M-6, L-6, L-7 were fixed
> in this pass, plus a newly-found gap (A-1: unthrottled Windows-login endpoint). **L-5 was
> deliberately reverted** at the product owner's request — folder-Edit (not folder-Admin) remains
> the gate for folder delete (empty folders only; non-empty deletes are already blocked). See
> [§11 Remediation Log](#11-remediation-log-2026-06-24) for details. Each fix ships with regression
> tests (full Api + Engine suites green).

## 1. Executive Summary

Der Audit deckt das gesamte NodePilot-Backend (ASP.NET Core 10, EF Core, PostgreSQL/SQL Server) sowie die sicherheitsrelevanten Schnittstellen zum Frontend (JWT-Auth, SignalR, REST-API) ab. Die Codebase zeigt ein **überdurchschnittlich hohes Security-Hygiene-Niveau**: parameterisierte Queries, PowerShell-Literal-Quoting, SSRF-Guards mit Connect-Callback-Validation, Output-Redaction, DPAPI-Credential-Verschlüsselung, Edit-Lock-Mechanismus, Circuit-Breaker, Rate-Limiting und ein durchdachtes Audit-Logging sind bereits vorhanden.

**Keine kritischen (Critical) oder hohen (High) Findings** wurden identifiziert. Die verbleibenden Lücken sind überwiegend architekturelle TOCTOU-Race-Conditions im Edit-Lock, Info-Disclosure via Exception-Messages, und Konfigurations-Defaults, die in Produktion gehärtet werden müssen.

**Gesamtscore: 7.4 / 10**

---

## 2. Security Score

| Dimension | Score | Begründung |
|---|---|---|
| **Auth & Session** | 7/10 | JWT + BCrypt + DPAPI solide; Rollen-Staleness und LDAP-Lockout-Lücke ziehen Score nach unten |
| **Input Validation & Injection** | 9/10 | SQL-injection, PowerShell-injection, SSRF, XXE, Path-Traversal alles gehärtet; nur minimale Restrisiken |
| **Secrets & Crypto** | 7/10 | DPAPI + AES-GCM für Credentials; Plaintext-Config-Secrets (by design); Postgres SslMode.Prefer-Default |
| **Web Security** | 7/10 | Security-Headers, CSP, Rate-Limiting vorhanden; AllowedHosts="*" Default, Swagger-Exposure, Info-Disclosure |
| **Data Access & RBAC** | 7/10 | Rollen-gated Endpoints, Edit-Lock; TOCTOU auf Unlock/Publish/Rollback, Folder-Delete-Permission-Lücke |
| **Dependency Health** | 9/10 | Alle NuGet-Pakete aktuell; bekannte CVEs gepinnt; keine neuen verwundbaren Abhängigkeiten |
| **Audit & Observability** | 8/10 | Vollständiges Audit-Logging, Support-Log, OTel; Detail-Granularität gut |
| **Hardening & Defaults** | 6/10 | Mehrere Defaults auf "dev-tolerant" (AllowedHosts, Swagger, SslMode); opt-in Flags vorhanden, aber nicht erzwungen |
| **Gesamt** | **7.4/10** | Keine Critical/High-Findings, aber 7 Medium-Fisks erfordern produktive Härtung |

---

## 3. Kritische Findings (Critical)

Keine kritischen Findings identifiziert.

---

## 4. Hohe Findings (High)

Keine hohen Findings identifiziert.

---

## 5. Mittlere Findings (Medium)

### M-1: JWT Role Claims nicht gegen DB validiert — Stale-Admin nach Demotion

| Feld | Wert |
|---|---|
| **Severity** | Medium |
| **Betroffene Dateien** | `AuthSessionIssuer.cs`, `RoleExtensions.cs`, `ResourceAuthorizationService.cs` |
| **Code-Stelle** | `AuthSessionIssuer.cs:106` — Rolle wird beim Login ins JWT geschrieben, danach nur noch aus Token gelesen |
| **Warum Risiko** | JWT-Rollen sind frozen-at-issue. Ein Admin der zu Operator/Viewer degradiert wird, behält Admin-Rechte bis zum Token-Ablauf (12h) oder bis das Token explizit revoziert wird. `RoleExtensions.cs:12-18` und `ResourceAuthorizationService.cs:70,183` vertrauen ausschließlich `ClaimsPrincipal.IsInRole()`. |
| **Angriffspfad** | (1) Admin-Account wird demoted → (2) User hat noch gültiges JWT mit Role=Admin → (3) Admin-Endpoints bleiben 12h zugänglich |
| **Auswirkung Worst Case** | Unbefugter Admin-Zugriff auf Delete/Credential/Force-Unlock für bis zu 12h |
| **Wahrscheinlichkeit** | Mittel — erfordert Admin-Demotion-Event, aber dann automatisch ausnutzbar |
| **Empfohlener Fix** | (a) Kurzfristig: Bei Rolle-kritischen Endpoints (Delete, Credential-Write, Force-Unlock) DB-Lookup via `ResourceAuthorizationService` ergänzen. (b) Mittelfristig: `TokenValidityMiddleware` um `role_version`-Claim erweitern, der bei Rollenänderung inkrementiert wird → Middleware prüft Claim gegen DB-Wert und lehnt ab bei Mismatch. |
| **Aufwand** | (a) 2h, (b) 4h |
| **Nutzen** | Schließt 12h-Window nach Rollenänderung |
| **Priorität** | P1 |

### M-2: LDAP-Login ohne Per-Account-Lockout

| Feld | Wert |
|---|---|
| **Severity** | Medium |
| **Betroffene Dateien** | `LdapAuthenticator.cs`, `AuthController.cs` |
| **Code-Stelle** | `AuthController.cs:434-504` — LDAP-Pfad gibt nur 401 zurück; `LdapAuthenticator.cs:60-103` — nur Circuit-Breaker, kein `FailedLoginCount` |
| **Warum Risiko** | Lokaler Auth-Pfad hat Account-Lockout (10 Fehlversuche → 15min Sperre, `AuthController.cs:88-89`). LDAP-Pfad hat nur einen Service-Level-Circuit-Breaker (`LdapCircuitBreaker.cs`), der nach 5 konsekutiven Fehlern den gesamten LDAP-Dienst blockiert — nicht den angreifenden Account. |
| **Angriffspfad** | (1) Brute-Force gegen LDAP-Account → (2) Circuit-Breaker löst nach 5 Versuchen global aus → (3) Alle LDAP-User gesperrt für 30s → (4) Nach Cooldown: weitere 5 Versuche → DoS-Schleife |
| **Auswirkung Worst Case** | LDAP-Brute-Force-DoS: Angreifer blockiert alle LDAP-Logins durch kontinuierliche Versuche; keine Per-Account-Sperre |
| **Wahrscheinlichkeit** | Mittel — LDAP ist opt-in, aber wenn aktiv, ist der Angriff trivial |
| **Empfohlener Fix** | Per-IP-Rate-Limit auf LDAP-Login (5/Min, analog zu lokalem Login). Optional: FailedLoginCount pro Username tracken und nach 10 Versuchen 15min Sperre (wie lokaler Pfad). |
| **Aufwand** | 2h |
| **Nutzen** | Verhindert LDAP-Brute-Force-DoS |
| **Priorität** | P1 |

### M-3: Edit-Lock TOCTOU auf Unlock/Publish/Rollback/Delete

| Feld | Wert |
|---|---|
| **Severity** | Medium |
| **Betroffene Dateien** | `WorkflowEditingController.cs`, `WorkflowsController.cs` |
| **Code-Stelle** | `WorkflowEditingController.cs:265-281` (Unlock), `:306-341` (Publish), `:102-146` (Rollback); `WorkflowsController.cs:443-457` (Delete) |
| **Warum Risiko** | Lock (`:213-220`) nutzt atomares CAS via `ExecuteUpdateAsync` mit WHERE-Klausel. Unlock/Publish/Rollback/Delete nutzen `FindAsync` → in-memory Check `EnsureWriteLockAsync` → `SaveChangesAsync`. Zwischen Load und Save kann ein konkurrierender Request den Lock-Status ändern (TOCTOU). |
| **Angriffspfad** | (1) User A und User B feuern gleichzeitig Publish → (2) Beide laden Workflow mit lock-by-A → (3) Beide bestehen `EnsureWriteLockAsync`-Check → (4) Beide mutieren → (5) Zweiter Save überschreibt ersten (kein Concurrency-Token) |
| **Auswirkung Worst Case** | Workflow wird von nicht-Lock-Owner veröffentlicht oder gelöscht; Version-Kollision |
| **Wahrscheinlichkeit** | Niedrig — erfordert gleichzeitige Requests auf denselben Workflow |
| **Empfohlener Fix** | Unlock/Publish/Rollback/Delete auf `ExecuteUpdateAsync` mit WHERE `CheckedOutByUserId == currentUserId` umschreiben (analog zu Lock). Oder Concurrency-Token (`RowVersion`) auf `Workflow` einführen und `DbUpdateConcurrencyException` behandeln. |
| **Aufwand** | 3h |
| **Nutzen** | Schließt TOCTOU-Window vollständig |
| **Priorität** | P1 |

```csharp
// Fix-Beispiel für Unlock (atomar):
var updated = await _db.Workflows
    .Where(w => w.Id == id && w.CheckedOutByUserId == meId)
    .ExecuteUpdateAsync(setters => setters
        .SetProperty(w => w.CheckedOutByUserId, (string?)null)
        .SetProperty(w => w.CheckedOutAt, (DateTime?)null),
    ct);
if (updated == 0) return StatusCode(423, new { code = "not_lock_owner" });
```

### M-4: PostgreSQL SqlActivity SslMode.Prefer-Default

| Feld | Wert |
|---|---|
| **Severity** | Medium |
| **Betroffene Dateien** | `SqlActivity.cs` |
| **Code-Stelle** | `SqlActivity.cs:314-317` — sslMode wird nur gesetzt wenn explizit angegeben; Npgsql-Default ist `Prefer` |
| **Warum Risiko** | `SslMode.Prefer` verbindet zuerst ohne TLS; nur wenn der Server STARTTLS anbietet, wird upgradet. Wenn der Server kein TLS unterstützt, werden Credentials im Klartext übertragen. In Produktivumgebungen mit externem Postgres bedeutet das: Anmeldeinformationen können im Netzwerk mitgeschnitten werden. |
| **Angriffspfad** | (1) MITM auf Netzwerk zwischen API und Postgres → (2) Server unterstützt kein TLS → (3) `Prefer` fällt auf Klartext zurück → (4) Credentials im Netzwerk sichtbar |
| **Auswirkung Worst Case** | Datenbank-Credentials kompromittiert via Netzwerk-Sniffing |
| **Wahrscheinlichkeit** | Mittel — erfordert MITM-Position; in On-Prem-Netzwerken realistischer als Cloud |
| **Empfohlener Fix** | Default auf `SslMode.Require` ändern. Nutzer die explizit `Prefer` wollen können es setzen. Alternativ: `SqlActivity:RequireSslModeRequire=true` opt-in-Flag einführen. |
| **Aufwand** | 1h |
| **Nutzen** | Erzwingt TLS für Postgres-Verbindungen |
| **Priorität** | P1 |

```csharp
// SqlActivity.cs — Default auf Require ändern:
var sslMode = config.GetStringOrNull("sslMode") ?? "Require";
```

### M-5: AllowedHosts="*" Default-Konfiguration

| Feld | Wert |
|---|---|
| **Severity** | Medium |
| **Betroffene Dateien** | `appsettings.json`, `SecurityHardeningSettingsDto.cs`, `SettingsSections.cs`, `SecurityHardeningWarnings.cs` |
| **Code-Stelle** | `appsettings.json:200` — `"AllowedHosts": "*"` |
| **Warum Risiko** | `AllowedHosts="*"` deaktiviert Host-Filtering. ASP.NET Core leitet Requests mit beliebigem `Host`-Header durch, was Cache-Poisoning und Password-Reset-Link-Manipulation ermöglicht. `SecurityHardeningWarnings.cs:47-62` warnt beim Boot, erzwingt aber nichts (nur `Security:StrictAllowedHosts=true` → Exception). |
| **Angriffspfad** | (1) Angreifer sendet Request mit `Host: evil.com` → (2) API akzeptiert → (3) Generierte URLs (Passwort-Reset, Webhooks) nutzen `evil.com` → (4) Opfer klickt Link → Phishing |
| **Auswirkung Worst Case** | Password-Reset-Phishing via manipuliertem Host-Header |
| **Wahrscheinlichkeit** | Mittel — erfordert spezifischen Kontext (Reverse-Proxy ohne Host-Rewrite) |
| **Empfohlener Fix** | Production-Default auf konkrete Hosts setzen (`appsettings.Production.json.template`: `"AllowedHosts": "nodepilot.example.com"`). Boot-Warning auf Error-Level hochstufen oder `StrictAllowedHosts=true` zum Production-Default machen. |
| **Aufwand** | 1h |
| **Nutzen** | Verhindert Host-Header-Injection |
| **Priorität** | P1 |

### M-6: Info-Disclosure via ex.Message in DbAdminController und MachinesController

| Feld | Wert |
|---|---|
| **Severity** | Medium |
| **Betroffene Dateien** | `DbAdminController.cs`, `MachinesController.cs` |
| **Code-Stelle** | `DbAdminController.cs:149` (ArgumentException), `:193` (DbUpdateException), `:255` (DbUpdateException); `MachinesController.cs:254` (WinRM-Fehler) |
| **Warum Risiko** | `ex.Message` und `ex.InnerException.Message` können Datenbank-Constraint-Namen, Tabellennamen, Spaltennamen, WinRM-Interna und interne Hostnamen preisgeben. DbUpdateException auf `:193` und `:255` leckt EF-Fehlermeldungen die Tabellen-/Spaltennamen enthalten. `MachinesController.cs:254` leckt WinRM-Fehlerdetails inkl. Target-Hostname. |
| **Angriffspfad** | (1) Manipulierte PatchRow-Request → (2) Constraint-Verletzung → (3) Response enthüllt `FK_Workflows_Machines` etc. → (4) Angreifer lernt Schema-Namen für gezielte Angriffe |
| **Auswirkung Worst Case** | Schema-Reconnaissance; interne Hostnamen sichtbar |
| **Wahrscheinlichkeit** | Hoch — tritt bei jedem Constraint-Fehler automatisch auf |
| **Empfohlener Fix** | Generische Fehlermeldungen statt `ex.Message`: `BadRequest("Invalid value")`, `Conflict("Operation conflicts with existing data")`. Detaillierte Meldung nur ins Server-Log. |
| **Aufwand** | 1h |
| **Nutzen** | Entfernt Schema-Interna aus API-Responses |
| **Priorität** | P2 |

```csharp
// DbAdminController.cs:193 — Vorher:
return Conflict(new { code = "constraint_violation", message = ex.InnerException?.Message ?? ex.Message });
// Nachher:
_logger.LogWarning(ex, "Constraint violation in PatchRow");
return Conflict(new { code = "constraint_violation", message = "Operation conflicts with existing data" });
```

### M-7: Cookie Secure-Flag-Inkonsistenz: SetAuthCookies vs ClearAuthCookies

| Feld | Wert |
|---|---|
| **Severity** | Medium |
| **Betroffene Dateien** | `AuthSessionIssuer.cs`, `AuthController.cs` |
| **Code-Stelle** | `AuthSessionIssuer.cs:160-163` (Set: `Secure=true` in Production) vs `AuthController.cs:109-132` (Clear: `Secure = Request.IsHttps`) |
| **Warum Risiko** | Wenn der Cookie mit `Secure=true` gesetzt wird (Production), aber der Delete-Aufruf mit `Secure=false` kommt (z.B. HTTP-Request an Kestrel hinter einem SSL-Terminating-Proxy ohne X-Forwarded-Proto), behandelt der Browser Delete als anderen Cookie und das Original bleibt bestehen. Logout schlägt fehl → Session persistiert. |
| **Angriffspfad** | (1) User loggt sich über HTTPS ein (Cookie: Secure=true) → (2) Logout-Request kommt über HTTP → (3) ClearAuthCookies setzt Secure=false → (4) Browser löscht Cookie nicht → (5) Session bleibt aktiv |
| **Auswirkung Worst Case** | Logout-Fehlfunktion auf Shared Devices — Session bleibt nach Logout bestehen |
| **Wahrscheinlichkeit** | Niedrig — erfordert HTTP/HTTPS-Mismatch-Konfiguration |
| **Empfohlener Fix** | `ClearAuthCookies` muss dieselbe `Secure`-Logik wie `SetAuthCookies` verwenden: `Secure = environment.IsDevelopment() ? isHttps : true`. |
| **Aufwand** | 0.5h |
| **Nutzen** | Garantiert Logout-Funktionalität |
| **Priorität** | P2 |

---

## 6. Niedrige Findings (Low)

### L-1: Disabled-User-Login leakt Account-Status

| Feld | Wert |
|---|---|
| **Severity** | Low |
| **Betroffene Dateien** | `AuthController.cs` |
| **Code-Stelle** | Login-Endpoint gibt differentielle Fehlermeldungen: "Account ist deaktiviert" vs. "Ungültige Anmeldedaten" |
| **Warum Risiko** | Ein Angreifer kann unterscheiden ob ein Account existiert+aktiv, existiert+deaktiviert oder nicht existiert — klassischer User-Enumeration-Vektor. |
| **Angriffspfad** | (1) Probiert Username → (2) "Deaktiviert"-Meldung bestätigt Existenz → (3) Gezieltere Angriffe möglich |
| **Auswirkung Worst Case** | Account-Enumeration |
| **Wahrscheinlichkeit** | Hoch — trivial ausnutzbar |
| **Empfohlener Fix** | Einheitliche Fehlermeldung für alle Auth-Failures: "Ungültige Anmeldedaten". Status-spezifische Meldung nur im Server-Log. |
| **Aufwand** | 0.5h |
| **Nutzen** | Verhindert Account-Enumeration |
| **Priorität** | P3 |

### L-2: Swagger UI in Production exponiert

| Feld | Wert |
|---|---|
| **Severity** | Low |
| **Betroffene Dateien** | `Program.cs` (Swagger-Middleware-Registrierung) |
| **Code-Stelle** | Swagger wird ohne Environment-Gate registriert |
| **Warum Risiko** | OpenAPI-Schema enthüllt alle Endpoints, DTOs, Parameter und Auth-Schemata. In Production ein unnötiges Reconnaissance-Werkzeug. |
| **Angriffspfad** | (1) `GET /swagger` → (2) Vollständiges API-Schema sichtbar → (3) Gezielte Angriffe auf spezifische Endpoints |
| **Auswirkung Worst Case** | API-Surface-Enumeration |
| **Wahrscheinlichkeit** | Hoch — öffentlich zugänglich |
| **Empfohlener Fix** | Swagger nur in Development registrieren: `if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }`. Admin-only-Gate alternativ möglich. |
| **Aufwand** | 0.5h |
| **Nutzen** | Versteckt API-Schema in Production |
| **Priorität** | P3 |

### L-3: LLM HTTP-Client ohne Connect-Time-SSRF-Guard

| Feld | Wert |
|---|---|
| **Severity** | Low |
| **Betroffene Dateien** | `LlmServiceCollectionExtensions.cs` |
| **Code-Stelle** | `:25` — Fresh `SocketsHttpHandler` mit `UseProxy=false`, aber ohne `ConnectCallback`/`NetworkGuard` |
| **Warum Risiko** | Der LLM-HTTP-Client nutzt nicht den `RestApiHttpClientProvider` (dessen SSRF-Guard `127.0.0.1` blocken würde). Metadaten-IPs sind blockiert, aber private RFC1918-Adressen sind erreichbar. Da `Llm:BaseUrl` Admin-only konfiguriert ist, ist das Risiko auf interne Netzwerk-Reconnaissance beschränkt. |
| **Angriffspfad** | (1) Admin konfiguriert `Llm:BaseUrl` auf `http://192.168.1.50:8080` → (2) API sendet LLM-Request an internes System → (3) Response enthüllt Service-Existenz |
| **Auswirkung Worst Case** | Interne Netzwerk-Enumeration via LLM-Proxy |
| **Wahrscheinlichkeit** | Sehr niedrig — erfordert Admin-Zugriff auf Config |
| **Empfohlener Fix** | `NetworkGuard` oder `ConnectCallback` (wie `RestApiHttpClientProvider`) auf dem LLM-Handler registrieren. |
| **Aufwand** | 1h |
| **Nutzen** | Konsistente SSRF-Abwehr |
| **Priorität** | P3 |

### L-4: Security-Headers nur außerhalb Development

| Feld | Wert |
|---|---|
| **Severity** | Low |
| **Betroffene Dateien** | Security-Header-Middleware |
| **Code-Stelle** | Headers werden nur bei `!IsDevelopment` gesetzt |
| **Warum Risiko** | In Development fehlen HSTS, CSP, X-Frame-Options etc. Wenn Entwickler den Dev-Server versehentlich extern erreichbar machen, sind keine Schutz-Headers aktiv. |
| **Angriffspfad** | Entwickler exponiert Dev-Server → keine Security-Headers → Clickjacking/XSS-Vektoren offen |
| **Auswirkung Worst Case** | Clickjacking auf versehentlich exponiertem Dev-Server |
| **Wahrscheinlichkeit** | Sehr niedrig |
| **Empfohlener Fix** | Akzeptabel als Design-Entscheidung. Optional: Headers immer setzen, aber HSTS in Dev weglassen. |
| **Aufwand** | 0.5h |
| **Nutzen** | Defense-in-Depth |
| **Priorität** | P3 |

### L-5: SharedFolder-Delete durch Edit statt Admin gegated

| Feld | Wert |
|---|---|
| **Severity** | Low |
| **Betroffene Dateien** | `SharedFoldersController.cs` |
| **Code-Stelle** | Delete-Endpoint prüft `canEdit`, nicht `canAdmin` |
| **Warum Risiko** | Ein User mit Edit-Recht kann Folder löschen, die andere User enthalten. Delete ist eine destruktive Operation die Admin-Rechte rechtfertigen würde. |
| **Angriffspfad** | (1) User hat Edit-Recht auf Folder → (2) Löscht Folder mit Unterordnern → (3) Alle Workflows im Folder verlieren Zuordnung |
| **Auswirkung Worst Case** | Versehentlicher Datenverlust durch Edit-User |
| **Wahrscheinlichkeit** | Niedrig — Backend verhindert Löschen nicht-leerer Folder |
| **Empfohlener Fix** | Delete auf `canAdmin`-Gate umstellen oder mindestens bei Folder mit Kindern `canAdmin` fordern. |
| **Aufwand** | 1h |
| **Nutzen** | Konsistenz der RBAC-Semantik |
| **Priorität** | P3 |

### L-6: Duplicate kopiert IsEnabled ohne Lock-Check

| Feld | Wert |
|---|---|
| **Severity** | Low |
| **Betroffene Dateien** | `WorkflowsController.cs` |
| **Code-Stelle** | Duplicate-Endpoint |
| **Warum Risiko** | Der duplizierte Workflow übernimmt `IsEnabled` aus dem Original, ohne Edit-Lock-Validierung. Ein User kann einen gelockten Workflow durch Duplizieren "umgehen" und die Kopie sofort aktivieren. |
| **Angriffspfad** | (1) Workflow ist gelockt → (2) User dupliziert ihn → (3) Kopie ist sofort enabled → (4) Umgehung des Edit-Locks |
| **Auswirkung Worst Case** | Edit-Lock-Bypass via Duplicate |
| **Wahrscheinlichkeit** | Sehr niedrig — Duplicate erzeugt eigene ID, ist kein direkter Bypass |
| **Empfohlener Fix** | Duplizierte Workflows immer als `IsEnabled=false` anlegen. User muss explizit Enable aufrufen. |
| **Aufwand** | 0.5h |
| **Nutzen** | Konsistenz mit Edit-Lock-Semantik |
| **Priorität** | P3 |

### L-7: ExternalTrigger-Response ohne OutputRedactor

| Feld | Wert |
|---|---|
| **Severity** | Low |
| **Betroffene Dateien** | `ExternalTriggerController.cs` |
| **Code-Stelle** | `:44-55` — `ToResponse` mappt `WorkflowExecution` direkt ohne Redaction |
| **Warum Risiko** | `ExecutionsController` nutzt `OutputRedactor` um sensible Daten (ReturnData, ErrorMessage, InputParametersJson) für Nicht-Admins zu redacten. Der ExternalTrigger-Endpoint gibt diese Felder ungefiltert an den API-Key-Caller zurück. API-Keys haben keine Rollen-Zugehörigkeit, also gibt es keine rollenbasierte Redaction. |
| **Angriffspfad** | (1) Trigger-Request mit API-Key → (2) Response enthüllt ReturnData/ErrorMessage → (3) Potenziell sensible Workflow-Outputs sichtbar |
| **Auswirkung Worst Case** | Sensible Workflow-Outputs über API-Key-Endpoint |
| **Wahrscheinlichkeit** | Mittel — API-Key-Inhaber hat ohnehin Trigger-Berechtigung |
| **Empfohlener Fix** | `OutputRedactor` in `ExternalTriggerController` injizieren und `ToResponse`-Mapping redacten. |
| **Aufwand** | 0.5h |
| **Nutzen** | Konsistente Output-Redaction |
| **Priorität** | P3 |

### L-8: Custom-Redaction-Regex ohne Timeout

| Feld | Wert |
|---|---|
| **Severity** | Low |
| **Betroffene Dateien** | `OutputRedactor.cs` |
| **Code-Stelle** | Custom-Patterns aus `Logging:Redaction:Patterns` werden als Regex kompiliert |
| **Warum Risiko** | Benutzerdefinierte Regex-Muster können ReDoS (Regular Expression Denial of Service) verursachen, wenn sie katastrophales Backtracking aufweisen. Die Regex-Kompilierung hat kein Timeout. |
| **Angriffspfad** | (1) Admin konfiguriert bösartiges/ineffizientes Regex → (2) Output-Redaction blockiert bei jedem Step → (3) Workflow-Execution verlangsamt/hängt |
| **Auswirkung Worst Case** | ReDoS via Custom-Redaction-Pattern |
| **Wahrscheinlichkeit** | Sehr niedrig — erfordert Admin-Zugriff auf Config |
| **Empfohlener Fix** | `Regex.IsMatch` mit `matchTimeout: TimeSpan.FromSeconds(1)` aufrufen. Bei Timeout: Pattern überspringen + Warning-Log. |
| **Aufwand** | 0.5h |
| **Nutzen** | ReDoS-Resilienz |
| **Priorität** | P3 |

---

## 7. Dependency- und CVE-Bewertung

### NuGet-Pakete

| Paket | Version | Status |
|---|---|---|
| ASP.NET Core | 10.0 | Aktuell, keine bekannten CVEs |
| EF Core | 10.0 | Aktuell |
| Npgsql | aktuell | Aktuell |
| Serilog | aktuell | Aktuell |
| Quartz.NET | aktuell | Aktuell |
| Microsoft.PowerShell.SDK | aktuell | Aktuell |
| System.Security.Cryptography.Xml | gepinnt | Explizit auf sichere Version gepinnt (CVE vorher behoben) |
| OpenTelemetry | gepinnt | Version mit bekannter CVE explizit ausgeschlossen |
| JWT Bearer | aktuell | Aktuell |
| Moq / FluentAssertions (Test) | aktuell | Keine Produktions-Abhängigkeit |

**Ergebnis:** Alle Produktions-Abhängigkeiten sind aktuell oder explizit auf sichere Versionen gepinnt. Keine neuen CVEs identifiziert. Die Codebase nutzt gezielte Version-Pinning für bekannte Schwachstellen — proaktiver Ansatz.

### npm-Pakete (Frontend)

Frontend-Abhängigkeiten wurden nicht tiefgehend auditiert (Fokus: Backend). Empfehlung: Regelmäßiges `npm audit` + Dependabot/Renovate aktivieren.

---

## 8. Architektur- und Designrisiken

### A-1: Config-Secrets im Klartext (By Design)

`appsettings.json` und `appsettings.Production.json` enthalten `Jwt:Key`, `Smtp:Password`, `Llm:ApiKey` und den AES-GCM-Master-Key im Klartext. `SecurityHardeningWarnings.cs:74` warnt bei Klartext-Keys, erzwingt aber keine Verschlüsselung. Schutz ausschließlich über File-ACLs.

**Risiko:** Wenn File-Permissions fehlerhaft konfiguriert sind, sind alle Secrets sofort kompromittiert. Kein Secret-Rotation ohne Neustart.

**Mitigation-Pfad:** (a) HashiCorp Vault / Azure Key Vault für Secret-Storage, (b) `dpapi`-Verschlüsselung sensibler Config-Sections, (c) Env-Vars statt Config-File. Langfristig empfehlenswert, aktuell durch File-ACLs für On-Prem akzeptabel.

### A-2: Keine Concurrency-Tokens auf Core-Entities

`Workflow`-Entitäten haben kein `RowVersion`/Concurrency-Token. Bei konkurrierenden Writes (zwei Admins bearbeiten denselben Workflow) gewinnt Last-Write-Wins ohne Konflikt-Erkennung. Der Edit-Lock mildert das Problem, aber TOCTOU-Lücken (M-3) bleiben.

**Mitigation:** Concurrency-Token (`[Timestamp] byte[] RowVersion`) auf `Workflow` einführen. EF Core wirft dann `DbUpdateConcurrencyException` bei konkurrierenden Writes.

### A-3: Single-Process-Architektur (HA-Follow-Up)

Die aktuelle Architektur ist Single-Instance. HA Active/Passive ist auf `feat/ha-active-passive` implementiert, aber nicht in main gemerged. Im Produktivbetrieb ohne HA: Single Point of Failure.

**Status:** Bekannt und adressiert — HA-Branch existiert, Field-Test bestanden.

### A-4: Keine End-to-End-Verschlüsselung für SignalR

SignalR nutzt JWT via `?access_token=`-Query-Parameter. Bei HTTP (ohne TLS) ist das Token im URL-Log des Proxys/Load-Balancers sichtbar. Production-Deployment MUSS HTTPS erzwingen.

**Status:** `KestrelHttpsConfigurator.cs` verfügbar, aber nicht erzwungen. Siehe M-4/M-5.

---

## 9. Priorisierte Maßnahmenliste

| Prio | Finding | Maßnahme | Aufwand | Status |
|---|---|---|---|---|
| **P0** | — | Keine Critical/High-Findings | — | — |
| **P1** | M-1 | JWT Role-Validation: DB-Lookup bei kritischen Endpoints oder `role_version`-Claim | 2-4h | Offen |
| **P1** | M-2 | LDAP Per-IP-Rate-Limit + optional Per-Account-Lockout | 2h | Offen |
| **P1** | M-3 | Unlock/Publish/Rollback/Delete auf `ExecuteUpdateAsync` umstellen | 3h | Offen |
| **P1** | M-4 | SqlActivity SslMode-Default auf `Require` ändern | 1h | Offen |
| **P1** | M-5 | AllowedHosts in Production auf konkrete Hosts setzen, Warning → Error | 1h | Offen |
| **P2** | M-6 | Generische Fehlermeldungen statt `ex.Message` in DbAdmin/Machines | 1h | Offen |
| **P2** | M-7 | `ClearAuthCookies` Secure-Flag mit `SetAuthCookies` synchronisieren | 0.5h | Offen |
| **P3** | L-1 | Einheitliche Login-Fehlermeldung | 0.5h | Offen |
| **P3** | L-2 | Swagger nur in Development | 0.5h | Offen |
| **P3** | L-3 | NetworkGuard auf LLM-HTTP-Handler | 1h | Offen |
| **P3** | L-5 | SharedFolder-Delete auf `canAdmin`-Gate | 1h | Offen |
| **P3** | L-6 | Duplicate: `IsEnabled=false` für Kopie | 0.5h | Offen |
| **P3** | L-7 | OutputRedactor in ExternalTriggerController | 0.5h | Offen |
| **P3** | L-8 | Regex-Timeout in OutputRedactor | 0.5h | Offen |
| **P3** | L-4 | Security-Headers in Dev (optional) | 0.5h | Offen |
| — | A-1 | Secret-Management (Vault/DPAPI-Config) | 8h+ | Empfehlung |
| — | A-2 | Concurrency-Token auf Workflow | 2h | Empfehlung |

**Gesamtaufwand P1+P2:** ~9.5h  
**Gesamtaufwand P3:** ~5.5h  
**Gesamtaufwand Architektur-Empfehlungen:** ~10h+

---

## 10. Fazit zur Produktiv- und Enterprise-Tauglichkeit

**NodePilot ist auf einem soliden Sicherheits-Fundament gebaut.** Die Abwesenheit von Critical- und High-Findings bei einer bewussten Audit-Tiefe bestätigt, dass Security von Anfang an im Design verankert war — nicht nachträglich aufgesetzt. Die Infrastruktur-Schichten (SSRF-Guards, Output-Redaction, Edit-Locks, Audit-Logging, DPAPI) sind professionell implementiert und decken die gängigen OWASP-Top-10-Vektoren ab.

**Für den produktiven Einsatz sind die P1-Findings zu schließen:**

1. **TOCTOU im Edit-Lock** (M-3) — das einzige strukturelle Problem. Die atomare Lock-Implementierung zeigt dass das Team das Muster kennt; die fehlende Konsistenz bei Unlock/Publish ist ein Implementierungs-Lückchen, kein Design-Defizit.
2. **JWT Role-Staleness** (M-1) — 12h-Window nach Rollenänderung ist für Enterprise-Umgebungen zu lang. Kurzfristig via DB-Lookup bei kritischen Endpoints schließbar.
3. **LDAP-Lockout-Lücke** (M-2) — wenn LDAP aktiviert wird, ist Brute-Force-DoS trivial. Per-IP-Rate-Limit schließt das.
4. **Config-Defaults** (M-4, M-5) — `SslMode.Prefer` und `AllowedHosts="*"` sind Dev-freundlich, Production-gefährlich. Die opt-in-Hardening-Flags existieren bereits; sie sollten in der Production-Config aktiviert sein.

**Enterprise-Readiness:** Mit Schließen der P1+P2-Findings und Aktivierung der existierenden Hardening-Flags (`Security:StrictAllowedHosts`, `Kestrel:Https:Enabled`, `Remote:RequireWinRmSsl`) ist NodePilot für den produktiven On-Prem-Einsatz unter Windows geeignet. Für regulierte Umgebungen (SOC2, ISO27001) sollten die Architektur-Empfehlungen (A-1: Secret-Vault, A-2: Concurrency-Token) adressiert werden.

**Security Score: 7.4/10** — mit P1-Fixes erreichbar: **8.5/10**

---

## 11. Remediation Log (2026-06-24)

Branch `security/eliminate-vulnerabilities`. Goal: schließe alle offenen Findings. Vor der
Umsetzung wurde der Ist-Stand jedes Findings gegen den aktuellen Code verifiziert — mehrere waren
in den Wochen seit dem Audit bereits geschlossen worden.

| Finding | Status vor Pass | Maßnahme | Code | Test |
|---|---|---|---|---|
| **M-1** JWT role-staleness | Bereits behoben | `SecurityStamp` wird bei Role-Change + Active-Toggle gebumpt (`UsersController`), `TokenValidityMiddleware` verwirft stale Tokens pro Request | — | bestehend |
| **M-2** LDAP kein Per-Account-Lockout | Offen → **behoben** | Per-Account `FailedLoginCount`/`LockedUntil` im LDAP-Pfad (`TryLdapLoginAsync`), analog zum lokalen 10-Strikes/15-Min-Lockout; gesperrtes Konto wird vor dem Directory-Call abgewiesen | `AuthController.cs` | `AuthControllerLdapTests` (+2) |
| **M-3** Edit-Lock TOCTOU | Offen → **behoben** | Unlock/Delete → atomares `ExecuteUpdate`/`ExecuteDelete` mit Lock-Owner-WHERE; Publish/Rollback → Execution-Strategy-Transaktion mit CAS auf `(CheckedOutByUserId==me, Version==old)` | `WorkflowEditingController.cs`, `WorkflowsController.cs` | `WorkflowsEditLockTests` (+3) |
| **M-4** Postgres `SslMode.Prefer` | Offen → **behoben** | Default ohne explizites `sslMode` ist jetzt `Require` (Plaintext-Fallback nur per Opt-in `sslMode="Disable"`) | `SqlActivity.cs` | `SqlActivityTests` (+2) |
| **M-5** `AllowedHosts="*"` | Bereits behoben | Base-Config = `localhost;127.0.0.1`; Production-Template substituiert FQDN + `Security:StrictAllowedHosts=true` (Boot-Abbruch bei `*`) | — | bestehend |
| **M-6** Info-Disclosure via `ex.Message` | Offen → **behoben** | `MachinesController.TestConnection`: generische Response + CorrelationId; Detail bleibt im Server-Log + Admin-Audit. (DbAdmin war bereits gefixt.) | `MachinesController.cs` | `MachinesControllerTests` (+1) |
| **M-7** Cookie-Secure-Inkonsistenz | Bereits behoben | Set + Clear teilen sich `AuthCookieOptionsBuilder.ResolveSecure` | — | bestehend |
| **L-1** Disabled-User-Enumeration | Bereits behoben | Einheitliche „Invalid credentials"-Antwort; Grund nur in Audit/Metrik | — | bestehend |
| **L-2** Swagger in Production | Bereits behoben | `Swagger:DisableInNonDevelopment=true` im Production-Template | — | bestehend |
| **L-3** LLM-Client SSRF | Bereits behoben | `LlmConnectGuard.ConnectAsync` (Connect-Time-Guard, Link-Local-Block) | — | bestehend |
| **L-4** Security-Headers nur Non-Dev | Bereits behoben (by design) | Header-Pipeline Production-gated; HSTS/CSP/X-Frame/nosniff vollständig | — | bestehend |
| **L-5** Folder-Delete via Edit | **Bewusst zurückgedreht** | War kurzzeitig auf `ResourceOp.Admin` umgestellt, auf Wunsch des Product-Owners aber wieder auf `ResourceOp.Edit` zurückgesetzt: ein FolderEditor darf (leere) Folder löschen; nicht-leere Folder sind ohnehin blockiert. Akzeptiertes Restrisiko (Low). | `SharedWorkflowFoldersController.cs` | — |
| **L-6** Duplicate kopiert `IsEnabled` | Offen → **behoben** | Duplikat ist immer `IsEnabled=false` | `WorkflowsController.cs` | `WorkflowsEditLockTests` (+1) |
| **L-7** ExternalTrigger ohne Redaction | Offen → **behoben** | `OutputRedactor` auf ErrorMessage/ReturnData/InputParametersJson (API-Key-Caller = rollenlos → immer redacten) | `ExternalTriggerController.cs` | `ExecutionsControllerTests` (+1) |
| **L-8** Redaction-Regex ohne Timeout | Bereits behoben | 500ms `matchTimeout` + Fail-Open auf allen Pattern | — | bestehend |
| **A-1** Windows-Login ohne Rate-Limit (neu) | Neu → **behoben** | `[EnableRateLimiting("login")]` auf `POST /api/auth/windows` | `AuthController.cs` | bestehend (Pipeline) |

**Frontend-Dependencies (nachgeholt, Audit §7 hatte diese deferred) — jetzt 0 Findings:**
`npm audit` fand 19 Findings. `npm audit fix` (semver-konform) schloss alle **4 HIGH** (react-router
7.x turbo-stream-RCE / Open-Redirect / CSRF / DoS, `ws`-DoS) + protobufjs + Low/mehrere Moderate.
Die verbleibenden **13 Moderate** stammten alle aus einem Advisory (`@opentelemetry/core <2.8.0`,
W3C-Baggage-Memory-DoS) und wurden durch die **OTel-Web-SDK-Migration 1.x→2.x** geschlossen (alle
`@opentelemetry/*`-Pakete auf 2.x; einzige Breaking-Change im Code: `new Resource()` →
`resourceFromAttributes()` in `src/telemetry/otel.ts`). **`npm audit` (prod + dev): 0 vulnerabilities.**
SPA-Build + `tsc -b` + alle 1720 Vitest-Tests grün.

**Info-Disclosure-Restbereinigung:** Über M-6 hinaus wurden die zwei verbleibenden echten
Internals-Leaks bereinigt: `DiagnosticsController` (Support-Log-Read leakte Server-Dateipfade →
generische Meldung + CorrelationId) und `BackupController` (Malformed-Content hängte rohe
Parser-/Crypto-`ex.Message` an → curated „Malformed backup content."). **Bewusst belassen:** die
restlichen `ex.Message`-Rückgaben sind reines Validierungs-Echo der *eigenen* Eingabe des Callers
(Cron-Ausdruck, hochgeladenes Workflow-/Settings-JSON, Wert-Coercion, curated `BackupFormatException`/
`BackupRestoreException`-Domänenmeldungen) — kein interner System-State, sondern intendiertes,
aktionierbares Feedback; Genericisierung würde legitimes Operator-Debugging verschlechtern.

**Architektur-Empfehlungen:** A-2 (Concurrency-Token) — die *zugrundeliegende* TOCTOU-Lücke ist via
M-3 (atomare CAS-Statements) geschlossen; der Token war nur eine alternative Implementierung, kein
Restrisiko. A-1 (Config-Secrets im Klartext) ist laut §8 eine bewusste On-Prem-Design-Entscheidung
mit existierender Mitigation (File-ACLs + Boot-Warnung via `SecurityHardeningWarnings`), keine
Code-Lücke; ein Vault-Backend (`Secrets:Provider`) existiert bereits als opt-in Enterprise-Feature.

**Ergebnis:** Alle Medium/Low geschlossen, keine Critical/High, Frontend `npm audit` = 0. Score
geschätzt **8.5/10**.