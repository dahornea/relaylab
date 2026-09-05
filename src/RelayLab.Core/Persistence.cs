using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace RelayLab.Core;

public sealed class Delivery
{
    public Guid Id { get; set; }
    public Guid WorkId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTime AcceptedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public string LastOutcome { get; set; } = "AwaitingPublication";
    public string RemoteOutcome { get; set; } = "NotAttempted";
    public EventRequest ToRequest() => new(DestinationId, EventType, new(DocumentId));
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime NextPublishUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public int SendCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class DeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public Guid WorkId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Outcome { get; set; } = "Started";
    public int? HttpStatus { get; set; }
    public string? FailureCategory { get; set; }
    public string RemoteOutcome { get; set; } = "Unknown";
}

public sealed class RelayDb(DbContextOptions<RelayDb> options) : DbContext(options)
{
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var delivery = model.Entity<Delivery>();
        delivery.ToTable("Deliveries");
        delivery.HasKey(d => d.Id);
        delivery.HasIndex(d => d.IdempotencyKey).IsUnique();
        delivery.Property(d => d.IdempotencyKey).HasMaxLength(128).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        delivery.Property(d => d.DestinationId).HasMaxLength(128);
        delivery.Property(d => d.EventType).HasMaxLength(128);
        delivery.Property(d => d.DocumentId).HasMaxLength(128);
        delivery.Property(d => d.Status).HasMaxLength(16);
        delivery.Property(d => d.LastOutcome).HasMaxLength(80);
        delivery.Property(d => d.RemoteOutcome).HasMaxLength(32);
        var outbox = model.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages");
        outbox.HasKey(o => o.Id);
        outbox.HasIndex(o => o.DeliveryId).IsUnique();
        outbox.HasIndex(o => new { o.PublishedUtc, o.NextPublishUtc });
        outbox.Property(o => o.LastError).HasMaxLength(80);
        outbox.HasOne<Delivery>().WithMany().HasForeignKey(o => o.DeliveryId).OnDelete(DeleteBehavior.Restrict);
        var attempt = model.Entity<DeliveryAttempt>();
        attempt.ToTable("DeliveryAttempts");
        attempt.HasKey(a => a.Id);
        attempt.HasIndex(a => new { a.DeliveryId, a.StartedUtc });
        attempt.HasOne<Delivery>().WithMany().HasForeignKey(a => a.DeliveryId).OnDelete(DeleteBehavior.Restrict);
        attempt.Property(a => a.Outcome).HasMaxLength(32);
        attempt.Property(a => a.FailureCategory).HasMaxLength(80);
        attempt.Property(a => a.RemoteOutcome).HasMaxLength(32);
    }

    public Task<DateTime> UtcNowAsync(CancellationToken ct) =>
        Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct);
}

public static class SqlErrors
{
    public static bool IsUniqueViolation(DbUpdateException error) =>
        error.InnerException is SqlException { Number: 2601 or 2627 };
}
