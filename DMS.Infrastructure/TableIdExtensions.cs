using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF;

/// <summary>
/// New primary keys for every insert come from dbo.Proc_NextID (Database/009_Proc_NextID.sql), never from
/// MAX(ID) + 1 in code: the procedure keeps the last number handed out per table in TableID_Setup and locks
/// while it does so, so two users saving at the same time never get the same ID.
///
///   Origin "HQ"  - office rows: no vessel check (VesselID is ignored).
///   Origin "VSL" - rows from a vessel: numbered in that vessel's own range (V_Vessel.TableIDstart).
/// </summary>
public static class TableIdExtensions
{
    public const string OriginHQ = "HQ";
    public const string OriginVessel = "VSL";

    /// <summary>
    /// The next ID for <typeparamref name="TEntity"/>'s table. With <paramref name="count"/> &gt; 1 a block is
    /// reserved: the IDs returned .. returned + count - 1 are all the caller's.
    /// </summary>
    public static Task<long> NextIdAsync<TEntity>(this DbContext db, string origin = OriginHQ, int vesselId = -1,
        int count = 1, CancellationToken ct = default) where TEntity : class
    {
        var table = db.Model.FindEntityType(typeof(TEntity))?.GetTableName()
                    ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped to a table in {db.GetType().Name}.");
        return db.NextIdAsync(table, origin, vesselId, count, ct);
    }

    /// <summary>The next ID for the table named <paramref name="tableName"/> (see the generic overload).</summary>
    public static async Task<long> NextIdAsync(this DbContext db, string tableName, string origin = OriginHQ, int vesselId = -1,
        int count = 1, CancellationToken ct = default)
    {
        var newId = new SqlParameter("@NewTableID", SqlDbType.BigInt) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "EXEC dbo.Proc_NextID @TableName, @Origin, @VesselID, @Incrementor, @NewTableID OUTPUT",
            new object[]
            {
                new SqlParameter("@TableName", SqlDbType.VarChar, 50) { Value = tableName },
                new SqlParameter("@Origin", SqlDbType.VarChar, 3) { Value = string.IsNullOrWhiteSpace(origin) ? OriginHQ : origin.Trim().ToUpperInvariant() },
                new SqlParameter("@VesselID", SqlDbType.Int) { Value = vesselId },
                new SqlParameter("@Incrementor", SqlDbType.Int) { Value = Math.Max(1, count) },
                newId,
            },
            ct);

        return newId.Value is long id && id > 0
            ? id
            : throw new InvalidOperationException($"Proc_NextID returned no ID for table {tableName}.");
    }

    /// <summary>
    /// For a row that arrives from a vessel with the ID it was given on board (Proc_NextID in that vessel's range):
    /// that same ID when it is positive and free here, so both databases hold the row under one ID; otherwise
    /// (an older negative ID, or the ID is taken) a new one from Proc_NextID.
    /// </summary>
    public static async Task<long> KeepOrNextIdAsync<TEntity>(this DbContext db, long proposedId, string origin, int vesselId,
        CancellationToken ct = default) where TEntity : class
    {
        if (proposedId > 0)
        {
            var key = db.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()?.Properties.SingleOrDefault()?.Name
                      ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no single-column primary key.");
            var usedInThisSave = db.ChangeTracker.Entries<TEntity>()
                .Any(e => Convert.ToInt64(e.Property(key).CurrentValue) == proposedId);
            if (!usedInThisSave && !await db.Set<TEntity>().AnyAsync(e => Microsoft.EntityFrameworkCore.EF.Property<long>(e, key) == proposedId, ct))
                return proposedId;
        }
        return await db.NextIdAsync<TEntity>(origin, vesselId, ct: ct);
    }
}
