"""Generate disposable PostgreSQL TLS fixtures for PG-09/10; requires cryptography."""

import argparse
import json
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import cryptography
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--hostname", required=True)
    parser.add_argument("--crl-port", type=int, default=55412)
    parser.add_argument("--unavailable-port", type=int, default=55413)
    args = parser.parse_args()
    if not all(1024 <= port <= 65535 for port in (args.crl_port, args.unavailable_port)):
        parser.error("Ports must be between 1024 and 65535.")
    if args.crl_port == args.unavailable_port:
        parser.error("Reachable and unavailable ports must differ.")
    hostname = args.hostname.encode("idna").decode("ascii")
    if not hostname or any(char in hostname for char in "/\\:* \r\n"):
        parser.error("Use the guest's DNS hostname, without a scheme, port or wildcard.")
    out = args.output.resolve()
    out.mkdir(exist_ok=False)  # Parent must already have the lab's restricted ACL.
    now = datetime.now(timezone.utc)
    run_id = uuid.uuid4().hex
    ca_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "NodePilot UI CRL " + run_id)])
    ca = (
        x509.CertificateBuilder()
        .subject_name(ca_name).issuer_name(ca_name).public_key(ca_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(days=1)).not_valid_after(now + timedelta(days=7))
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, None, None), critical=True)
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(ca_key.public_key()), critical=False)
        .sign(ca_key, hashes.SHA256())
    )
    (out / "root.crt").write_bytes(ca.public_bytes(serialization.Encoding.PEM))
    certificates = {}
    manifest = {"runId": run_id, "createdUtc": now.isoformat(), "hostname": hostname,
                "cryptographyVersion": cryptography.__version__,
                "caSubject": ca.subject.rfc4514_string(), "certificates": {}}
    for mode in ("unavailable", "revoked", "valid"):
        key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        port = args.unavailable_port if mode == "unavailable" else args.crl_port
        path = "unavailable.crl" if mode == "unavailable" else "issuer.crl"
        cdp = f"http://127.0.0.1:{port}/{path}?run={run_id}"
        certificate = (
            x509.CertificateBuilder()
            .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, hostname)]))
            .issuer_name(ca.subject).public_key(key.public_key())
            .serial_number(x509.random_serial_number())
            .not_valid_before(now - timedelta(days=1)).not_valid_after(now + timedelta(days=3))
            .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
            .add_extension(x509.SubjectAlternativeName([x509.DNSName(hostname)]), critical=False)
            .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
            .add_extension(x509.KeyUsage(True, False, True, False, False, False, False, None, None), critical=True)
            .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()), critical=False)
            .add_extension(x509.CRLDistributionPoints([x509.DistributionPoint(
                full_name=[x509.UniformResourceIdentifier(cdp)], relative_name=None,
                reasons=None, crl_issuer=None)]), critical=False)
            .sign(ca_key, hashes.SHA256())
        )
        certificates[mode] = certificate
        (out / f"{mode}.crt").write_bytes(certificate.public_bytes(serialization.Encoding.PEM))
        (out / f"{mode}.key").write_bytes(key.private_bytes(
            serialization.Encoding.PEM, serialization.PrivateFormat.TraditionalOpenSSL,
            serialization.NoEncryption()))
        manifest["certificates"][mode] = {"serialHex": format(certificate.serial_number, "x"), "cdp": cdp}
    revoked = (x509.RevokedCertificateBuilder()
               .serial_number(certificates["revoked"].serial_number)
               .revocation_date(now - timedelta(hours=1)).build())
    crl = (
        x509.CertificateRevocationListBuilder().issuer_name(ca.subject)
        .last_update(now - timedelta(minutes=10)).next_update(now + timedelta(days=2))
        .add_revoked_certificate(revoked)
        .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(ca_key.public_key()), critical=False)
        .add_extension(x509.CRLNumber(1), critical=False).sign(ca_key, hashes.SHA256())
    )
    (out / "issuer.crl").write_bytes(crl.public_bytes(serialization.Encoding.DER))
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Created disposable CRL fixtures: {out}")


if __name__ == "__main__":
    main()
