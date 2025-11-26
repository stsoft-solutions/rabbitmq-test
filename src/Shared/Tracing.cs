using System.Diagnostics;

namespace Shared;

public static class Tracing
{
    public const string WorkerActivitySourceName = "PaymentService.Worker";
    public const string GatewayActivitySourceName = "PaymentGateway";

    public static readonly ActivitySource WorkerActivitySource = new(WorkerActivitySourceName);
    public static readonly ActivitySource GatewayActivitySource = new(GatewayActivitySourceName);
}
