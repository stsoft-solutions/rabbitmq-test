using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PaymentService.Persistence;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentStatusHistory> PaymentStatusHistory => Set<PaymentStatusHistory>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payments");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);
    }
}

public sealed record Payment
{
    public Guid PaymentId { get; init; }
    public Guid ClientRequestId { get; init; }
    public long AmountMinor { get; init; }
    public string CurrencyCode { get; init; } = string.Empty;
    public Guid CustomerId { get; init; }
    public string GatewayCode { get; init; } = string.Empty;
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; set; }
    public long Version { get; set; }
    public ICollection<PaymentStatusHistory> StatusHistory { get; init; } = new List<PaymentStatusHistory>();
    public ICollection<OutboxMessage> OutboxMessages { get; init; } = new List<OutboxMessage>();
}

public sealed record PaymentStatusHistory
{
    public long HistoryId { get; init; }
    public Guid PaymentId { get; init; }
    public PaymentStatus? PreviousStatus { get; init; }
    public PaymentStatus NewStatus { get; init; }
    public string? ReasonCode { get; init; }
    public string? Payload { get; init; }
    public DateTime OccurredUtc { get; init; }
    public Guid CorrelationId { get; init; }
}

public sealed record OutboxMessage
{
    public long OutboxId { get; init; }
    public Guid PaymentId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public string RoutingKey { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public DateTime? ProcessedUtc { get; set; }
    public int Attempts { get; set; }
    public Guid TraceId { get; init; }
}

public sealed record InboxMessage
{
    public long InboxId { get; init; }
    public Guid MessageId { get; init; }
    public Guid PaymentId { get; init; }
    public string SourceExchange { get; init; } = string.Empty;
    public string PayloadHash { get; init; } = string.Empty;
    public DateTime ProcessedUtc { get; init; }
}

public enum PaymentStatus
{
    Pending,
    Validated,
    Dispatched,
    PendingGateway,
    Processing,
    Succeeded,
    Failed
}

internal sealed class PaymentEntityConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(x => x.PaymentId);
        builder.HasIndex(x => x.ClientRequestId).IsUnique();
        builder.Property(x => x.AmountMinor).IsRequired();
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(x => x.GatewayCode).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>();
        builder.Property(x => x.CreatedUtc).HasColumnType("timestamptz");
        builder.Property(x => x.UpdatedUtc).HasColumnType("timestamptz");
        builder.Property(x => x.Version).IsConcurrencyToken();
    }
}

internal sealed class PaymentStatusHistoryConfiguration : IEntityTypeConfiguration<PaymentStatusHistory>
{
    public void Configure(EntityTypeBuilder<PaymentStatusHistory> builder)
    {
        builder.ToTable("payment_status_history");
        builder.HasKey(x => x.HistoryId);
        builder.Property(x => x.Payload).HasColumnType("jsonb");
        builder.Property(x => x.OccurredUtc).HasColumnType("timestamptz");
        builder.Property(x => x.PreviousStatus).HasConversion<string>();
        builder.Property(x => x.NewStatus).HasConversion<string>();
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox");
        builder.HasKey(x => x.OutboxId);
        builder.Property(x => x.Payload).HasColumnType("jsonb");
        builder.Property(x => x.CreatedUtc).HasColumnType("timestamptz");
        builder.Property(x => x.ProcessedUtc).HasColumnType("timestamptz");
    }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox");
        builder.HasKey(x => x.InboxId);
        builder.HasIndex(x => x.MessageId).IsUnique();
        builder.Property(x => x.ProcessedUtc).HasColumnType("timestamptz");
    }
}
