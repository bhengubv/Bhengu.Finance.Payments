// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.MultiTenant;

/// <summary>
/// Ambient tenant context. Implementations resolve the current tenant from whatever signal the
/// host uses — claims principal, HTTP header, route segment, gRPC metadata, background-job context.
/// The SDK's multi-tenant wrapper reads <see cref="CurrentTenantId"/> to know which tenant's
/// credentials to apply when calling the underlying provider.
///
/// <para>Default registration in <c>AddBhenguMultiTenantPayments</c> is a no-op stub that throws
/// — consumers MUST register their own implementation. This is intentional: there's no safe
/// default for "which tenant is this request for" and silently failing open is the kind of bug
/// that causes cross-tenant credential leaks.</para>
/// </summary>
public interface IBhenguTenantContext
{
    /// <summary>
    /// The tenant identifier for the current request / unit of work. Throws if no tenant is in
    /// scope — multi-tenant code paths must NEVER silently fall back to a default.
    /// </summary>
    /// <exception cref="InvalidOperationException">No tenant is in scope.</exception>
    string CurrentTenantId { get; }

    /// <summary>True when a tenant is currently in scope. Use to gate optional behaviour.</summary>
    bool HasTenant { get; }
}
