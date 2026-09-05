using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RelayLab.Core;
using RelayLab.Receiver;
using Xunit;

namespace RelayLab.Tests;

[Collection("Infrastructure")]
[Trait("Category", "Integration")]
public sealed class SchemaDeploymentTests(Infrastructure infrastructure)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_baseline_is_atomic_repeatable_and_refuses_incompatible_marker(bool receiver)
    {
        var connection = new SqlConnectionStringBuilder(infrastructure.SqlConnection) { InitialCatalog = "RelayLabSchema_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        await using DbContext db = receiver ? new ReceiverDb(new DbContextOptionsBuilder<ReceiverDb>().UseSqlServer(connection).Options)
            : new RelayDb(new DbContextOptionsBuilder<RelayDb>().UseSqlServer(connection).Options);
        var kind = receiver ? "receiver" : "relay";
        try
        {
            await ((Microsoft.EntityFrameworkCore.Storage.RelationalDatabaseCreator)db.GetService<Microsoft.EntityFrameworkCore.Storage.IDatabaseCreator>()).CreateAsync();
            await SchemaDeployment.ApplyAsync(db, kind, default);
            await SchemaDeployment.ApplyAsync(db, kind, default);
            await SchemaDeployment.VerifyAsync(db, kind, default);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM dbo.RelayLabSchema").SingleAsync());
            await db.Database.ExecuteSqlRawAsync("UPDATE dbo.RelayLabSchema SET Version=99");
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaDeployment.ApplyAsync(db, kind, default));
            Assert.Equal(99, await db.Database.SqlQueryRaw<int>("SELECT Version AS [Value] FROM dbo.RelayLabSchema").SingleAsync());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [Fact]
    public async Task Unversioned_database_is_preserved_and_rejected_without_silent_adoption()
    {
        await using var runtime = new TestRuntime(infrastructure);
        await runtime.StartAsync();
        using var accepted = await runtime.SubmitAsync("preserve-unversioned-M2-data");
        var id = await TestRuntime.IdAsync(accepted);
        await using var db = runtime.OpenRelay();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaDeployment.ApplyAsync(db, "relay", default));
        Assert.Equal(id, (await db.Deliveries.SingleAsync()).Id);
        Assert.Single(await db.OutboxMessages.ToListAsync());
    }
}
