namespace DMS.Application.DTO.Manuals;

/// <summary>Where a manual version is in its approval flow (GET api/Manuals/GetApprovalProgress): one entry per approval step.</summary>
public sealed class ManualApprovalProgressDto
{
    public bool ApprovalRequired { get; set; }
    public long StatusId { get; set; }
    public List<ManualApprovalStepDto> Steps { get; set; } = new();
}

public sealed class ManualApprovalStepDto
{
    public long StatusId { get; set; }
    /// <summary>The status name, e.g. "Supt Approval".</summary>
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? Dept { get; set; }
    /// <summary>"done", "current" or "pending".</summary>
    public string State { get; set; } = "pending";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? Comments { get; set; }
}
