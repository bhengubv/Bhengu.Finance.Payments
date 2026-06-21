// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core;

/// <summary>
/// Canonical provider-name constants — use these instead of bare strings when looking up
/// providers from DI (eg. <c>[FromKeyedServices(ProviderNames.PayFast)]</c>) or filtering an
/// <see cref="System.Collections.Generic.IEnumerable{T}"/> of <see cref="Interfaces.IPaymentGatewayProvider"/>.
/// Eliminates typo-risk that bare string comparison invites.
/// </summary>
public static class ProviderNames
{
    // South Africa
    public const string PayFast = "payfast";
    public const string Yoco = "yoco";
    public const string Ozow = "ozow";
    public const string PayJustNow = "payjustnow";
    public const string PayShap = "payshap";
    public const string Mukuru = "mukuru";
    public const string Stitch = "stitch";

    // Mobile money
    public const string MPesa = "mpesa";
    public const string MTNMoMo = "mtnmomo";
    public const string AirtelMoney = "airtelmoney";
    public const string OrangeMoney = "orangemoney";
    public const string Wave = "wave";
    public const string EcoCash = "ecocash";

    // Pan-African aggregators
    public const string Flutterwave = "flutterwave";
    public const string Cellulant = "cellulant";
    public const string DPO = "dpo";
    public const string Onafriq = "onafriq";

    // Nigeria
    public const string Paystack = "paystack";
    public const string Interswitch = "interswitch";
    public const string OPay = "opay";
    public const string Moniepoint = "moniepoint";
    public const string Remita = "remita";

    // Ghana
    public const string Hubtel = "hubtel";
    public const string ExpressPay = "expresspay";
    public const string Slydepay = "slydepay";

    // Kenya
    public const string Pesapal = "pesapal";
    public const string IPay = "ipay";
    public const string JamboPay = "jambopay";

    // Egypt
    public const string Fawry = "fawry";
    public const string Paymob = "paymob";
    public const string Kashier = "kashier";

    // Morocco
    public const string CMI = "cmi";

    // Pan-African transfer
    public const string ChipperCash = "chippercash";

    // BRICS — Brazil
    public const string MercadoPago = "mercadopago";
    public const string PagSeguro = "pagseguro";

    // BRICS — India
    public const string Razorpay = "razorpay";
    public const string PayUIndia = "payuindia";
    public const string Paytm = "paytm";

    // BRICS — China
    public const string Alipay = "alipay";
    public const string WeChatPay = "wechatpay";
    public const string UnionPay = "unionpay";

    // Cross-border BRICS rail
    public const string BricsPay = "bricspay";

    // Global card networks / wallets
    public const string Stripe = "stripe";
    public const string ApplePay = "applepay";
    public const string GooglePay = "googlepay";
}
