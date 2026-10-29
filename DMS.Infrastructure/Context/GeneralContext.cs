using DMS.Application.DTO.Features;
using DMS.Data.EF.Models;
using DMS.Data.EF.Models.Features;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Context;

public partial class GeneralContext : DbContext
{
    private DBConnectionString _dbActions;

    public GeneralContext(DbContextOptions<GeneralContext> options, DBConnectionString dbActions) : base(options)
    {
        _dbActions = dbActions;
    }

    public virtual DbSet<GenErrorHqLog> GenErrorHqLogs { get; set; }

    public virtual DbSet<GenErrorVslLog> GenErrorVslLogs { get; set; }

    public virtual DbSet<HqUser> HqUsers { get; set; }

    public virtual DbSet<HqUserRole> HqUserRoles { get; set; }

    public virtual DbSet<VVessel> VVessels { get; set; }

    public virtual DbSet<VslStaff> VslStaffs { get; set; }

    public virtual DbSet<SugMaster> SugMasters { get; set; } = null!;

    public virtual DbSet<SugLog> SugLogs { get; set; } = null!;
    
    public virtual DbSet<SugBlob> SugBlobs { get; set; } = null!;
    
    public virtual DbSet<DmsInbox> DmsInbox { get; set; } = null!;
    
    public virtual DbSet<DmsInboxRead> DmsInboxReads { get; set; } = null!;
    
    public virtual DbSet<BlobTableSettingRow> BlobTableSettings { get; set; } = null!;
    
    public virtual DbSet<SyncVessel> SyncVessels { get; set; } = null!;
    
    public virtual DbSet<Sync_Log> Sync_Logs { get; set; } = null!;
    
    public virtual DbSet<SyncVesselKey> SyncVesselKeys { get; set; } = null!;
    
    public virtual DbSet<SyncChangeLog> SyncChangeLogs { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlServer(_dbActions.DefaultConnection);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AckPendingRow>(entity =>
        {
            entity.HasNoKey();
            entity.ToView(null);
        });
        modelBuilder.Entity<GenErrorHqLog>(entity =>
        {
            entity.Property(e => e.GenErrorHqLogId).ValueGeneratedNever();
            entity.Property(e => e.AddedOn).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.ApplicationName).HasDefaultValue("");
            entity.Property(e => e.FunctionName).HasDefaultValue("");
            entity.Property(e => e.LoggedInUserId).HasDefaultValue("");
            entity.Property(e => e.PageName).HasDefaultValue("");
        });

        modelBuilder.Entity<AckPendingRow>(entity =>
        {
            entity.HasNoKey();
            entity.ToView(null);
        });

        modelBuilder.Entity<GenErrorVslLog>(entity =>
        {
            entity.Property(e => e.AddedOn).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.ApplicationName).HasDefaultValue("");
            entity.Property(e => e.FunctionName).HasDefaultValue("");
            entity.Property(e => e.LoggedInUserId).HasDefaultValue("");
            entity.Property(e => e.PageName).HasDefaultValue("");
        });

        modelBuilder.Entity<HqUser>(entity =>
        {
            entity.Property(e => e.HqUsersId).ValueGeneratedNever();
            entity.Property(e => e.Designation).HasDefaultValue("");
            entity.Property(e => e.Email).HasDefaultValue("");
            entity.Property(e => e.EmpId).HasDefaultValue("");
            entity.Property(e => e.IsDisabled).HasDefaultValue(0);
            entity.Property(e => e.PhoneNo).HasDefaultValue("");
        });

        modelBuilder.Entity<HqUserActionVsl>(entity =>
        {
            entity.Property(e => e.HqUserActionVslsId).ValueGeneratedNever();
            entity.Property(e => e.IsActionVsls).HasDefaultValue(0);
            entity.Property(e => e.IsPrimaryVsl).HasDefaultValue(0);
            entity.Property(e => e.IsSecdonaryVsl).HasDefaultValue(0);
        });

        modelBuilder.Entity<HqUserRole>(entity =>
        {
            entity.Property(e => e.HqUserRolesId).ValueGeneratedNever();
        });

        modelBuilder.Entity<VVessel>(entity =>
        {
            entity.Property(e => e.VVesselId).ValueGeneratedNever();
        });


        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
