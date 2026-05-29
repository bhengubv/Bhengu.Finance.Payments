// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

// Minimal smoke-test host. DI extensions will be re-wired once the new
// IPaymentGatewayProvider implementations are in place (Phase C of the
// house-style migration). See Bhengu.Family/HOUSE_STYLE.md.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();
app.MapGet("/", () => "Bhengu.Finance.Payments API Host — smoke test only.");
app.Run();
