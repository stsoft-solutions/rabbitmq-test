using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Linq;
using Dapper;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var connString = builder.Configuration.GetConnectionString("Payments")
                 ?? "Host=localhost;Port=5432;Database=payments;Username=app;Password=app;Pooling=true";

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connString));

// OpenTelemetry (HTTP server + client)
builder.Services.AddOpenTelemetry()
    .WithTracing(tp => tp
        .ConfigureResource(r => r.AddService("PaymentService.Api", serviceVersion: "1.0.0"))
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/payments/{id:guid}", async (Guid id, NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    var payment = await conn.QuerySingleOrDefaultAsync(@"select payment_id, amount, currency, customer_id, status, gateway_provider,
        external_reference, created_at, updated_at, version from payments where payment_id = @id", new { id });
    return payment is null ? Results.NotFound() : Results.Ok(payment);
});

app.MapPost("/payments", async (PaymentRequest req, NpgsqlDataSource ds) =>
{
    if (req.Amount <= 0) return Results.BadRequest("Amount must be positive");
    if (string.IsNullOrWhiteSpace(req.Currency) || req.Currency.Length != 3) return Results.BadRequest("Currency must be 3 letters");

    var paymentId = Guid.NewGuid();
    var provider = string.IsNullOrWhiteSpace(req.Provider) ? "simulated" : req.Provider!;
    var outboxMessageId = Guid.NewGuid();

    // Capture W3C context from current Activity
    var traceparent = Activity.Current?.Id;
    var tracestate = Activity.Current?.TraceStateString;
    var baggagePairs = Activity.Current?.Baggage?.Select(kv => $"{kv.Key}={kv.Value}");
    var baggage = baggagePairs is null ? null : string.Join(", ", baggagePairs);

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    // Insert payment
    await conn.ExecuteAsync(@"insert into payments (
        payment_id, amount, currency, customer_id, status, gateway_provider)
        values (@id, @amount, @currency, @customer, 'Created', @provider)",
        new { id = paymentId, amount = req.Amount, currency = req.Currency.ToUpperInvariant(), customer = req.CustomerId, provider }, tx);

    // Insert outbox event PaymentCreated
    var payload = JsonSerializer.Serialize(new
    {
        payment_id = paymentId,
        amount = req.Amount,
        currency = req.Currency.ToUpperInvariant(),
        customer_id = req.CustomerId,
        gateway_provider = provider,
        idempotency_key = req.IdempotencyKey
    });

    await conn.ExecuteAsync(@"insert into outbox (aggregate_type, aggregate_id, type, payload, message_id, traceparent, tracestate, baggage)
        values ('Payment', @id, 'PaymentCreated', CAST(@payload as jsonb), @mid, @traceparent, @tracestate, @baggage)",
        new { id = paymentId, payload, mid = outboxMessageId, traceparent, tracestate, baggage }, tx);

    await tx.CommitAsync();

    return Results.Created($"/payments/{paymentId}", new { payment_id = paymentId });
});

app.Run();

record PaymentRequest(decimal Amount, string Currency, Guid CustomerId, string? Provider, string? IdempotencyKey);
