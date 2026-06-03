// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Vault;

/// <summary>
/// A request to vault a card for re-use against a vault customer.
/// </summary>
public sealed record TokeniseRequest
{
    /// <summary>The raw card details. Discarded immediately after the provider call returns.</summary>
    public required CardDetails Card { get; init; }

    /// <summary>
    /// The vault customer to attach the new payment method to. When null the provider will create
    /// a new vault customer and return its identifier on <see cref="PaymentMethod.CustomerId"/>.
    /// </summary>
    public string? CustomerId { get; init; }

    /// <summary>When true, the new method becomes the customer's default for future charges.</summary>
    public bool SetAsDefault { get; init; }

    /// <summary>Optional display label.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Caller-supplied idempotency key. Providers that support <see cref="ProviderCapabilities.Idempotency"/>
    /// dedupe retries so a network blip won't create two vault entries.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}
