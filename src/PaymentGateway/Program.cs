using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Rmq = RabbitMQ.Client;
using Shared;

var builder = Host.CreateApplicationBuilder(args);

var amqpUri = builder.Configuration.GetValue<string>("Rabbit:Uri")
              ?? "amqp://guest:guest@localhost:5672/";
var provider = builder.Configuration.GetValue<string>("Provider") ?? "simulated";

builder.Services.AddSingleton<Rmq.IConnection>(sp =>
{
    var f = new Rmq.ConnectionFactory
    {
        Uri = new Uri(amqpUri),
        AutomaticRecoveryEnabled = true
    };
    // v7 API is async; block for DI construction
    return f.CreateConnectionAsync().GetAwaiter().GetResult();
});

builder.Services.AddHostedService(sp => new GatewayService(
    sp.GetRequiredService<Rmq.IConnection>(),
    sp.GetRequiredService<ILogger<GatewayService>>(),
    provider));

// OpenTelemetry for Gateway
builder.Services.AddOpenTelemetry()
    .WithTracing(tp => tp
        .ConfigureResource(r => r.AddService("PaymentGateway", serviceVersion: "1.0.0"))
        .AddSource(Tracing.GatewayActivitySourceName)
        .AddConsoleExporter());

var app = builder.Build();
await app.RunAsync();

internal class GatewayService(Rmq.IConnection connection, ILogger<GatewayService> logger, string provider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var ch = await connection.CreateChannelAsync();
        await ch.BasicQosAsync(0, 16, false);
        var consumer = new AsyncEventingBasicConsumer(ch);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var rk = ea.RoutingKey; // e.g., gateway.simulated.payment.created
            try
            {
                // Extract parent context from headers if present
                ActivityContext parentCtx = default;
                if (ea.BasicProperties?.Headers != null && ea.BasicProperties.Headers.TryGetValue("traceparent", out var tpv))
                {
                    var tp = tpv?.ToString();
                    var ts = ea.BasicProperties.Headers.TryGetValue("tracestate", out var tsv) ? tsv?.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(tp)) ActivityContext.TryParse(tp, ts, out parentCtx);
                }

                using var consume = Tracing.GatewayActivitySource.StartActivity(
                    "consume x.gateway.request", ActivityKind.Consumer, parentCtx);
                consume?.SetTag("messaging.system", "rabbitmq");
                consume?.SetTag("messaging.rabbitmq.routing_key", rk);

                var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ea.Body.Span));
                var root = doc.RootElement;
                var paymentId = root.GetProperty("payment_id").GetGuid();

                // Simulate processing delay
                await Task.Delay(Random.Shared.Next(200, 1000), stoppingToken);

                // Intermediate status
                var intermediateRk = Topology.GatewayStatusIntermediate(provider);
                await PublishStatusAsync(ch, intermediateRk, paymentId, parentCtx, new
                {
                    payment_id = paymentId,
                    provider,
                    status_code = "AuthorizedPendingCapture",
                    details = new { risk = Random.Shared.Next(1, 100) },
                    emitted_at = DateTimeOffset.UtcNow
                });

                await Task.Delay(Random.Shared.Next(300, 1200), stoppingToken);

                // Final status
                var finalRk = Topology.GatewayStatusFinal(provider);
                await PublishStatusAsync(ch, finalRk, paymentId, parentCtx, new
                {
                    payment_id = paymentId,
                    provider,
                    final_status = "Authorized",
                    emitted_at = DateTimeOffset.UtcNow
                });

                await ch.BasicAckAsync(ea.DeliveryTag, false);
                logger.LogInformation("Processed payment dispatch payment={PaymentId}", paymentId);
            }
            catch (TransientGatewayException tex)
            {
                // Route to appropriate retry exchange preserving original routing key
                var retryCount = 1;
                if (ea.BasicProperties?.Headers != null && ea.BasicProperties.Headers.TryGetValue("x-retry-count", out var rc) &&
                    rc is byte[] b)
                {
                    if (int.TryParse(Encoding.UTF8.GetString(b), out var parsed)) retryCount = parsed + 1;
                    else retryCount = 2;
                }

                var retryExchange = Topology.NextRequestRetryExchange(retryCount);
                await using var ch2 = await connection.CreateChannelAsync();
                var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent, Headers = new Dictionary<string, object> { ["x-retry-count"] = retryCount, ["provider"] = provider } };
                await ch2.BasicPublishAsync(retryExchange, rk, true, props, ea.Body, CancellationToken.None);
                await ch.BasicAckAsync(ea.DeliveryTag, false);
                logger.LogWarning(tex, "Gateway transient error, republished to {Ex} rk={Rk} retry={Retry}", retryExchange, rk, retryCount);
            }
            catch (Exception ex)
            {
                // Terminal failure: route to DLX
                await using var ch3 = await connection.CreateChannelAsync();
                var props = new BasicProperties { DeliveryMode = DeliveryModes.Persistent, Headers = new Dictionary<string, object> { ["reason"] = ex.GetType().Name } };
                var deadKey = $"dead.{rk}";
                await ch3.BasicPublishAsync(Topology.ExchangeDlx, deadKey, true, props, ea.Body, CancellationToken.None);
                await ch.BasicAckAsync(ea.DeliveryTag, false);
                logger.LogError(ex, "Gateway terminal error, sent to DLX rk={Rk}", rk);
            }
            return;
        };
        await ch.BasicConsumeAsync(Topology.QueueGatewayRequestSimulated, false, consumer);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task PublishStatusAsync(IChannel ch, string routingKey, Guid paymentId, ActivityContext parentCtx, object body)
    {
        var props = new BasicProperties();
        props.DeliveryMode = DeliveryModes.Persistent;
        props.MessageId = Guid.NewGuid().ToString();
        props.CorrelationId = paymentId.ToString();
        props.Headers = new Dictionary<string, object> { ["provider"] = provider, ["schema_version"] = 1, ["x-retry-count"] = 0 };
        using var activity = Tracing.GatewayActivitySource.StartActivity("publish x.gateway.status", ActivityKind.Producer, parentCtx);
        // inject the same context
        if (activity != null)
        {
            props.Headers["traceparent"] = activity.Id;
            if (!string.IsNullOrEmpty(activity.TraceStateString)) props.Headers["tracestate"] = activity.TraceStateString;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        await ch.BasicPublishAsync(Topology.ExchangeGatewayStatus, routingKey, true, props, bytes, CancellationToken.None);
    }
}

// A marker exception to simulate transient problems (e.g., network hiccup to the real gateway)
internal class TransientGatewayException(string message) : Exception(message);