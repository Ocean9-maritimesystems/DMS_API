namespace DMS.Application.DTO.Features;

public sealed record OfficeUser(string UserId, string UserName, string Role, string? Dept);

public class AckPendingRow
{
    public int VesselID { get; set; }               // -1 for office users
    public string? VesselName { get; set; }
    public string ItemType { get; set; } = "";      // Manual | Circular | Alert
    public long ItemID { get; set; }
    public string? Number { get; set; }
    public string? Title { get; set; }
    public string? Version { get; set; }
    public string? Rank { get; set; }               // vessel rank, or office role
    public string? UserID { get; set; }
    public string? UserName { get; set; }
    public string? Dept { get; set; }
    public DateTime? ReleasedOn { get; set; }
    public DateTime? DueOn { get; set; }
    public int AckDueDays { get; set; }
    public bool Overdue => DueOn.HasValue && DueOn.Value < DateTime.Now;
    public int DaysWaiting => ReleasedOn.HasValue ? Math.Max(0, (int)(DateTime.Now - ReleasedOn.Value).TotalDays) : 0;
}

public class AckVesselSummary
{
    public int VesselID { get; set; }
    public string? VesselName { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public int ManualsPending { get; set; }
    public int ManualsOverdue { get; set; }
    public int CircularsPending { get; set; }
    public int CircularsOverdue { get; set; }
    public int AlertsPending { get; set; }
    public int AlertsOverdue { get; set; }
}

public class FeatureSettingsDto
{
    public string? AllowedRolesToViewAllRankPendingItems { get; set; }
    public int DefaultAckDueDaysManual { get; set; }
    public int DefaultAckDueDaysCircular { get; set; }
    public int DefaultAckDueDaysAlert { get; set; }
    public int FolderMaxFileSizeMB { get; set; }
    public int FolderMaxFiles { get; set; }
}
