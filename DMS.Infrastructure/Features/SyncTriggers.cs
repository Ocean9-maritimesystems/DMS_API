using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DMS.Data.EF.Context;

// Tables with the vessel sync triggers TR_<table>_SyncRow_InsDel / _Upd (dbo.Sync_CreateRowTriggers). EF Core 8 must
// know about them: without HasTrigger it saves with an OUTPUT clause, which SQL Server refuses on a table with
// triggers ("The target table ... cannot have any enabled triggers ...").
// Which tables have them is read from dbo.TableID_Setup (HQ_Has_InsDEL_Trigger / HQ_Has_Upd_Trigger), once per run
// of the API, when EF builds each context's model: after changing the flags (and running Sync_CreateRowTriggers),
// restart the API.

internal static class SyncRowTriggers
{
    /// <summary>
    /// The flagged tables, plus the tables active in Sync_Table with no TableID_Setup row (Sync_CreateRowTriggers keeps
    /// their triggers). Without the HQ flag columns: every active table in Sync_Table.
    /// </summary>
    private const string SyncedTablesSql = """
        DECLARE @sql nvarchar(max);
        IF OBJECT_ID(N'dbo.TableID_Setup', N'U') IS NOT NULL
           AND COL_LENGTH('dbo.TableID_Setup', 'HQ_Has_InsDEL_Trigger') IS NOT NULL
           AND COL_LENGTH('dbo.TableID_Setup', 'HQ_Has_Upd_Trigger') IS NOT NULL
        BEGIN
            SET @sql = N'SELECT DISTINCT LTRIM(RTRIM(TableName)) FROM dbo.TableID_Setup
                          WHERE HQ_Has_InsDEL_Trigger = 1 OR HQ_Has_Upd_Trigger = 1';
            IF OBJECT_ID(N'dbo.Sync_Table', N'U') IS NOT NULL
                SET @sql += N' UNION SELECT s.TableName FROM dbo.Sync_Table s
                                WHERE s.IsActive = 1
                                  AND NOT EXISTS (SELECT 1 FROM dbo.TableID_Setup t WHERE LTRIM(RTRIM(t.TableName)) = s.TableName)';
            EXEC sp_executesql @sql;
        END
        ELSE IF OBJECT_ID(N'dbo.Sync_Table', N'U') IS NOT NULL
            EXEC sp_executesql N'SELECT TableName FROM dbo.Sync_Table WHERE IsActive = 1';
        """;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, HashSet<string>> Loaded = new(StringComparer.Ordinal);

    /// <summary>The tables with row-sync triggers in this database (read once per connection string and cached).</summary>
    public static IReadOnlySet<string> SyncedTables(string connectionString)
    {
        lock (Gate)
        {
            if (Loaded.TryGetValue(connectionString, out var cached)) return cached;
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = SyncedTablesSql;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (!reader.IsDBNull(0)) tables.Add(reader.GetString(0).Trim());
            }
            Loaded[connectionString] = tables;
            return tables;
        }
    }

    /// <summary>Other triggers that make EF Core leave out the OUTPUT clause (012_NewFeatures.sql).</summary>
    private static readonly Dictionary<string, string> OtherTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DM_ManualVersion"] = "TR_DM_ManualVersion_Inbox",
        ["DM_Manual"] = "TR_DM_Manual_AckDueDays",
        ["CIR_Master"] = "TR_CIR_Master_Inbox",
        ["OtherDocumentMaster"] = "TR_OtherDocumentMaster_Inbox",
    };

    /// <summary>
    /// The context's connection string, from its options. Database.GetConnectionString() cannot be used here: inside
    /// OnModelCreating it needs the model that is being built ("An attempt was made to use the model while it was
    /// being created").
    /// </summary>
    public static string? ConnectionStringOf(DbContext context)
    {
        try
        {
            var options = context.GetService<IDbContextOptions>();
            var relational = options.Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault();
            return relational?.ConnectionString ?? relational?.Connection?.ConnectionString;
        }
        catch
        {
            return null; // then every table is declared (see Apply)
        }
    }

    /// <summary>
    /// Declares the sync trigger on every entity of this context that is mapped to a table with row-sync triggers
    /// (from TableID_Setup). If the list cannot be read, every table is declared: EF then never uses an OUTPUT clause,
    /// which is always safe (a little slower on tables without triggers).
    /// </summary>
    public static void Apply(ModelBuilder modelBuilder, string? connectionString)
    {
        IReadOnlySet<string>? synced = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(connectionString)) synced = SyncedTables(connectionString);
        }
        catch
        {
            synced = null; // not readable now: declare every table
        }

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var table = entityType.GetTableName();
            if (table == null || entityType.IsOwned()) continue;
            if (synced == null || synced.Contains(table))
                modelBuilder.Entity(entityType.ClrType).ToTable(t => t.HasTrigger($"TR_{table}_SyncRow"));
            if (OtherTriggers.TryGetValue(table, out var trigger))
                modelBuilder.Entity(entityType.ClrType).ToTable(t => t.HasTrigger(trigger));
        }
    }
}

public partial class CircularsContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder) => SyncRowTriggers.Apply(modelBuilder, SyncRowTriggers.ConnectionStringOf(this));
}

public partial class GeneralContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder) => SyncRowTriggers.Apply(modelBuilder, SyncRowTriggers.ConnectionStringOf(this));
}

public partial class OtherDocumentsContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder) => SyncRowTriggers.Apply(modelBuilder, SyncRowTriggers.ConnectionStringOf(this));
}
