#!/usr/bin/env bash
# Create a STABLE self-signed code-signing certificate in the login keychain.
# hearken signs its macOS app with this so the microphone (TCC) permission grant
# survives rebuilds — ad-hoc signing (Wails default) changes the cdhash every
# build and silently revokes the mic grant.
#
# Run once per Mac. Idempotent: re-running replaces the cert (you'd re-grant mic).
# The cert is NOT Apple-notarized — it only solves TCC persistence on this machine,
# not Gatekeeper for distribution to other Macs (that needs a Developer ID).
set -euo pipefail
NAME="${HEARKEN_SIGN_ID:-hearken-selfsign}"
KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

cat > "$TMP/csr.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = v3
prompt = no
[dn]
CN = $NAME
[v3]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
EOF

security delete-certificate -c "$NAME" "$KEYCHAIN" 2>/dev/null || true
# Use macOS LibreSSL for the p12 so `security` can import it (OpenSSL 3.x needs -legacy).
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes \
  -keyout "$TMP/k.key" -out "$TMP/k.crt" -days 7300 \
  -config "$TMP/csr.cnf" -extensions v3
/usr/bin/openssl pkcs12 -export -out "$TMP/k.p12" \
  -inkey "$TMP/k.key" -in "$TMP/k.crt" -passout pass:hearken -name "$NAME"
# -A lets codesign use the key without a GUI keychain prompt.
security import "$TMP/k.p12" -k "$KEYCHAIN" -P hearken -T /usr/bin/codesign -A

echo "Created code-signing identity '$NAME'. (find-identity -v hides it as untrusted;"
echo "that's fine — codesign still produces a cert-anchored requirement for stable TCC.)"
security find-identity -p codesigning | grep -i "$NAME" || true
