FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
      samba winbind samba-ad-provision samba-dsdb-modules samba-vfs-modules ldb-tools \
    && rm -rf /var/lib/apt/lists/*
# Zertifikate vorher mit make-certs.ps1 erzeugen (certs/ ist gitignored).
COPY certs/server.key certs/server.crt certs/ca.crt /certs/
RUN chmod 600 /certs/server.key
COPY entrypoint.sh /entrypoint.sh
COPY setup-directory.sh /setup-directory.sh
RUN chmod +x /entrypoint.sh /setup-directory.sh
EXPOSE 636
ENTRYPOINT ["/entrypoint.sh"]
