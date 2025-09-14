param(
  [Parameter(Mandatory = $true)] [string] $AppSecret,
  [string] $Url = "http://localhost:7137/wa/webhook",
  [string] $PhoneNumberId = "831371026718601",
  [string] $DisplayPhoneNumber = "15551379162",
  [string] $EntryIdWaba = "1799258950683640",
  [string] $FromWa = "5534999999999",
  [string] $MessageId = "wamid.test.123",
  [string] $PayloadFile = "tests/payloads/wa-inbound-text.json"
)

$ErrorActionPreference = "Stop"

# Ensure folder
$dir = Split-Path $PayloadFile -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

# Build payload with placeholders
$payload = @"
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
"@

$payload = $payload.Replace("__PHONE_NUMBER_ID__", $PhoneNumberId).
  Replace("__DISPLAY_PHONE_NUMBER__", $DisplayPhoneNumber).
  Replace("__ENTRY_ID_WABA__", $EntryIdWaba).
  Replace("__FROM_WA__", $FromWa).
  Replace("__MESSAGE_ID__", $MessageId)

# Save payload
[IO.File]::WriteAllText($PayloadFile, $payload, [Text.Encoding]::UTF8)

# Compute HMAC SHA256 hex (sha256=<hex>)
$bytes = [Text.Encoding]::UTF8.GetBytes($payload)
$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = [Text.Encoding]::UTF8.GetBytes($AppSecret)
$hashBytes = $hmac.ComputeHash($bytes)
$hex = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })
$headerSig = "sha256=$hex"

# POST
try {
  $response = Invoke-WebRequest -Method Post -Uri $Url -Headers @{
    "Content-Type" = "application/json"
    "X-Hub-Signature-256" = $headerSig
  } -Body $payload -UseBasicParsing -TimeoutSec 15
  $code = $response.StatusCode.value__
} catch {
  if ($_.Exception.Response) {
    $code = $_.Exception.Response.StatusCode.value__
    $bodyReader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
    $errBody = $bodyReader.ReadToEnd()
    Write-Host "Resposta de erro: $errBody"
  } else {
    throw
  }
}

$bytesLen = (Get-Item $PayloadFile).Length
Write-Host ("Status: {0}  | Body enviado: {1} bytes" -f $code, $bytesLen)
Write-Host ("MessageId: {0} | PhoneNumberId: {1}" -f $MessageId, $PhoneNumberId)

if ($code -ne 200) {
  Write-Error "Webhook respondeu $code" ; exit 1
} else {
  Write-Host "OK ✅  Webhook respondeu 200. Verifique logs e persistência."
}

