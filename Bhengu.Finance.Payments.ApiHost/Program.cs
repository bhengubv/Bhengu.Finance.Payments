using Bhengu.Finance.Payments.PayFast.Services;
using Bhengu.Finance.Payments.Google.Services;
using Bhengu.Finance.Payments.ApplePay.Services;
using Bhengu.Finance.Payments.BricsPay.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;


var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddPayFastPayments()
    .AddGooglePayments()
    .AddApplePayPayments()
    .AddBricsPayPayments();

var app = builder.Build();
app.MapGet("/", () => "Bhengu.Finance.Payments API Host Running");
app.Run();