namespace Bhengu.Finance.Payments.PayShap.Configuration
{
    /// <summary>
    /// Configuration for the PayShap (BankservAfrica RTC) provider. Bound from the
    /// <c>PayShapSettings</c> root section in <c>IConfiguration</c>.
    /// </summary>
    public class PayShapSettings
    {
        /// <summary>Base URL of the upstream PayShap (Electrum) API. e.g. <c>https://api.payshap.co.za</c>.</summary>
        public string ApiBaseUrl { get; set; } = string.Empty;

        /// <summary>API key issued by the PayShap upstream.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>API secret issued by the PayShap upstream.</summary>
        public string ApiSecret { get; set; } = string.Empty;

        /// <summary>HMAC-SHA256 signing key used for inbound webhook signature verification.</summary>
        public string SignatureKey { get; set; } = string.Empty;

        /// <summary>Merchant identifier (BankservAfrica-assigned).</summary>
        public string MerchantId { get; set; } = string.Empty;

        /// <summary>
        /// Default payee details used by <see cref="Bhengu.Finance.Payments.PayShap.Providers.PayShapQrCodeProvider"/>
        /// when generating QR codes — the QR payload is a deep-link to the merchant's PayShap account, so
        /// the merchant's bank-code / account / display-name need to be known up-front. Optional: only
        /// required when the QR provider is used.
        /// </summary>
        public PayShapPayeeSettings? Payee { get; set; }
    }

    /// <summary>
    /// Default payee (merchant) identity rendered into PayShap QR payloads. The wallet scanning the QR
    /// uses these fields to address the credit transfer to the merchant.
    /// </summary>
    public sealed class PayShapPayeeSettings
    {
        /// <summary>BankservAfrica bank code of the merchant's receiving bank.</summary>
        public string BankCode { get; set; } = string.Empty;

        /// <summary>Merchant account number.</summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>Merchant display name shown on the payer's wallet during scan.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional proxy alias identifier type (<c>MSISDN</c>, <c>EMAIL</c>, <c>ID</c>, <c>BUSINESS</c>, <c>ACCOUNT</c>).</summary>
        public string? IdentifierType { get; set; }

        /// <summary>Optional proxy alias value paired with <see cref="IdentifierType"/> (e.g. the merchant's MSISDN).</summary>
        public string? IdentifierValue { get; set; }
    }
}
