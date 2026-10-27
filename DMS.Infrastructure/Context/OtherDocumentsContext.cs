using DMS.Application.DTO.OtherDocument;
using DMS.Data.EF.Models.Circulars;
using DMS.Data.EF.Models.OtherDocument;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Context;

public partial class OtherDocumentsContext : DbContext
{
    private DBConnectionString _dbActions;

    public OtherDocumentsContext(DbContextOptions<OtherDocumentsContext> options, DBConnectionString dbActions) : base(options)
    {
        _dbActions = dbActions;
    }

    public virtual DbSet<OtherDocumentMaster> OtherDocumentMaster { get; set; } = null!;

    public virtual DbSet<OtherDocument_Folder> OtherDocument_Folder { get; set; } = null!;

    public virtual DbSet<OtherDocument_Blob> OtherDocument_Blob { get; set; } = null!;

    public virtual DbSet<OtherDocument_Type> OtherDocument_Type { get; set; } = null!;

    public virtual DbSet<OtherDocument_Log> OtherDocument_Log { get; set; } = null!;

    public virtual DbSet<OtherDocument_UserFavourite> OtherDocument_UserFavourite { get; set; }

    public virtual DbSet<OtherDocument_Settings> OtherDocument_Settings { get; set; }

    public virtual DbSet<OtherDocument_Notification> OtherDocument_Notification { get; set; }

    public virtual DbSet<Vw_OtherDocument_MasterBlob> Vw_OtherDocument { get; set; } = null!;

    public virtual DbSet<VwOtherDocumentNotification> VwOtherDocumentNotification { get; set; }

    public virtual DbSet<OtherDocumentList> OtherDocumentList { get; set; }

    public virtual DbSet<OtherDocumentChart> OtherDocumentChart { get; set; }

    public virtual DbSet<OtherDocumentRecentList> OtherDocumentRecentList { get; set; } = null!;


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlServer(_dbActions.DefaultConnection);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OtherDocumentMaster>(entity =>
        {
            entity.HasKey(e => e.OtherDocumentMasterID).HasName("PK_OtherDocument");
            entity.Property(e => e.OtherDocumentMasterID).ValueGeneratedNever();
            entity.Property(e => e.DocumentTitle).HasMaxLength(250).HasDefaultValue("");
            entity.Property(e => e.OtherDocument_TypeID);
            entity.Property(e => e.UploadedOn).HasColumnType("datetime").HasDefaultValueSql("(getdate())");
            entity.Property(e => e.UploadedBy).HasMaxLength(150).HasDefaultValue("");
        });

        modelBuilder.Entity<OtherDocument_Folder>(entity =>
        {
            entity.HasKey(e => e.OtherDocument_FolderID).HasName("PK_OtherDocument_Folder");
            entity.Property(e => e.OtherDocument_FolderID).ValueGeneratedNever();
        });

        modelBuilder.Entity<OtherDocument_Blob>(entity =>
        {
            entity.HasKey(e => e.OtherDocument_BlobID).HasName("PK_OtherDocument_Blob");
            entity.Property(e => e.OtherDocument_BlobID).ValueGeneratedNever();
            entity.Property(e => e.OtherDocumentMasterID).IsRequired();
            entity.Property(e => e.AddedOn).HasColumnType("datetime").HasDefaultValueSql("(getdate())");
            entity.Property(e => e.FileName).HasMaxLength(150);
            entity.Property(e => e.FileType).HasMaxLength(50);
            entity.Property(e => e.OriginalFileType).HasMaxLength(50);
            entity.Property(e => e.HQ_DatabaseName).HasDefaultValue("");
            entity.Property(e => e.HQ_Local_Folder).HasDefaultValue("");
            entity.Property(e => e.HQ_Storage_Option).HasDefaultValue("");
            entity.Property(e => e.VSL_DatabaseName).HasDefaultValue("");
            entity.Property(e => e.VSL_Local_Folder).HasDefaultValue("");
            entity.Property(e => e.VSL_Storage_Option).HasDefaultValue("");
            entity.Property(e => e.Cloud_ContainerName).HasDefaultValue("");
            entity.Property(e => e.Cloud_Directory).HasDefaultValue("");
        });

        modelBuilder.Entity<OtherDocument_Type>(entity =>
        {
            entity.HasKey(e => e.OtherDocument_TypeID).HasName("PK_OtherDocument_Type");
            entity.Property(e => e.OtherDocument_TypeID).ValueGeneratedNever();
            entity.Property(e => e.DocumentType).HasMaxLength(100).HasDefaultValue("");
            entity.Property(e => e.DocumentCode).HasMaxLength(250);
            entity.Property(e => e.IsActive).HasDefaultValue(1);
        });

        modelBuilder.Entity<OtherDocument_Log>(entity =>
        {
            entity.HasKey(e => e.OtherDocument_LogID).HasName("PK_OtherDocument_Log");
            entity.Property(e => e.OtherDocument_LogID).ValueGeneratedNever();
            entity.Property(e => e.Activity).HasDefaultValue("");
            entity.Property(e => e.LogDate).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Remarks).HasDefaultValue("");
            entity.Property(e => e.UserName).HasDefaultValue("");
        });

        modelBuilder.Entity<OtherDocument_UserFavourite>(entity =>
        {
            entity.Property(e => e.OtherDocument_UserFavouriteID).ValueGeneratedNever();
        });

        modelBuilder.Entity<OtherDocument_Notification>(entity =>
        {
            entity.HasKey(e => e.OtherDocument_NotificationID).HasName("PK_OtherDocument_Notification");

            entity.Property(e => e.OtherDocument_NotificationID).ValueGeneratedNever();
            entity.Property(e => e.Comments).HasDefaultValue("");
            entity.Property(e => e.CreatedDate).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.IsRead).HasDefaultValue(0);
        });

        modelBuilder.Entity<OtherDocument_Settings>(entity =>
        {
            entity.Property(e => e.OtherDocument_SettingsID).ValueGeneratedNever();
        });

        modelBuilder.Entity<Vw_OtherDocument_MasterBlob>(eb =>
        {
            eb.HasNoKey();
            eb.ToView("Vw_OtherDocument_MasterBlob");
        });

        modelBuilder.Entity<VwOtherDocumentNotification>(entity =>
        {
            entity.ToView("Vw_OtherDocumentNotification");
        });

        modelBuilder.Entity<OtherDocumentChart>(eb =>
        {
            eb.HasNoKey();
            eb.ToView(null);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}