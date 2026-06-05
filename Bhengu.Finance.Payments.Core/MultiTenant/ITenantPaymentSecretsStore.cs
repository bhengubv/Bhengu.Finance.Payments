// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.MultiTenant;

/// <summary>
/// Per-tenant credential lookup. Returns the options object that should be applied for the
/// given tenant + provider — e.g. PayFastOptions { MerchantId, MerchantKey, Passphrase, ... }
/// decrypted from the tenant's encrypted credential row.
///
/// <para>The SDK does not prescribe storage — implementations can use Postgres / SQL Server /
/// DynamoDB / Vault / Secret Manager / whatever the host already uses. The contract is just
/// "given a tenant id and an options type, return a populated instance, or null if this tenant
/// hasn't configured the provider."</para>
///
/// <para>Implementations are responsible for caching, decryption, and refresh — they're called
/// on every request that crosses a multi-tenant provider, so caching is important. The SDK's
/// in-process <see cref="Caching.IBhenguDistributedCache"/> is available for that purpose.</para>
/// </summary>
public interface ITenantPaymentSecretsStore
{
    /// <summary>
    /// Resolve options of type <typeparamref name="TOptions"/> for the given tenant. Returns
    /// null when the tenant has not enabled / configured the corresponding provider.
    /// </summary>
    /// <typeparam name="TOptions">The provider options type (e.g. <c>PayFastOptions</c>, <c>StripeOptions</c>).</typeparam>
    /// <param name="tenantId">Tenant identifier as returned by <see cref="IBhenguTenantContext.CurrentTenantId"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TOptions?> GetOptionsAsync<TOptions>(string tenantId, CancellationToken ct = default)
        where TOptions : class, new();
}
