// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Vault;

/// <summary>
/// What kind of payment method a vaulted token represents.
/// </summary>
public enum PaymentMethodKind
{
    /// <summary>The token represents a credit / debit / prepaid card.</summary>
    Card,

    /// <summary>The token represents a bank account (EFT / ACH / SEPA / BACS).</summary>
    BankAccount,

    /// <summary>The token represents a mobile-money wallet (M-PESA, MTN MoMo, etc.).</summary>
    MobileMoney,

    /// <summary>The token represents a digital wallet (Apple Pay, Google Pay, Alipay, WeChat Pay).</summary>
    Wallet,

    /// <summary>The token represents an authorised debit-order mandate.</summary>
    Mandate,

    /// <summary>Anything else.</summary>
    Other
}
