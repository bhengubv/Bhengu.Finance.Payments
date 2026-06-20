# PayFast API — Current Reference (source of truth)

> Verified against PayFast's **official PHP SDK** (`github.com/Payfast/payfast-php-sdk`, `master`) and
> `developers.payfast.co.za`, June 2026. The official SDK is authoritative and current; the docs website
> is a JavaScript app and does not fetch cleanly, so the SDK code is the canonical reference.
>
> **Why this file exists:** an earlier integration was built from a stale internal copy that predated
> PayFast's refund API, and wrongly assumed "PayFast has no refund API." It does. Re-base on this file,
> not on old internal code.

## Signing

Two distinct algorithms (PayFast `lib/Auth.php`):

- **REST API** (`generateApiSignature`): take all header + body values, add `passphrase` if set,
  **sort by key alphabetically** (`ksort`), join `key=urlencode(value)` with `&` (excluding `signature`),
  `md5(...)`. PHP `urlencode` encodes **space as `+`** ⇒ .NET `WebUtility.UrlEncode` (NOT `Uri.EscapeDataString`,
  which uses `%20`). Passphrase participates in the sort (it is NOT appended last).
- **Redirect / form** (`generateSignature`): fixed PayFast field order (not alphabetical), `urlencode(trim(value))`,
  `md5(...)`. Subscriptions require a passphrase.

## Hosts

- **REST API**: always `https://api.payfast.co.za`. Sandbox is `?testing=true` on the SAME host (not a different host).
- **Redirect/onsite**: `https://www.payfast.co.za/eng/process` (live) / `https://sandbox.payfast.co.za/eng/process` (sandbox).
- **ITN validate**: POST the raw ITN back to `{www|sandbox}/eng/query/validate`, expect `VALID`.

REST headers on every call: `merchant-id`, `version` (`v1`), `timestamp`, `signature`.
**Money values on the REST API are in CENTS (integer).** Redirect-form amounts are in rands (`F2`).

## Refunds  (`lib/Services/Refunds.php`)

- **Create**: `POST refunds/{pf_payment_id}` — body: `amount` (cents, int), `reason`, `notify_buyer` (default `1`), optional `acc_type`.
- **Fetch**: `GET refunds/{id}`.
- ⚠️ **NOT available in sandbox** — PayFast's SDK throws `"Refunds is not available in Sandbox mode"`.
  Refunds can therefore only ever be **production-verified**; there is no sandbox step.

## Subscriptions  (`lib/Services/Subscriptions.php`)

- **Fetch**:  `GET  subscriptions/{token}/fetch`
- **Pause**:  `PUT  subscriptions/{token}/pause`   — optional body `cycles` (int). Default 1 cycle.
- **Unpause**:`PUT  subscriptions/{token}/unpause`
- **Cancel**: `PUT  subscriptions/{token}/cancel`
- **Update**: `PATCH subscriptions/{token}/update` — body `cycles` (int), `frequency` (int), `run_date` (date), `amount` (cents).
- **Ad-hoc** (tokenisation charge): `POST subscriptions/{token}/adhoc` — body `amount` (cents), `item_name`, optional `cc_cvv`.

### Frequency codes (subscription `frequency`)

| Code | Interval |
|---|---|
| 1 | Daily |
| 2 | Weekly |
| 3 | Monthly |
| 4 | Quarterly |
| 5 | Biannually |
| 6 | Annual |

PayFast has **no fortnightly / bi-weekly** option — callers requesting it must be failed loudly, not silently re-mapped.

## Other current services (not yet wired in our SDK)

- `lib/Services/CreditCardTransactions.php` — card transactions.
- `lib/Services/TransactionHistory.php` — transaction history (our SDK is missing this).
- `lib/PaymentIntegrations/OnsiteIntegration.php` — onsite (in-page) checkout.

## Sources
- PayFast official PHP SDK: https://github.com/Payfast/payfast-php-sdk
- Developer docs: https://developers.payfast.co.za/
- Product: https://payfast.io/
