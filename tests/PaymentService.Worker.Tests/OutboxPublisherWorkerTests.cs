using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Persistence;
using PaymentService.Worker;
using Xunit;

public class OutboxPublisherWorkerTests
{
    [Fact]
    public async Task MarksOutboxProcessed_WhenPublishSucceeds()
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentsDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<IOutboxPublisher, NoOpPublisher>();

        await using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<PaymentsDbContext>();

        var payment = new Payment
        {
            PaymentId = Guid.NewGuid(),
            ClientRequestId = Guid.NewGuid(),
            AmountMinor = 100,
            CurrencyCode = "USD",
            CustomerId = Guid.NewGuid(),
            GatewayCode = "mock",
            Status = PaymentStatus.Pending,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            Version = 1
        };
        db.Payments.Add(payment);
        db.OutboxMessages.Add(new OutboxMessage
        {
            PaymentId = payment.PaymentId,
            EventType = "PaymentCreated",
            Payload = "{}",
            RoutingKey = "payment.created.mock",
            CreatedUtc = DateTime.UtcNow,
            TraceId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        var processed = await OutboxProcessor.ProcessPendingAsync(db, provider.GetRequiredService<IOutboxPublisher>(), NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, processed);
        var outbox = await db.OutboxMessages.FirstAsync();
        Assert.NotNull(outbox.ProcessedUtc);
    }

    private sealed class NoOpPublisher : IOutboxPublisher
    {
        public void Publish(OutboxMessage message)
        {
            // intentionally left blank
        }
    }
}
