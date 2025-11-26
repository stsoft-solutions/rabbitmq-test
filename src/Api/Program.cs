using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var paymentServiceBase = builder.Configuration.GetValue<string>(
    "PaymentService:BaseUrl") ?? "http://localhost:5081";

builder.Services.AddHttpClient("PaymentService", client => { client.BaseAddress = new Uri(paymentServiceBase); });

// OpenTelemetry Tracing (HTTP server + client)
builder.Services.AddOpenTelemetry()
    .WithTracing(tp => tp
        .ConfigureResource(r => r.AddService("Api", serviceVersion: "1.0.0"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/payments", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var payload = await ctx.Request.ReadFromJsonAsync<object>();
    if (payload is null) return Results.BadRequest("Invalid payload");

    var client = httpFactory.CreateClient("PaymentService");
    var resp = await client.PostAsJsonAsync("/payments", payload);
    var content = await resp.Content.ReadAsStringAsync();
    return Results.Content(content,
        resp.Content.Headers.ContentType?.ToString() ?? "application/json",
        statusCode: (int)resp.StatusCode);
});

app.Run();