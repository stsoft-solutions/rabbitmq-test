using Microsoft.EntityFrameworkCore;
using PaymentService.Persistence;
using PaymentService.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Payments")));

builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<GatewayEventConsumerWorker>();

var host = builder.Build();
host.Run();
