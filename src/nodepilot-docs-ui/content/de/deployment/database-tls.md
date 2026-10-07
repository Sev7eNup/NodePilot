# Datenbank-Zertifikate

Im Produktivbetrieb verbindet sich NodePilot nur per TLS mit seiner Datenbank und prüft das Serverzertifikat: Der Hostname muss passen, die Kette muss zu einer vertrauenswürdigen Wurzel führen, und bei PostgreSQL muss der Sperrstatus prüfbar sein. Diese Seite zeigt, wie man ein solches Zertifikat erstellt, wenn keine interne CA zur Verfügung steht.

**Mit einer internen CA (etwa AD CS)** braucht es nichts davon: ein Serverzertifikat auf den FQDN des Datenbank-Hosts beantragen und im Abschnitt der eigenen Datenbank bei *Auf dem NodePilot-Server* weitermachen.

Der Hostname im Zertifikat muss genau der Name sein, über den NodePilot verbindet. Eine IP-Adresse oder der Kurzname wird abgelehnt.

## PostgreSQL

Benötigt wird OpenSSL. Git for Windows bringt es mit (`C:\Program Files\Git\usr\bin\openssl.exe`). Die Schritte in PowerShell in einem leeren Ordner auf einer beliebigen Maschine ausführen.

### 1. CA und Serverzertifikat

```powershell
$db = 'pg1.corp.example.com'   # Hostname, über den NodePilot verbindet
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -keyout ca.key -out ca.crt -subj "/CN=NodePilot DB CA" -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
openssl req -newkey rsa:3072 -nodes -keyout server.key -out server.csr -subj "/CN=$db"
Set-Content server.ext "subjectAltName=DNS:$db`r`nextendedKeyUsage=serverAuth" -Encoding ascii
openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -days 825 -extfile server.ext -out server.crt
openssl verify -CAfile ca.crt server.crt
```

Der letzte Befehl muss `server.crt: OK` ausgeben.

### 2. Sperrliste (CRL)

NodePilot prüft, ob das Zertifikat gesperrt wurde. Ohne CRL lehnt es die Verbindung ab, obwohl das Zertifikat gültig ist.

```powershell
Set-Content ca.cnf "[ca]`r`ndefault_ca=db_ca`r`n[db_ca]`r`ndatabase=index.txt`r`ndefault_md=sha256`r`ndefault_crl_days=365" -Encoding ascii
New-Item index.txt -ItemType File | Out-Null
openssl ca -config ca.cnf -keyfile ca.key -cert ca.crt -gencrl -out ca.crl
```

Die CRL gilt ein Jahr. Vor Ablauf den letzten Befehl erneut ausführen und die neue `ca.crl` wie in Schritt 4 importieren. `ca.key`, `ca.cnf` und `index.txt` sicher aufbewahren, nie auf dem Datenbankserver.

### 3. Auf dem PostgreSQL-Server

`server.crt` und `server.key` ins Datenverzeichnis kopieren und in `postgresql.conf` setzen:

```ini
ssl = on
ssl_cert_file = 'server.crt'
ssl_key_file = 'server.key'
```

Das PostgreSQL-Dienstkonto braucht Leserecht auf `server.key`. Konto nachsehen, Recht vergeben (das Beispiel nimmt `NetworkService` und das Datenverzeichnis `C:\PostgreSQL\data`), dann den Dienst neu starten. Unter `LocalSystem` ist keine Freigabe nötig:

```powershell
(Get-CimInstance Win32_Service -Filter "Name like 'postgresql%'").StartName
icacls 'C:\PostgreSQL\data\server.key' /grant 'NT AUTHORITY\NetworkService:R'
Restart-Service postgresql*
```

### 4. Auf dem NodePilot-Server

Die CRL in den Computer-Speicher importieren:

```powershell
certutil -addstore CA ca.crl
```

`ca.crt` dem Installer als Root-Zertifikat übergeben (`-PostgresRootCertificate` oder die PostgreSQL-Seite des Assistenten). Sie landet im Connection-String als `Root Certificate=…`.

### Prüfen

```powershell
$env:PGSSLMODE = 'verify-full'
$env:PGSSLROOTCERT = 'C:\PKI\ca.crt'
psql -h pg1.corp.example.com -U nodepilot -d nodepilot -c "select ssl, version from pg_stat_ssl where pid = pg_backend_pid()"
```

Erwartet: `t | TLSv1.3`. Das Root-Zertifikat als Umgebungsvariable setzen: Im `psql`-Connection-String gehen Windows-Backslashes verloren. `psql` prüft keine Sperrung. Das tut der Pre-Flight des Installers, er bricht bei fehlender CRL ab.

## SQL Server

SQL Server bietet nur Zertifikate an, die RSA mit `KeySpec=KeyExchange` sind. Der CNG-Standardschlüssel von `New-SelfSignedCertificate` ist für ihn unsichtbar, deshalb sind beide Provider-Angaben unten nötig. Als Administrator auf dem SQL-Server ausführen:

```powershell
$cert = New-SelfSignedCertificate -DnsName 'sql1.corp.example.com' `
    -CertStoreLocation Cert:\LocalMachine\My `
    -KeySpec KeyExchange `
    -Provider 'Microsoft RSA SChannel Cryptographic Provider' `
    -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
$key = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
icacls $key /grant 'NT Service\MSSQLSERVER:R'
Export-Certificate -Cert $cert -FilePath C:\Temp\sql1.cer
```

`NT Service\MSSQLSERVER` durch das tatsächliche SQL-Dienstkonto ersetzen, falls es abweicht (`(Get-CimInstance Win32_Service -Filter "Name='MSSQLSERVER'").StartName`).

1. Das Zertifikat im SQL Server Configuration Manager zuweisen: *Protokolle für MSSQLSERVER* → Reiter *Zertifikat*. **Force Encryption bleibt auf No.** NodePilot verschlüsselt seine Verbindung ohnehin selbst, und eine instanzweite Erzwingung bricht jeden anderen Client einer gemeinsam genutzten Instanz, der dem Zertifikat nicht vertraut, etwa entfernte ConfigMgr-Standortsysteme.
2. Den SQL-Server-Dienst neu starten und die ERRORLOG-Zeile `The certificate ... was successfully loaded for encryption` bestätigen.
3. **Auf dem NodePilot-Server** den öffentlichen Teil als vertrauenswürdige Wurzel importieren:

```powershell
Import-Certificate -FilePath C:\Temp\sql1.cer -CertStoreLocation Cert:\LocalMachine\Root
```

SQL Server braucht keine CRL. Den FQDN aus `-DnsName` dem Installer als `-SqlCertificateHostName` übergeben.

## Häufige Fehler

| Meldung | Ursache |
|---|---|
| `The remote certificate was rejected by the provided RemoteCertificateValidationCallback` (PostgreSQL) | CRL nicht importiert oder abgelaufen, Root-Zertifikat nicht angegeben oder Hostname passt nicht |
| `The certificate chain was issued by an authority that is not trusted` (SQL Server) | `.cer` nicht in `LocalMachine\Root` auf dem NodePilot-Server importiert |
| `The target principal name is incorrect` (SQL Server) | Über IP oder Kurzname verbunden statt über den Namen im Zertifikat |
| `Aborted: Postgres pre-flight failed - certificate revocation could not be checked.` | CRL fehlt auf dem NodePilot-Server, siehe Schritt 4 |
