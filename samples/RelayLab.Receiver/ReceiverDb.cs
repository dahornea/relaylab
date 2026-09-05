using Microsoft.EntityFrameworkCore;
using RelayLab.Core;

namespace RelayLab.Receiver;

public sealed class Receipt
{
    public Guid DeliveryId { get; set; }
    public string DestinationId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public DateTime ReceivedUtc { get; set; }
}

public sealed class DocumentEffect
{
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public string DocumentId { get; set; } = "";
}

public sealed class ReceiverDb(DbContextOptions<ReceiverDb> options) : DbContext(options)
{
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<DocumentEffect> Effects => Set<DocumentEffect>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var receipt = model.Entity<Receipt>();
        receipt.HasKey(r => r.DeliveryId);
        receipt.Property(r => r.DestinationId).HasMaxLength(128);
        receipt.Property(r => r.EventType).HasMaxLength(128);
        receipt.Property(r => r.DocumentId).HasMaxLength(128);
        var effect = model.Entity<DocumentEffect>();
        effect.HasKey(e => e.Id);
        effect.HasIndex(e => e.DeliveryId).IsUnique();
        effect.Property(e => e.DocumentId).HasMaxLength(128);
        effect.HasOne<Receipt>().WithMany().HasForeignKey(e => e.DeliveryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ReceiverLedger(IDbContextFactory<ReceiverDb> databases)
{
    public async Task<bool> RecordAsync(Guid id, EventRequest request, CancellationToken ct) => (await RecordDetailedAsync(id, request, ct)).Matches;

    public async Task<ReceiptResult> RecordDetailedAsync(Guid id, EventRequest request, CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        var existing = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.DeliveryId == id, ct);
        if (existing is not null) return new(Matches(existing, request), false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct);
            db.Receipts.Add(new Receipt
            {
                DeliveryId = id, DestinationId = request.DestinationId!, EventType = request.EventType!,
                DocumentId = request.Data!.DocumentId!, ReceivedUtc = now
            });
            await db.SaveChangesAsync(ct);
            db.Effects.Add(new DocumentEffect { Id = Guid.NewGuid(), DeliveryId = id, DocumentId = request.Data!.DocumentId! });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            Telemetry.Effects.Add(1);
            return new(true, true);
        }
        catch (DbUpdateException error) when (SqlErrors.IsUniqueViolation(error))
        {
            await transaction.RollbackAsync(ct);
            await using var fresh = await databases.CreateDbContextAsync(ct);
            existing = await fresh.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.DeliveryId == id, ct);
            if (existing is null) throw;
            return new(Matches(existing, request), false);
        }
    }

    private static bool Matches(Receipt receipt, EventRequest request) =>
        EventContract.Matches(request, receipt.DestinationId, receipt.EventType, receipt.DocumentId);
}

public sealed record ReceiptResult(bool Matches, bool Created);
