using DMS.Application.DTO.Manuals;
using DMS.Data.EF.Models.Features;
using DMS.Data.EF.Models.Manuals;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Context;

public partial class DmsContext : DbContext
{
    private DBConnectionString _dbActions;

    public DmsContext(DbContextOptions<DmsContext> options, DBConnectionString dbActions) : base(options)
    {
        _dbActions = dbActions;
    }

    public virtual DbSet<Manual_TreeviewEntity> DmManual_Treeview { get; set; } = null!;

    public virtual DbSet<Manual_TreeviewFilteredEntity> DmManual_TreeviewFiltered { get; set; } = null!;

    public virtual DbSet<Manual_Content> DmManualContent { get; set; } = null!;
    
    public virtual DbSet<ManualVersionDetailsDto> DmManualVersionList { get; set; } = null!;

    public virtual DbSet<ManualDetails> DmManualList { get; set; } = null!;

    public virtual DbSet<ManualCount_User> UserManualCount { get; set; } = null!;

    public virtual DbSet<ManualCount_Dashboard> DashboardManualCount { get; set; } = null!;

    public virtual DbSet<ManualApprovalRoleListDto> approvalFlowDetails { get; set; } = null!;

    public virtual DbSet<ManualUserAckList> UserAckLists { get; set; } = null!;

    public virtual DbSet<DmApprovalAndMailSetup> DmApprovalAndMailSetups { get; set; }

    public virtual DbSet<DmCategory> DmCategories { get; set; }

    public virtual DbSet<DmManual> DmManuals { get; set; }
    
    public virtual DbSet<DmManualVersion> DmManualVersions { get; set; }

    public virtual DbSet<DmManualApprovalSetting> DmManualApprovalSettings { get; set; }

    public virtual DbSet<DmManualApprovedList> DmManualApprovedLists { get; set; }

    public virtual DbSet<DmManualAttachment> DmManualAttachments { get; set; }

    public virtual DbSet<DmManualLog> DmManualLogs { get; set; }

    public virtual DbSet<DmManualToAckHq> DmManualToAckHqs { get; set; }

    public virtual DbSet<DmManualToAckVsl> DmManualToAckVsls { get; set; }

    public virtual DbSet<DmManualUserAck> DmManualUserAcks { get; set; }

    public virtual DbSet<DmManualUserFavourite> DmManualUserFavourites { get; set; }

    public virtual DbSet<DM_ManualNotification> DM_ManualNotification { get; set; }

    public virtual DbSet<DmSettings> DmSettings { get; set; }

    public virtual DbSet<DmStatus> DmStatuses { get; set; }

    public virtual DbSet<VwDmManualVersion> VwDmManualVersions { get; set; }

    public virtual DbSet<VwManualNotification> VwManualNotification { get; set; }

    /// <summary>Which notifications each office user has read (Notification_Read; Features/NotificationReads).</summary>
    public virtual DbSet<NotificationRead> Notification_Read { get; set; } = null!;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder) => SyncRowTriggers.Apply(modelBuilder, null);

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlServer(_dbActions.DefaultConnection);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<DmCategory>(entity =>
        {
            entity.HasKey(e => e.DM_CategoryID).HasName("PK_DM_ManualCategory");

            entity.Property(e => e.DM_CategoryID).ValueGeneratedNever();
            entity.Property(e => e.CategoryCode).HasDefaultValue("");
            entity.Property(e => e.IsDisabled).HasDefaultValue(0);
        });

        modelBuilder.Entity<DmManual>(entity =>
        {
            entity.Property(e => e.DM_ManualID).ValueGeneratedNever();
            entity.Property(e => e.CanExport).HasDefaultValue(0);
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.IsApprovalRequired).HasDefaultValue(0);
            entity.Property(e => e.IsDeleted).HasDefaultValue(0);
            entity.Property(e => e.ManualCode).HasDefaultValue("");
            entity.Property(e => e.SortOrder).HasDefaultValueSql("((0))");
        });

        modelBuilder.Entity<DmManualVersion>(entity =>
        {
            entity.Property(e => e.DM_ManualVersionID).ValueGeneratedNever();
            entity.Property(e => e.Comments).HasDefaultValue("");
            entity.Property(e => e.IsActive).HasDefaultValue(0);
            entity.Property(e => e.IsDisabled).HasDefaultValue(0);
        });

        modelBuilder.Entity<DmManualApprovalSetting>(entity =>
        {
            entity.HasKey(e => e.DmManualApprovalSettingsId).HasName("PK_DM_ApprovalSettings");

            entity.Property(e => e.DmManualApprovalSettingsId).ValueGeneratedNever();
            entity.Property(e => e.CanAddEmailApprovalLink).HasDefaultValue(0);
            entity.Property(e => e.IsDisabled).HasDefaultValue(0);
            entity.Property(e => e.SendBackDmStatusId).HasDefaultValue(-1L);
            entity.Property(e => e.UserId).HasDefaultValue("");
        });

        modelBuilder.Entity<DmManualApprovedList>(entity =>
        {
            entity.Property(e => e.DmManualApprovedListId).ValueGeneratedNever();
            entity.Property(e => e.ApprovedDate).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Comments).HasDefaultValue("");
        });

        modelBuilder.Entity<DmManualAttachment>(entity =>
        {
            entity.HasKey(e => e.DmManualAttachmentsId).HasName("PK_DM_ManualAttachments");

            entity.Property(e => e.DmManualAttachmentsId).ValueGeneratedNever();
            entity.Property(e => e.AddedOn).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.IsDeleted).HasDefaultValue(0);
        });

        modelBuilder.Entity<DmManualLog>(entity =>
        {
            entity.HasKey(e => e.DmManualLogId).HasName("PK_DM_ManualLog");

            entity.Property(e => e.DmManualLogId).ValueGeneratedNever();
            entity.Property(e => e.Activity).HasDefaultValue("");
            entity.Property(e => e.Logdate).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Remarks).HasDefaultValue("");
            entity.Property(e => e.UserName).HasDefaultValue("");
            entity.Property(e => e.UserRank).HasDefaultValue("");
        });

        modelBuilder.Entity<DmManualToAckHq>(entity =>
        {
            entity.HasKey(e => e.DmManualToAckHqid).HasName("PK_DM_Manual_HQ");

            entity.Property(e => e.DmManualToAckHqid).ValueGeneratedNever();
            entity.Property(e => e.IsActive).HasDefaultValue(0);
        });

        modelBuilder.Entity<DmManualToAckVsl>(entity =>
        {
            entity.HasKey(e => e.DmManualToAckVslId).HasName("PK_DM_Manual_Vsl");

            entity.Property(e => e.DmManualToAckVslId).ValueGeneratedNever();
            entity.Property(e => e.IsActive).HasDefaultValue(0);
        });

        modelBuilder.Entity<DmManualUserAck>(entity =>
        {
            entity.HasKey(e => e.DmManualUserAckId).HasName("PK_DM_Manual_VslAck");

            entity.Property(e => e.DmManualUserAckId).ValueGeneratedNever();
            entity.Property(e => e.Comments).HasDefaultValue("");
            entity.Property(e => e.DateRead).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.IsActive).HasDefaultValue(0);
        });

        modelBuilder.Entity<DmManualUserFavourite>(entity =>
        {
            entity.Property(e => e.DM_ManualUserFavouriteID).ValueGeneratedNever();
        });

        modelBuilder.Entity<DM_ManualNotification>(entity =>
        {
            entity.HasKey(e => e.DM_ManualNotificationId).HasName("PK_DM_ManualNotification");

            entity.Property(e => e.DM_ManualNotificationId).ValueGeneratedNever();
            entity.Property(e => e.Comments).HasDefaultValue("");
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.IsRead).HasDefaultValue(0);
        });

        modelBuilder.Entity<DmSettings>(entity =>
        {
            entity.Property(e => e.DmSettingsId).ValueGeneratedNever();
        });

        modelBuilder.Entity<DmStatus>(entity =>
        {
            entity.HasKey(e => e.DmStatusId).HasName("PK_DM_ManualStatus");

            entity.Property(e => e.DmStatusId).ValueGeneratedNever();
            entity.Property(e => e.IsDisabled).HasDefaultValue(0);
            entity.Property(e => e.SortOrder).HasDefaultValue(-1);
            entity.Property(e => e.StatusCode).HasDefaultValue("");
        });

        modelBuilder.Entity<VwDmManualVersion>(entity =>
        {
            entity.ToView("Vw_DM_ManualVersions");
        });

        modelBuilder.Entity<VwManualNotification>(entity =>
        {
            entity.ToView("Vw_ManualNotification");
        });


        modelBuilder.Entity<Manual_TreeviewDto>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        modelBuilder.Entity<Manual_TreeviewEntity>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        // Keyless type that only reads GetManual_TreeViewList_Filtered.
        modelBuilder.Entity<Manual_TreeviewFilteredEntity>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        modelBuilder.Entity<ManualDetails>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        modelBuilder.Entity<ManualCount_User>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        modelBuilder.Entity<TreeviewDataDto>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        modelBuilder.Entity<ManualVersionDetailsDto>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });
        
        modelBuilder.Entity<Manual_TreeviewDto>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        modelBuilder.Entity<ManualApproveDto>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        modelBuilder.Entity<ManualUserAckList>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null); 
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
