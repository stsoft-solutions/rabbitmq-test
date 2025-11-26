using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PaymentService.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PaymentService.Worker;

public static class OutboxProcessor
{
    public static async Task<int> ProcessPendingAsync(PaymentsDbContext dbContext, IOutboxPublisher publisher, ILogger logger, CancellationToken cancellationToken)
    {
        var messages = await dbContext.OutboxMessages
            .Where(o => o.ProcessedUtc == null)
            .OrderBy(o => o.CreatedUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            return 0;
        }

        foreach (var message in messages)
        {
            try
            {
                publisher.Publish(message);
                message.ProcessedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                logger.LogError(ex, "Failed to publish outbox message {OutboxId}", message.OutboxId);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return messages.Count;
    }
}

public interface IOutboxPublisher
{
    void Publish(OutboxMessage message);
}

public sealed class OutboxPublisherWorker(ILogger<OutboxPublisherWorker> logger, IServiceProvider serviceProvider, IOutboxPublisher publisher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = await OutboxProcessor.ProcessPendingAsync(dbContext, publisher, logger, stoppingToken);
            if (processed == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}

public sealed class RabbitMqPublisher : IOutboxPublisher, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private bool _disposed;
    private readonly RabbitMqOptions _options;

    public RabbitMqPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqPublisher> logger)
    {
        _options = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.ConfirmSelect();
    }

    public void Publish(OutboxMessage message)
    {
        var properties = _channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.MessageId = Guid.NewGuid().ToString();
        properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        properties.CorrelationId = message.TraceId.ToString();

        var body = Encoding.UTF8.GetBytes(message.Payload);
        _channel.BasicPublish(exchange: _options.OutboundExchange, routingKey: message.RoutingKey, basicProperties: properties, body: body);
        _channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Dispose();
        _connection.Dispose();
    }
}

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "dev";
    public string Password { get; set; } = "devpass";
    public string OutboundExchange { get; set; } = "payments.topic";
    public string IntermediateQueue { get; set; } = "gateway.intermediate.q";
    public string FinalQueue { get; set; } = "gateway.final.q";
    public string InboundExchange { get; set; } = "gateway.topic";
}

public sealed class GatewayEventConsumerWorker(ILogger<GatewayEventConsumerWorker> logger, IServiceProvider serviceProvider, IOptions<RabbitMqOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var rabbitOptions = options.Value;

        var factory = new ConnectionFactory
        {
            HostName = rabbitOptions.HostName,
            Port = rabbitOptions.Port,
            UserName = rabbitOptions.UserName,
            Password = rabbitOptions.Password,
            DispatchConsumersAsync = true
        };

        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.BasicQos(0, 10, false);

        var intermediateConsumer = new AsyncEventingBasicConsumer(channel);
        intermediateConsumer.Received += async (_, args) =>
        {
            if (await TryProcessMessageAsync(args, dbContext, logger, isFinal: false, stoppingToken))
            {
                channel.BasicAck(args.DeliveryTag, false);
            }
            else
            {
                channel.BasicNack(args.DeliveryTag, false, false);
            }
        };

        channel.BasicConsume(rabbitOptions.IntermediateQueue, autoAck: false, consumer: intermediateConsumer);

        var finalConsumer = new AsyncEventingBasicConsumer(channel);
        finalConsumer.Received += async (_, args) =>
        {
            if (await TryProcessMessageAsync(args, dbContext, logger, isFinal: true, stoppingToken))
            {
                channel.BasicAck(args.DeliveryTag, false);
            }
            else
            {
                channel.BasicNack(args.DeliveryTag, false, false);
            }
        };

        channel.BasicConsume(rabbitOptions.FinalQueue, autoAck: false, consumer: finalConsumer);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private static async Task<bool> TryProcessMessageAsync(BasicDeliverEventArgs args, PaymentsDbContext dbContext, ILogger logger, bool isFinal, CancellationToken ct)
    {
        try
        {
            var payload = Encoding.UTF8.GetString(args.Body.ToArray());
            var message = JsonSerializer.Deserialize<GatewayStatusMessage>(payload);
            if (message is null)
            {
                logger.LogWarning("Unable to deserialize gateway message.");
                return true;
            }

            var alreadyProcessed = await dbContext.InboxMessages
                .AnyAsync(x => x.MessageId == message.MessageId, ct);

            if (alreadyProcessed)
            {
                logger.LogInformation("Duplicate gateway message {MessageId} ignored", message.MessageId);
                return true;
            }

            var payment = await dbContext.Payments.FirstOrDefaultAsync(p => p.PaymentId == message.PaymentId, ct);
            if (payment is null)
            {
                logger.LogWarning("Payment {PaymentId} not found for gateway message", message.PaymentId);
                return true;
            }

            var inbox = new InboxMessage
            {
                MessageId = message.MessageId,
                PaymentId = message.PaymentId,
                SourceExchange = args.Exchange,
                PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
                ProcessedUtc = DateTime.UtcNow
            };

            dbContext.InboxMessages.Add(inbox);

            var previousStatus = payment.Status;
            payment.Status = isFinal ? MapFinalStatus(message.Status) : PaymentStatus.Processing;
            payment.UpdatedUtc = DateTime.UtcNow;
            payment.Version++;

            dbContext.PaymentStatusHistory.Add(new PaymentStatusHistory
            {
                PaymentId = payment.PaymentId,
                PreviousStatus = previousStatus,
                NewStatus = payment.Status,
                ReasonCode = message.Reason,
                Payload = payload,
                OccurredUtc = DateTime.UtcNow,
                CorrelationId = message.CorrelationId
            });

            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing gateway message");
            return false;
        }
    }

    private static PaymentStatus MapFinalStatus(string status) => status switch
    {
        "Succeeded" => PaymentStatus.Succeeded,
        "Failed" => PaymentStatus.Failed,
        "Cancelled" => PaymentStatus.Failed,
        _ => PaymentStatus.Processing
    };
}

sealed record GatewayStatusMessage(Guid MessageId, Guid PaymentId, string GatewayCode, string Status, string? Reason, Guid CorrelationId);
