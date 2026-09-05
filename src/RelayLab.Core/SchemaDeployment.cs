using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace RelayLab.Core;

// A fresh baseline, not an M1/M2 data migration. One privileged, explicit job owns DDL.
public static class SchemaDeployment
{
    public const int Version = 1;
    private static string Fingerprint(DbContext db) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(db.Database.GenerateCreateScript().Replace("\r\n", "\n"))));

    public static async Task ApplyAsync(DbContext db, string kind, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=N'RelayLabSchemaDeployment', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=30000;
            IF @result < 0 THROW 51010, 'Schema deployment lock unavailable.', 1;
            """, ct);
        var empty = !await db.Database.SqlQueryRaw<bool>("SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM sys.tables WHERE is_ms_shipped=0) THEN 1 ELSE 0 END AS bit) AS [Value]").SingleAsync(ct);
        if (empty)
        {
            foreach (var batch in System.Text.RegularExpressions.Regex.Split(db.Database.GenerateCreateScript(), @"(?m)^GO\s*$"))
                if (!string.IsNullOrWhiteSpace(batch)) await db.Database.ExecuteSqlRawAsync(batch, ct);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.RelayLabSchema (Id int NOT NULL PRIMARY KEY CHECK(Id=1), Version int NOT NULL, Kind varchar(16) NOT NULL, ModelHash char(64) NOT NULL)", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.RelayLabSchema (Id,Version,Kind,ModelHash) VALUES (1,{Version},{kind},{Fingerprint(db)})", ct);
        }
        await VerifyAsync(db, kind, ct);
        await transaction.CommitAsync(ct);
    }

    public static async Task VerifyAsync(DbContext db, string kind, CancellationToken ct)
    {
        var marker = await db.Database.SqlQueryRaw<bool>("SELECT CAST(CASE WHEN OBJECT_ID('dbo.RelayLabSchema') IS NULL THEN 0 ELSE 1 END AS bit) AS [Value]").SingleAsync(ct);
        if (!marker) throw new InvalidOperationException("Fresh M3 schema required. Unversioned M1/M2 databases are not upgraded or erased.");
        var matches = await db.Database.SqlQuery<bool>($"SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM dbo.RelayLabSchema WHERE Id=1 AND Version={Version} AND Kind={kind} AND ModelHash={Fingerprint(db)}) THEN 1 ELSE 0 END AS bit) AS [Value]").SingleAsync(ct);
        if (!matches) throw new InvalidOperationException("Schema baseline differs from this image; refuse deployment or rollback.");
    }

    public static async Task GrantRuntimeAsync(DbContext db, IConfiguration configuration, bool receiver, CancellationToken ct)
    {
        var principals = receiver ? new[] { ("receiver", "Schema:ReceiverObjectId") }
            : new[] { ("api", "Schema:ApiObjectId"), ("worker", "Schema:WorkerObjectId") };
        foreach (var (role, key) in principals)
        {
            var name = "relaylab-" + role;
            var principal = Guid.Parse(CloudHosting.RequiredGuid(configuration, key));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @name sysname={name}, @sid binary(16)=CONVERT(binary(16),{principal});
                IF EXISTS(SELECT 1 FROM sys.database_principals WHERE name=@name AND (type<>'E' OR sid<>@sid))
                    THROW 51011, 'Runtime identity does not match the existing SQL principal.', 1;
                IF NOT EXISTS(SELECT 1 FROM sys.database_principals WHERE name=@name)
                BEGIN
                    DECLARE @sql nvarchar(max)=N'CREATE USER '+QUOTENAME(@name)+N' WITH SID='+CONVERT(varchar(34),@sid,1)+N', TYPE=E';
                    EXEC sys.sp_executesql @sql;
                END
                """, ct);
            // All identifiers below are constants; no input becomes an SQL identifier.
            var grants = role switch
            {
                "api" => """
                    GRANT CONNECT TO [relaylab-api]; GRANT SELECT ON dbo.RelayLabSchema TO [relaylab-api];
                    GRANT SELECT, INSERT, UPDATE ON dbo.Deliveries TO [relaylab-api];
                    GRANT SELECT, INSERT ON dbo.OutboxMessages TO [relaylab-api];
                    GRANT SELECT ON dbo.DeliveryAttempts TO [relaylab-api];
                    GRANT SELECT, INSERT ON dbo.ReplayRequests TO [relaylab-api];
                    """,
                "worker" => """
                    GRANT CONNECT TO [relaylab-worker]; GRANT SELECT ON dbo.RelayLabSchema TO [relaylab-worker];
                    GRANT SELECT, UPDATE ON dbo.Deliveries TO [relaylab-worker];
                    GRANT SELECT, INSERT, UPDATE ON dbo.OutboxMessages TO [relaylab-worker];
                    GRANT SELECT, INSERT, UPDATE ON dbo.DeliveryAttempts TO [relaylab-worker];
                    """,
                _ => """
                    GRANT CONNECT TO [relaylab-receiver]; GRANT SELECT ON dbo.RelayLabSchema TO [relaylab-receiver];
                    GRANT SELECT, INSERT ON dbo.Receipts TO [relaylab-receiver];
                    GRANT SELECT, INSERT ON dbo.Effects TO [relaylab-receiver];
                    """
            };
            await db.Database.ExecuteSqlRawAsync(grants, ct);
        }
    }
}
