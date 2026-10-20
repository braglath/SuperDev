#!/usr/bin/env bash
# Create a self-signed code-signing certificate for the Super Dev MSIX.
# Windows will not treat this as a verified publisher. Import the .cer into
# Local Machine → Trusted People on each PC that should install the package.
# Store the .pfx as GitHub secret WINDOWS_PFX_BASE64 and the password as
# WINDOWS_PFX_PASSWORD. Do not commit the .pfx or the password.

set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/.certs}"
mkdir -p "$out"

password="$(openssl rand -base64 24 | tr -d '/+=' | cut -c1-24)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cat > "$work/openssl.cnf" <<'EOF'
[req]
distinguished_name = dn
x509_extensions = codesign
prompt = no

[dn]
CN = Super Dev

[codesign]
keyUsage = digitalSignature
extendedKeyUsage = codeSigning
basicConstraints = CA:FALSE
subjectKeyIdentifier = hash
EOF

openssl req -x509 -newkey rsa:2048 \
  -keyout "$work/key.pem" \
  -out "$work/cert.pem" \
  -days 1095 \
  -nodes \
  -config "$work/openssl.cnf"

# Windows signtool reads the older PKCS#12 encryption. OpenSSL 3+ defaults to AES.
if ! openssl pkcs12 -export -legacy \
  -inkey "$work/key.pem" \
  -in "$work/cert.pem" \
  -out "$out/superdev.pfx" \
  -passout "pass:$password" \
  -name "Super Dev"; then
  openssl pkcs12 -export \
    -certpbe PBE-SHA1-3DES \
    -keypbe PBE-SHA1-3DES \
    -macalg SHA1 \
    -inkey "$work/key.pem" \
    -in "$work/cert.pem" \
    -out "$out/superdev.pfx" \
    -passout "pass:$password" \
    -name "Super Dev"
fi

openssl x509 -in "$work/cert.pem" -outform DER -out "$out/superdev.cer"
printf '%s\n' "$password" > "$out/password.txt"
chmod 600 "$out/superdev.pfx" "$out/password.txt"

echo "Wrote $out/superdev.pfx"
echo "Wrote $out/superdev.cer"
echo "Wrote $out/password.txt"
echo "Subject: CN=Super Dev"
echo "Import superdev.cer into Local Machine → Trusted People before installing the MSIX."
