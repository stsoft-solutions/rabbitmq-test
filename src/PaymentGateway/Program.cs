using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;

var builder = Host.CreateApplicationBuilder(args);

var amqpUri = builder.Configuration.GetValue<string>("Rabbit:Uri")
             ?? "amqp://guest:guest@localhost:5672/";
var provider = builder.Configuration.GetValue<string>("Provider") ?? "simulated";

builder.Services.AddSingleton<IConnection>(sp =>
{
    var f = new ConnectionFactory { Uri = new Uri(amqpUri), DispatchConsumersAsync = true, AutomaticRecoveryEnabled = true };
    return f.CreateConnection();
});

builder.Services.AddHostedService(sp => new GatewayService(
    sp.GetRequiredService<IConnection>(),
    sp.GetRequiredService<ILogger<GatewayService>>(),
    provider));

// OpenTelemetry for Gateway
builder.Services.AddOpenTelemetry()
    .WithTracing(tp => tp
        .ConfigureResource(r => r.AddService(serviceName: "PaymentGateway", serviceVersion: "1.0.0"))
        .AddSource(Tracing.GatewayActivitySourceName)
        .AddConsoleExporter());

var app = builder.Build();
await app.RunAsync();

class GatewayService(IConnection connection, ILogger<GatewayService> logger, string provider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ch = connection.CreateModel();
        ch.BasicQos(0, 16, false);
        var consumer = new AsyncEventingBasicConsumer(ch);
        consumer.Received += async (object? _, BasicDeliverEventArgs ea) =>
        {
            var rk = ea.RoutingKey; // e.g., gateway.simulated.payment.created
            try
            {
                // Extract parent context from headers if present
                ActivityContext parentCtx = default;
                if (ea.BasicProperties?.Headers != null && ea.BasicProperties.Headers.TryGetValue("traceparent", out var tpv))
                {
                    string? tp = tpv?.ToString();
                    string? ts = ea.BasicProperties.Headers.TryGetValue("tracestate", out var tsv) ? tsv?.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(tp)) ActivityContext.TryParse(tp, ts, out parentCtx);
                }
                using var consume = Tracing.GatewayActivitySource.StartActivity(
                    "consume x.gateway.request", ActivityKind.Consumer, parentCtx);
                consume?.SetTag("messaging.system", "rabbitmq");
                consume?.SetTag("messaging.rabbitmq.routing_key", rk);

                var doc = JsonDocument.Parse(Encoding.UTF8.GetString(ea.Body.ToArray()));
                var root = doc.RootElement;
                var paymentId = root.GetProperty("payment_id").GetGuid();

                // Simulate processing delay
                await Task.Delay(Random.Shared.Next(200, 1000), stoppingToken);

                // Intermediate status
                var intermediateRk = Topology.GatewayStatusIntermediate(provider);
                PublishStatus(ch, intermediateRk, paymentId, parentCtx, new
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
                PublishStatus(ch, finalRk, paymentId, parentCtx, new
                {
                    payment_id = paymentId,
                    provider,
                    final_status = "Authorized",
                    emitted_at = DateTimeOffset.UtcNow
                });

                ch.BasicAck(ea.DeliveryTag, false);
                logger.LogInformation("Processed payment dispatch payment={PaymentId}", paymentId);
            }
            catch (TransientGatewayException tex)
            {
                // Route to appropriate retry exchange preserving original routing key
                var retryCount = 1;
                if (ea.BasicProperties?.Headers != null && ea.BasicProperties.Headers.TryGetValue("x-retry-count", out var rc) && rc is byte[] b)
                {
                    if (int.TryParse(Encoding.UTF8.GetString(b), out var parsed)) retryCount = parsed + 1; else retryCount = 2;
                }
                var retryExchange = Topology.NextRequestRetryExchange(retryCount);
                using var ch2 = connection.CreateModel();
                var props = ch2.CreateBasicProperties();
                props.Persistent = true;
                props.Headers = new Dictionary<string, object> { ["x-retry-count"] = retryCount, ["provider"] = provider };
                ch2.BasicPublish(retryExchange, rk, props, ea.Body);
                ch.BasicAck(ea.DeliveryTag, false);
                logger.LogWarning(tex, "Gateway transient error, republished to {Ex} rk={Rk} retry={Retry}", retryExchange, rk, retryCount);
            }
            catch (Exception ex)
            {
                // Terminal failure: route to DLX
                using var ch3 = connection.CreateModel();
                var props = ch3.CreateBasicProperties();
                props.Persistent = true;
                props.Headers = new Dictionary<string, object> { ["reason"] = ex.GetType().Name };
                var deadKey = $"dead.{rk}";
                ch3.BasicPublish(Topology.ExchangeDlx, deadKey, props, ea.Body);
                ch.BasicAck(ea.DeliveryTag, false);
                logger.LogError(ex, "Gateway terminal error, sent to DLX rk={Rk}", rk);
            }
        };
        ch.BasicConsume(Topology.QueueGatewayRequestSimulated, false, consumer);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private void PublishStatus(IModel ch, string routingKey, Guid paymentId, ActivityContext parentCtx, object body)
    {
        var props = ch.CreateBasicProperties();
        props.Persistent = true;
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
        ch.BasicPublish(Topology.ExchangeGatewayStatus, routingKey, true, props, bytes);
    }
}

// A marker exception to simulate transient problems (e.g., network hiccup to the real gateway)
class TransientGatewayException(string message) : Exception(message);
