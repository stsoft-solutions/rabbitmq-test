using Microsoft.EntityFrameworkCore;
using PaymentService.Persistence;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Payments")));

builder.Services.AddScoped<IPaymentService, PaymentCommandService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapPost("/payments", async (CreatePaymentRequest request, IPaymentService service, CancellationToken ct) =>
{
    var result = await service.CreatePaymentAsync(request, ct);
    return Results.Accepted($"/payments/{result.PaymentId}", result);
});

app.MapGet("/payments/{paymentId:guid}", async (Guid paymentId, PaymentsDbContext db, CancellationToken ct) =>
{
    var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.PaymentId == paymentId, ct);
    return payment is null ? Results.NotFound() : Results.Ok(payment);
});

app.Run();

record CreatePaymentRequest(Guid ClientRequestId, long AmountMinor, string CurrencyCode, Guid CustomerId, string GatewayCode);
record PaymentResponse(Guid PaymentId, string Status);

interface IPaymentService
{
    Task<PaymentResponse> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);
}

sealed class PaymentCommandService(PaymentsDbContext dbContext) : IPaymentService
{
    public async Task<PaymentResponse> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            ClientRequestId = request.ClientRequestId,
            AmountMinor = request.AmountMinor,
            CurrencyCode = request.CurrencyCode,
            CustomerId = request.CustomerId,
            GatewayCode = request.GatewayCode,
            Status = PaymentStatus.Pending,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Version = 1
        };

        var outbox = new OutboxMessage
        {
            PaymentId = payment.PaymentId,
            EventType = "PaymentCreated",
            Payload = JsonSerializer.Serialize(payment),
            RoutingKey = $"payment.created.{payment.GatewayCode}",
            CreatedUtc = payment.CreatedUtc,
            TraceId = Guid.NewGuid()
        };

        dbContext.Payments.Add(payment);
        dbContext.OutboxMessages.Add(outbox);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new PaymentResponse(payment.PaymentId, payment.Status.ToString());
    }
}
