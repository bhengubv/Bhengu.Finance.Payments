# BRICS Pay E-Commerce API — Current Reference (source of truth)

> Verified 2026-06-21 against BRICS Pay's **official E-Commerce protocol description**
> (`outline.ilink.dev/s/689d8f67-d0fb-41ca-9895-677ecdc7171d`, linked from the "For developers"
> section of `brics-pay.com/BRICS-Pay-Resources`). The page is bot-blocked to plain HTTP fetchers
> (403) and only renders in a real browser — this file is the captured substance.
>
> **Why this file exists:** the original `Bhengu.Finance.Payments.BricsPay` was built from imagination —
> a fake `api.bricspay.org` domain, HMAC-SHA256 shared-secret signing, a card-token charge model, and
> a frankfurter-FX "cross-border conversion." **None of that matches the real API.** Re-base the rebuild
> on this file, not on the old code.

## What the real API is

BRICS Pay e-commerce acceptance is **QR-code acquiring** ("Интернет Эквайринг" / Internet Acquiring),
running on the **Joys** processing platform (IP owned by ООО «Цифровые платежи»; the name *Joys* appears
in their docs and endpoints). The customer pays by scanning a QR code in the BRICS Pay app — there is **no
card token**. The merchant ("ТСП" / TSP) creates a transaction, shows the returned payment page + QR, and
learns the result by **callback** or by **polling** a status endpoint.

This is a **redirect/QR provider shape**, NOT the card-charge `IPaymentGatewayProvider` contract.

## Onboarding (required before any call works)

1. Sign a contract to join the BRICS Pay QR service.
2. Register as a merchant.
3. Receive a **Pos (payment-terminal) ID**, linked to your merchant ID.
4. Configure the terminal:
   - **URL Shop** — return link to your store after payment
   - **URL CSS** — stylesheet for the payment page (optional; default styling otherwise)
   - **URL Callback** — where transaction-status POSTs are delivered
   - **TTL** — payment-form lifetime in minutes (default 5)
   - **Public key** + chosen signature algorithm
- The API **base URL is provisioned per terminal** — there is no fixed public host. There's an HTTP
  **test server**; production is HTTPS. No self-serve sandbox keys.

## Signing — asymmetric, NOT HMAC

You generate a keypair; the **public key** is registered at onboarding, the **private key** stays with you.
Every request's payload is hashed, signed with the private key, base64-encoded, and passed as a `signature`
**URL query parameter**. The processor verifies with your registered public key. A bad/missing signature → `401`.

| Algorithm | Parameters | Digest |
|---|---|---|
| ECDSA | curve prime256v1 or prime224v1 | SHA-256 |
| RSA | key length 1024 or 2048 | SHA-256 |
| GOST 34.10 | 256-bit, curve 2001-CryptoProA | GOST 34.11-2012 256 |
| GOST 34.10 | 256-bit, curve 2012-TC26A | GOST 34.11-2012 256 |
| GOST 34.10 | 512-bit, curve 2012-TC26A | GOST 34.11-2012 512 |

Default for a .NET build: **ECDSA P-256 (prime256v1) + SHA-256** (`System.Security.Cryptography.ECDsa`),
with RSA-2048 as an option. GOST is not natively supported by .NET and is out of scope unless required.

- For `POST /ia/api` and `POST /ia/refund`: the **body is the JSON object**; sign that JSON, put the base64
  signature in the URL (`...?signature=...`).
- For `GET /ia/get`: sign the URL parameters (`pos`, `sequence`); append `signature`.

## Endpoints (relative to the terminal's provisioned base URL)

| Path | Method | Purpose |
|---|---|---|
| `/ia/api/` | POST | Create transaction + return payment page (QR) |
| `/ia/get/` | GET | Get receipt/transaction status |
| `/ia/refund` | POST | Full or partial refund |

Header: `Accept-Language` (IETF BCP 47).

### Create transaction — `POST /ia/api/?signature=...`

Request body (PascalCase JSON, RFC 8259):

| Field | Req | Type | Notes |
|---|---|---|---|
| `Pos` | ✓ | string | Terminal ID |
| `Sequence` | ✓ | string | Unique per-terminal operation number |
| `Amount` | ✓ | string | Float-as-string |
| `User` | ✓ | string | **SHA256(IP + User-Agent)** |
| `Callback` | – | string | Per-call override of URL Callback |
| `CSS` | – | string | Per-call CSS URL |
| `Return` | – | string | Per-call return-to-store URL |
| `TTL` | – | uint8 | Per-call form lifetime (minutes) |
| `Receipt` | – | object | `{ Number (✓), Discount, Items[] }` |
| `Receipt.Items[]` | ✓ | object | `{ ID, Group, Title (✓), Quantity, Measure, Discount, Price (✓) }` |

Response: `{ "URL": "<payment page URL with QR>" }`. Showing the QR starts the callback mechanism.

### Get status — `GET /ia/get/?pos=...&sequence=...&signature=...`

Response JSON:

| Field | Type | Notes |
|---|---|---|
| `Transaction` | string | Present once `Processed = true` |
| `Paid` | bool | true = paid |
| `Processed` | bool | true = processing finished |
| `Amount` | string | In the terminal's currency |
| `Currency` | object | `{ Code (uint16 ISO numeric), Precision (uint8), Name, Symbol }` |
| `Time` | object | `{ Created (UTC), Processed (UTC, if processed), Timeout (UTC) }` |
| `Reference` | string | Parent transaction (for refunds) |
| `Error` | object | `{ Code, Message }` — present when `Paid=false && Processed=true` |

### Refund — `POST /ia/refund?signature=...`

Body: `POS`, `Sequence` (the refund's **own** new operation number — not the original's), `Reference`
(the original payment's `Transaction` number), `Amount` (≤ original). Response = same shape as Get status.

### Callback (processing → your URL Callback, POST)

Sent when a transaction reaches a final state (Processed=true, Paid true or false). Body carries `POS`,
`Sequence`, `Transaction`, `Paid`, `Processed`, `Amount`, `Currency{}`, `Time{}`, `Reference`, `Error{}` —
the same fields as Get status. Verify it against the processor's signature before trusting it.

## Protocol error codes (response has only an `Error{Number, Description}`, no `Reply`, no signature)

| Number | Meaning | Action |
|---|---|---|
| 400 | Syntax error | Check params/types/structure |
| 401 | Signature check failed | Check public key / terminal no. / signature; re-activate |
| 404 | Object not found | Check the data sent |
| 408 | Request timeout | Retry with same params |
| 409 | Object already exists | Check the data sent |
| 423 | Terminal blocked | Contact support |
| 500 | Internal server error | Contact support |

Request-logic errors are nested under a `Reply` structure and documented per-request.

## Status

This is **DocsOnly** — correctly implemented against the published protocol, never live-verified (no
terminal onboarded). Same honest ceiling as PayFast. The rebuild replaces the old fiction entirely.

## Sources
- E-Commerce protocol: `outline.ilink.dev/s/689d8f67-d0fb-41ca-9895-677ecdc7171d`
- POS (brick & mortar) protocol: `outline.ilink.dev/s/05717c4a-9396-4d53-b13d-d6898f7f5b5c`
- Swagger (rendered blank for us — likely auth-gated): `app.brics-pay.com/swagger/`
- Swagger instructions: `outline.ilink.dev/s/21e95491-cc24-42cc-856e-7c2e80c237a9`
- Resources hub: `brics-pay.com/BRICS-Pay-Resources`
