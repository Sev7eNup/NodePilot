#!/bin/bash
set -e
ADMIN_PASS="${ADMIN_PASS:-DcRoot#20260724!Aa}"

if [ ! -f /var/lib/samba/private/sam.ldb ]; then
  echo "=== Provisioning Samba AD DC (realm NODEPILOT.TEST) ==="
  rm -f /etc/samba/smb.conf
  samba-tool domain provision \
    --realm=NODEPILOT.TEST \
    --domain=NPTEST \
    --server-role=dc \
    --dns-backend=SAMBA_INTERNAL \
    --adminpass="$ADMIN_PASS" \
    --option="tls enabled = yes" \
    --option="tls keyfile = /certs/server.key" \
    --option="tls certfile = /certs/server.crt" \
    --option="tls cafile = /certs/ca.crt"
  /setup-directory.sh
  echo "=== Provisioning done ==="
fi
echo "=== Starting samba in foreground ==="
exec samba --foreground --no-process-group --debuglevel=1
