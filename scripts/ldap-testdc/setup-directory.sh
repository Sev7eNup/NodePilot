#!/bin/bash
set -e
echo "--- creating users + groups ---"
samba-tool user create svc-nodepilot 'Bind#20260724!Kq7z'
samba-tool user create alice.demo 'Login#20260724!Mv3p'
samba-tool user create carol.demo 'Login#20260724!Tw8r'
samba-tool user create bob.demo 'Login#20260724!Zh5c'
samba-tool group add NodePilot-Access
samba-tool group add NodePilot-Admins
# Nested-group test: Admins ist Mitglied von Access; alice ist NUR in Admins.
# tokenGroups muss die transitive Mitgliedschaft in Access liefern.
samba-tool group addmembers NodePilot-Access NodePilot-Admins
samba-tool group addmembers NodePilot-Admins alice.demo
samba-tool group addmembers NodePilot-Access carol.demo
# bob.demo: bewusst in keiner NodePilot-Gruppe -> testet das AllowedGroupSids-Gate

# userPrincipalName deterministisch setzen (Real-AD-Paritaet; ohne UPN-Attribut
# findet NodePilots Post-Bind-Suche `userPrincipalName=<upn>` nichts -> 503)
for u in svc-nodepilot alice.demo carol.demo bob.demo; do
  cat > /tmp/upn.ldif <<LDIF
dn: CN=$u,CN=Users,DC=nodepilot,DC=test
changetype: modify
replace: userPrincipalName
userPrincipalName: $u@nodepilot.test
LDIF
  ldbmodify -H /var/lib/samba/private/sam.ldb /tmp/upn.ldif
done
echo "--- directory setup done ---"
