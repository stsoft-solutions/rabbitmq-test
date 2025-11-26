using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddSingleton<RabbitMqGatewayClient>();
builder.Services.AddHostedService<PaymentGatewayWorker>();

var host = builder.Build();
host.Run();

sealed class PaymentGatewayWorker(ILogger<PaymentGatewayWorker> logger, RabbitMqGatewayClient client) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return client.StartConsuming(async envelope =>
        {
            logger.LogInformation("Dispatch received for payment {PaymentId}", envelope.PaymentId);
            await client.PublishIntermediateAsync(envelope, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            await client.PublishFinalAsync(envelope, stoppingToken);
        }, stoppingToken);
    }
}

sealed class RabbitMqGatewayClient : IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly GatewayOptions _options;
    private readonly ILogger<RabbitMqGatewayClient> _logger;
    private bool _disposed;

    public RabbitMqGatewayClient(IOptions<GatewayOptions> options, ILogger<RabbitMqGatewayClient> logger)
    {
        _options = options.Value;
        _logger = logger;
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
        _channel.BasicQos(0, _options.PrefetchCount, false);
    }

    public Task StartConsuming(Func<GatewayDispatchEnvelope, Task> handler, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() =>
        {
            Dispose();
            tcs.TrySetResult();
        });

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, args) =>
        {
            try
            {
                var payload = Encoding.UTF8.GetString(args.Body.ToArray());
                var envelope = JsonSerializer.Deserialize<GatewayDispatchEnvelope>(payload);
                if (envelope is null)
                {
                    _logger.LogWarning("Invalid dispatch payload discarded");
                    _channel.BasicAck(args.DeliveryTag, false);
                    return;
                }

                await handler(envelope);
                _channel.BasicAck(args.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process dispatch message");
                _channel.BasicNack(args.DeliveryTag, false, false);
            }
        };

        _channel.BasicConsume(queue: _options.DispatchQueue, autoAck: false, consumer: consumer);
        return tcs.Task;
    }

    public Task PublishIntermediateAsync(GatewayDispatchEnvelope envelope, CancellationToken ct)
    {
        var message = new GatewayStatusMessage(Guid.NewGuid(), envelope.PaymentId, envelope.GatewayCode, "Processing", null, envelope.CorrelationId ?? Guid.Empty);
        Publish("gateway.intermediate." + envelope.GatewayCode, message);
        return Task.CompletedTask;
    }

    public Task PublishFinalAsync(GatewayDispatchEnvelope envelope, CancellationToken ct)
    {
        var status = envelope.PaymentId.GetHashCode() % 2 == 0 ? "Succeeded" : "Failed";
        var reason = status == "Failed" ? "Random failure" : null;
        var message = new GatewayStatusMessage(Guid.NewGuid(), envelope.PaymentId, envelope.GatewayCode, status, reason, envelope.CorrelationId ?? Guid.Empty);
        Publish("gateway.final." + envelope.GatewayCode, message);
        return Task.CompletedTask;
    }

    private void Publish(string routingKey, GatewayStatusMessage message)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = _channel.CreateBasicProperties();
        props.Persistent = true;
        props.MessageId = message.MessageId.ToString();
        props.CorrelationId = message.CorrelationId == Guid.Empty ? null : message.CorrelationId.ToString();
        props.Type = message.Status;

        _channel.BasicPublish(_options.Exchange, routingKey, props, body);
        _logger.LogInformation("Published {Status} for payment {PaymentId} via {RoutingKey}", message.Status, message.PaymentId, routingKey);
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

sealed record GatewayDispatchEnvelope(Guid PaymentId, string GatewayCode, Guid? CorrelationId = null);
sealed record GatewayStatusMessage(Guid MessageId, Guid PaymentId, string GatewayCode, string Status, string? Reason, Guid CorrelationId);

sealed class GatewayOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "dev";
    public string Password { get; set; } = "devpass";
    public string Exchange { get; set; } = "gateway.topic";
    public string DispatchQueue { get; set; } = "gateway.dispatch.q";
    public ushort PrefetchCount { get; set; } = 10;
}
