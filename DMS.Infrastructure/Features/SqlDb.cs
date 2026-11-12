using System.Data;
using DMS.Data.EF.Context;
using DMS.Data.EF.Models.Circulars;
using DMS.Data.EF.Models.Manuals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DMS.Data.EF.Features;

/// <summary>
/// Shared helpers for the feature services: new keys (Proc_NextID), the DM_Settings / CIR_Settings values, a
/// transaction on GeneralContext, and stored procedures whose columns are not fixed (reports).
/// No SQL text is built here: settings go through EF, everything else is a stored procedure call.
/// </summary>
public sealed class SqlDb
{
    private readonly GeneralContext _ctx;
    private readonly DmsContext _dms;
    private readonly CircularsContext _cir;

    public SqlDb(GeneralContext ctx, DmsContext dms, CircularsContext cir)
    {
        _ctx = ctx;
        _dms = dms;
        _cir = cir;
    }

    public const string Office = "HQ";
    /// <summary>VesselID of office-only rows: never synced to a vessel.</summary>
    public const int OfficeOnly = -2;
    public const int AllVessels = -1;

    public Task<long> NextIdAsync(string table, string origin = Office, int vesselId = -1, int count = 1)
        => _ctx.NextIdAsync(table, origin, vesselId, count);

    /// <summary>A setting value from DM_Settings or CIR_Settings (the office row, VesselID -1, first).</summary>
    public async Task<string?> SettingAsync(string table, string name)
    {
        switch (table)
        {
            case "DM_Settings":
                return await _dms.DmSettings.AsNoTracking()
                    .Where(s => s.SettingName == name)
                    .OrderBy(s => s.VesselID == -1 ? 0 : 1)
                    .Select(s => s.SettingValue)
                    .FirstOrDefaultAsync();
            case "CIR_Settings":
                return await _cir.CIR_Settings.AsNoTracking()
                    .Where(s => s.SettingName == name)
                    .OrderBy(s => s.VesselID == -1 ? 0 : 1)
                    .Select(s => s.SettingValue)
                    .FirstOrDefaultAsync();
            default:
                throw new ArgumentException("Unknown settings table.", nameof(table));
        }
    }

    /// <summary>Sets the office row (VesselID -1) of a setting, adding it when it is not there.</summary>
    public async Task SetSettingAsync(string table, string name, string? value)
    {
        switch (table)
        {
            case "DM_Settings":
            {
                var rows = await _dms.DmSettings.Where(s => s.SettingName == name && s.VesselID == -1).ToListAsync();
                if (rows.Count == 0)
                    _dms.DmSettings.Add(new DmSettings
                    {
                        DmSettingsId = await _dms.NextIdAsync("DM_Settings", Office, -1),
                        Origin = Office, VesselID = -1, SettingName = name, SettingValue = value,
                    });
                foreach (var row in rows) row.SettingValue = value;
                await _dms.SaveChangesAsync();
                return;
            }
            case "CIR_Settings":
            {
                var rows = await _cir.CIR_Settings.Where(s => s.SettingName == name && s.VesselID == -1).ToListAsync();
                if (rows.Count == 0)
                    _cir.CIR_Settings.Add(new CIR_Settings
                    {
                        CIR_SettingsID = await _cir.NextIdAsync("CIR_Settings", Office, -1),
                        Origin = Office, VesselID = -1, SettingName = name, SettingValue = value,
                    });
                foreach (var row in rows) row.SettingValue = value;
                await _cir.SaveChangesAsync();
                return;
            }
            default:
                throw new ArgumentException("Unknown settings table.", nameof(table));
        }
    }

    /// <summary>Runs work in one transaction on GeneralContext (committed when it returns, rolled back if it throws).</summary>
    public async Task InTransactionAsync(Func<Task> work)
    {
        if (_ctx.Database.CurrentTransaction != null) { await work(); return; }
        await using var tx = await _ctx.Database.BeginTransactionAsync();
        await work();
        await tx.CommitAsync();
    }

    /// <summary>
    /// Runs a stored procedure and returns its rows as column name -> value (DBNull becomes null), in the order it
    /// returns them. For procedures whose columns differ per call (reports); fixed shapes use Database.SqlQuery.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> ProcRowsAsync(string procedure, params (string Name, object? Value)[] ps)
    {
        var result = new List<Dictionary<string, object?>>();
        var connection = _ctx.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = procedure;
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandTimeout = 120;
            cmd.Transaction = _ctx.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var (name, value) in ps)
            {
                if (value is System.Data.Common.DbParameter given) { cmd.Parameters.Add(given); continue; } // e.g. a table-valued parameter
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                if (value is DateTime) p.DbType = DbType.DateTime2;
                cmd.Parameters.Add(p);
            }
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var row = new Dictionary<string, object?>(r.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < r.FieldCount; i++)
                    row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                result.Add(row);
            }
        }
        finally
        {
            if (openedHere && _ctx.Database.CurrentTransaction == null) await connection.CloseAsync();
        }
        return result;
    }
}
