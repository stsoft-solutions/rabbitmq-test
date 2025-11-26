using System.Collections.Concurrent;
using System.Net.Http.Json;
using Polly;
using Polly.Extensions.Http;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddHttpClient<PaymentServiceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Services:PaymentService:BaseUrl"]!);
})
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(100 * attempt)));

builder.Services.AddSingleton<IdempotencyStore>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/payments", async (HttpRequest httpRequest, PaymentServiceClient client, IdempotencyStore store, CancellationToken ct) =>
{
    if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest("Missing Idempotency-Key header");
    }

    if (store.TryGet(key!, out var cached))
    {
        return Results.Accepted($"/payments/{cached.PaymentId}", cached);
    }

    var payload = await httpRequest.ReadFromJsonAsync<CreatePaymentRequest>(cancellationToken: ct);
    if (payload is null)
    {
        return Results.BadRequest("Invalid payload");
    }

    var response = await client.CreatePaymentAsync(payload, ct);
    store.Set(key!, response);
    return Results.Accepted($"/payments/{response.PaymentId}", response);
});

app.Run();

record CreatePaymentRequest(Guid ClientRequestId, long AmountMinor, string CurrencyCode, Guid CustomerId, string GatewayCode);
record PaymentResponse(Guid PaymentId, string Status);

sealed class PaymentServiceClient(HttpClient httpClient)
{
    public async Task<PaymentResponse> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken ct)
    {
        using var response = await httpClient.PostAsJsonAsync("/payments", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PaymentResponse>(cancellationToken: ct))!;
    }
}

sealed class IdempotencyStore
{
    private readonly ConcurrentDictionary<string, PaymentResponse> _cache = new();

    public bool TryGet(string key, out PaymentResponse response) => _cache.TryGetValue(key, out response!);

    public void Set(string key, PaymentResponse response) => _cache[key] = response;
}
