namespace Shared;

public static class Topology
{
    // Exchanges
    public const string ExchangeGatewayRequest = "x.gateway.request";
    public const string ExchangeGatewayStatus = "x.gateway.status";
    public const string ExchangeGatewayRequestRetry5s = "x.gateway.request.retry.5s";
    public const string ExchangeGatewayRequestRetry30s = "x.gateway.request.retry.30s";
    public const string ExchangeGatewayRequestRetry5m = "x.gateway.request.retry.5m";
    public const string ExchangeGatewayStatusRetry5s = "x.gateway.status.retry.5s";
    public const string ExchangeGatewayStatusRetry30s = "x.gateway.status.retry.30s";
    public const string ExchangeGatewayStatusRetry5m = "x.gateway.status.retry.5m";
    public const string ExchangeDlx = "x.dlx";

    // Queues
    public const string QueueGatewayRequestSimulated = "q.gateway.request.simulated";
    public const string QueueWorkerStatus = "q.worker.status";

    // Routing keys patterns
    public static string GatewayRequestCreated(string provider) => $"gateway.{provider}.payment.created";
    public static string GatewayStatusIntermediate(string provider) => $"gateway.{provider}.payment.status.intermediate";
    public static string GatewayStatusFinal(string provider) => $"gateway.{provider}.payment.status.final";

    // Retry helper chooses next exchange based on attempt number (1-based)
    public static string NextRequestRetryExchange(int attempt) => attempt switch
    {
        1 => ExchangeGatewayRequestRetry5s,
        2 => ExchangeGatewayRequestRetry30s,
        _ => ExchangeGatewayRequestRetry5m
    };

    public static string NextStatusRetryExchange(int attempt) => attempt switch
    {
        1 => ExchangeGatewayStatusRetry5s,
        2 => ExchangeGatewayStatusRetry30s,
        _ => ExchangeGatewayStatusRetry5m
    };
}
