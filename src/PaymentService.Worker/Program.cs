using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Shared;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
var pg = builder.Configuration.GetConnectionString("Payments")
         ?? "Host=localhost;Port=5432;Database=payments;Username=app;Password=app;Pooling=true";
var amqpUri = builder.Configuration.GetValue<string>("Rabbit:Uri")
              ?? "amqp://guest:guest@localhost:5672/";

builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(pg));
builder.Services.AddSingleton<IConnectionFactory>(sp =>
{
    return new ConnectionFactory
    {
        Uri = new Uri(amqpUri),
        AutomaticRecoveryEnabled = true
    };
});

builder.Services.AddHostedService<OutboxPublisherService>();
builder.Services.AddHostedService<StatusConsumerService>();

// OpenTelemetry Tracing for worker
builder.Services.AddOpenTelemetry()
    .WithTracing(tp => tp
        .ConfigureResource(r => r.AddService("PaymentService.Worker", serviceVersion: "1.0.0"))
        .AddSource(Tracing.WorkerActivitySourceName)
        .AddConsoleExporter());

var app = builder.Build();
await app.RunAsync();

// Background service: polls outbox and publishes to Rabbit with confirms
internal class OutboxPublisherService(ILogger<OutboxPublisherService> logger, NpgsqlDataSource ds, IConnectionFactory connectionFactory)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var items = await ClaimBatchAsync(ds, 50, stoppingToken);
                if (items.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                foreach (var it in items)
                {
                    var provider = (string)(it.payload?.gateway_provider ?? "simulated");
                    var routingKey = Topology.GatewayRequestCreated(provider);
                    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(it.payload));
                    var props = new BasicProperties();
                    props.DeliveryMode = DeliveryModes.Persistent; // persistent
                    // Reuse message_id and propagate trace context
                    var messageId = (Guid)it.message_id;
                    props.MessageId = messageId.ToString();
                    props.CorrelationId = (it.aggregate_id as Guid?)?.ToString();
                    props.Headers = new Dictionary<string, object>
                        { ["schema_version"] = 1, ["provider"] = provider, ["x-retry-count"] = 0 };

                    // Create producer span and inject context
                    var traceparent = it.traceparent as string;
                    var tracestate = it.tracestate as string;
                    var baggage = it.baggage as string;
                    ActivityContext parentCtx = default;
                    if (!string.IsNullOrWhiteSpace(traceparent)) ActivityContext.TryParse(traceparent, tracestate, out parentCtx);
                    using var activity = Tracing.WorkerActivitySource.StartActivity(
                        "publish x.gateway.request",
                        ActivityKind.Producer,
                        parentCtx);
                    activity?.SetTag("messaging.system", "rabbitmq");
                    activity?.SetTag("messaging.destination", Topology.ExchangeGatewayRequest);
                    activity?.SetTag("messaging.rabbitmq.routing_key", routingKey);
                    activity?.SetTag("messaging.message_id", props.MessageId);

                    // If using link semantics instead of parent, you could use AddLink here. We keep parent for simplicity.
                    // If we had no parentCtx but have stored strings, inject them directly
                    if (!string.IsNullOrEmpty(traceparent)) props.Headers["traceparent"] = traceparent;
                    if (!string.IsNullOrEmpty(tracestate)) props.Headers["tracestate"] = tracestate;
                    if (!string.IsNullOrEmpty(baggage)) props.Headers["baggage"] = baggage;

                    await channel.BasicPublishAsync(Topology.ExchangeGatewayRequest, routingKey, true, props, body, CancellationToken.None);

                    // Cast dynamic values to static types to avoid dynamic dispatch on logger extension methods
                    var outboxId = (long)it.id;
                    var paymentId = (Guid)it.aggregate_id;
                    await MarkSentAsync(ds, outboxId, paymentId, stoppingToken);
                    logger.LogInformation("Published and marked Sent outboxId={Id} payment={PaymentId}", outboxId, paymentId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in OutboxPublisherService loop");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private static async Task<List<dynamic>> ClaimBatchAsync(NpgsqlDataSource ds, int limit, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var sql = @"with cte as (
  select id
  from outbox
  where processing_status = 'Pending'
    and (locked_until is null or locked_until < now())
  order by occurred_at
  for update skip locked
  limit @limit
)
update outbox o
set processing_status = 'Processing',
    locked_until = now() + interval '30 seconds',
    lock_token = gen_random_uuid(),
    attempt_count = attempt_count + 1
from cte
where o.id = cte.id
returning o.id, o.aggregate_id, o.type, o.payload, o.attempt_count, o.message_id, o.traceparent, o.tracestate, o.baggage;";
        var rows = await conn.QueryAsync(sql, new { limit });
        return rows.ToList();
    }

    private static async Task BackoffAsync(NpgsqlDataSource ds, long id, int attempt, CancellationToken ct)
    {
        var delay = attempt switch { 1 => "5 seconds", 2 => "30 seconds", _ => "300 seconds" };
        await using var conn = await ds.OpenConnectionAsync(ct);
        await conn.ExecuteAsync(
            $"update outbox set processing_status='Pending', locked_until = now() + interval '{delay}', last_error='Publish confirm timeout' where id = @id",
            new { id });
    }

    private static async Task MarkSentAsync(NpgsqlDataSource ds, long id, Guid paymentId, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(
            "update outbox set processing_status='Sent', processed_at = now(), locked_until=null, lock_token=null where id = @id",
            new { id }, tx);
        await conn.ExecuteAsync("update payments set status='Dispatched' where payment_id=@pid", new { pid = paymentId }, tx);
        await conn.ExecuteAsync(@"insert into payment_status_history(payment_id, status, source) values(@pid, 'Dispatched', 'Worker')",
            new { pid = paymentId }, tx);
        await tx.CommitAsync(ct);
    }
}

// Background service: consumes gateway status updates and updates DB idempotently
internal class StatusConsumerService(ILogger<StatusConsumerService> logger, NpgsqlDataSource ds, IConnectionFactory connectionFactory)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.BasicQosAsync(0, 32, false);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var messageId = ea.BasicProperties?.MessageId ?? Guid.NewGuid().ToString();
                var payload = JsonDocument.Parse(Encoding.UTF8.GetString(ea.Body.Span)).RootElement;
                var provider = ea.BasicProperties?.Headers != null && ea.BasicProperties.Headers.TryGetValue("provider", out var pv)
                    ? pv?.ToString() ?? "simulated"
                    : "simulated";
                var paymentId = payload.TryGetProperty("payment_id", out var p) ? p.GetGuid() : Guid.Empty;
                var final = ea.RoutingKey.EndsWith(".final", StringComparison.OrdinalIgnoreCase);
                var statusValue = final ? payload.TryGetProperty("final_status", out var fs) ? fs.GetString() : "Authorized" :
                    payload.TryGetProperty("status_code", out var sc) ? sc.GetString() : "AuthorizedPendingCapture";

                // Extract context and create consumer span
                ActivityContext parentCtx = default;
                if (ea.BasicProperties?.Headers != null)
                {
                    var headers = ea.BasicProperties.Headers;
                    var tp = headers.TryGetValue("traceparent", out var tpv) ? tpv?.ToString() : null;
                    var ts = headers.TryGetValue("tracestate", out var tsv) ? tsv?.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(tp)) ActivityContext.TryParse(tp, ts, out parentCtx);
                }

                using var activity = Tracing.WorkerActivitySource.StartActivity(
                    "consume x.gateway.status",
                    ActivityKind.Consumer,
                    parentCtx);
                activity?.SetTag("messaging.system", "rabbitmq");
                activity?.SetTag("messaging.destination", Topology.ExchangeGatewayStatus);
                activity?.SetTag("messaging.rabbitmq.routing_key", ea.RoutingKey);
                activity?.SetTag("messaging.message_id", messageId);

                await using var conn = await ds.OpenConnectionAsync(stoppingToken);
                await using var tx = await conn.BeginTransactionAsync(stoppingToken);
                var rows = await conn.ExecuteAsync("insert into inbox(message_id, source) values(@m,@s) on conflict do nothing",
                    new { m = Guid.Parse(messageId), s = $"gateway.{provider}" }, tx);
                if (rows == 0)
                {
                    await channel.BasicAckAsync(ea.DeliveryTag, false);
                    return;
                }

                await conn.ExecuteAsync("update payments set status=@st where payment_id=@pid",
                    new { st = statusValue ?? (final ? "Authorized" : "Validated"), pid = paymentId }, tx);
                await conn.ExecuteAsync("insert into payment_status_history(payment_id, status, source) values(@pid,@st,'Worker')",
                    new { pid = paymentId, st = statusValue }, tx);
                await tx.CommitAsync(stoppingToken);
                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                // On transient error route to retry exchange with same routing key
                var rk = ea.RoutingKey;
                var retryCount = 1;
                if (ea.BasicProperties?.Headers != null && ea.BasicProperties.Headers.TryGetValue("x-retry-count", out var rc))
                {
                    if (rc is byte[] bb && int.TryParse(Encoding.UTF8.GetString(bb), out var parsed)) retryCount = parsed + 1;
                    else if (rc is string ss && int.TryParse(ss, out var parsed2)) retryCount = parsed2 + 1;
                    else retryCount = 2;
                }

                var retryExchange = Topology.NextStatusRetryExchange(retryCount);
                await using var ch = await connection.CreateChannelAsync();
                var republishProps = new BasicProperties
                {
                    DeliveryMode = DeliveryModes.Persistent, Headers = new Dictionary<string, object> { ["x-retry-count"] = retryCount }
                };
                await ch.BasicPublishAsync(retryExchange, rk, true, republishProps, ea.Body, CancellationToken.None);
                // Ack original to prevent redelivery storm
                try
                {
                    await channel.BasicAckAsync(ea.DeliveryTag, false);
                }
                catch
                {
                }

                // log
                logger.LogWarning(ex, "Status processing failed; republished to {RetryExchange} rk={RoutingKey} retry={Retry}",
                    retryExchange, rk, retryCount);
            }
        };
        await channel.BasicConsumeAsync(Topology.QueueWorkerStatus, false, consumer);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }
}