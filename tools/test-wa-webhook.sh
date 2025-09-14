#!/usr/bin/env bash
set -euo pipefail

# ==== Config =====
APP_SECRET="${APP_SECRET:-}"                       # << OBRIGATÓRIO: export APP_SECRET=seu_secret
URL="${URL:-http://localhost:7137/wa/webhook}"
PHONE_NUMBER_ID="${PHONE_NUMBER_ID:-831371026718601}"
DISPLAY_PHONE_NUMBER="${DISPLAY_PHONE_NUMBER:-15551379162}"
ENTRY_ID_WABA="${ENTRY_ID_WABA:-1799258950683640}"
FROM_WA="${FROM_WA:-5534999999999}"
MESSAGE_ID="${MESSAGE_ID:-wamid.test.123}"
PAYLOAD_FILE="${PAYLOAD_FILE:-tests/payloads/wa-inbound-text.json}"

# ==== Checks =====
if [[ -z "$APP_SECRET" ]]; then
  echo "ERRO: defina APP_SECRET. Ex: export APP_SECRET='xxxxxxxx'"; exit 2
fi

# ==== Ensure folder =====
mkdir -p "$(dirname "$PAYLOAD_FILE")"

# ==== Build payload (raw) ====
cat > "$PAYLOAD_FILE" <<'JSON'
{
  "object": "whatsapp_business_account",
  "entry": [
    {
      "id": "__ENTRY_ID_WABA__",
      "changes": [
        {
          "value": {
            "messaging_product": "whatsapp",
            "metadata": {
              "display_phone_number": "__DISPLAY_PHONE_NUMBER__",
              "phone_number_id": "__PHONE_NUMBER_ID__"
            },
            "contacts": [
              {
                "profile": { "name": "Cliente Teste" },
                "wa_id": "__FROM_WA__"
              }
            ],
            "messages": [
              {
                "from": "__FROM_WA__",
                "id": "__MESSAGE_ID__",
                "timestamp": "1700000000",
                "text": { "body": "Olá, estou testando" },
                "type": "text"
              }
            ]
          },
          "field": "messages"
        }
      ]
    }
  ]
}
JSON

# Replace placeholders
sed -i.bak \
  -e "s#__PHONE_NUMBER_ID__#${PHONE_NUMBER_ID}#g" \
  -e "s#__DISPLAY_PHONE_NUMBER__#${DISPLAY_PHONE_NUMBER}#g" \
  -e "s#__ENTRY_ID_WABA__#${ENTRY_ID_WABA}#g" \
  -e "s#__FROM_WA__#${FROM_WA}#g" \
  -e "s#__MESSAGE_ID__#${MESSAGE_ID}#g" \
  "$PAYLOAD_FILE"
rm -f "${PAYLOAD_FILE}.bak"

# ==== Compute signature (sha256=hex(HMACSHA256(secret, rawBody))) ====
RAW_BODY="$(cat "$PAYLOAD_FILE")"
HEX_SIG="$(printf "%s" "$RAW_BODY" | openssl dgst -sha256 -hmac "$APP_SECRET" -binary | xxd -p -c 256)"
HEADER_SIG="sha256=${HEX_SIG}"

# ==== POST ====
echo ">> Enviando para $URL"
HTTP_CODE=$(curl -s -o /tmp/wa_resp_body.txt -w "%{http_code}" \
  -X POST "$URL" \
  -H "Content-Type: application/json" \
  -H "X-Hub-Signature-256: ${HEADER_SIG}" \
  --data-binary @"$PAYLOAD_FILE")

BYTES=$(wc -c < "$PAYLOAD_FILE" | tr -d ' ')
echo "Status: $HTTP_CODE  | Body enviado: ${BYTES} bytes"
echo "MessageId: $MESSAGE_ID | PhoneNumberId: $PHONE_NUMBER_ID"

if [[ "$HTTP_CODE" != "200" ]]; then
  echo "ERRO: webhook respondeu $HTTP_CODE"
  echo "--- Resposta ---"
  cat /tmp/wa_resp_body.txt || true
  exit 1
fi

echo "OK ✅  Webhook respondeu 200. Verifique logs e persistência."

