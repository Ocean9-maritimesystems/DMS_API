// Dashboard > Overview: circulars and alerts whose "valid until" date is coming up.
namespace DMS.Application.DTO.Circulars;

/// <summary>A released circular or alert that stops being valid within the next few days (GET api/Circular/GetExpiringSoon).</summary>
public sealed class CircularExpiringDto
{
    public long Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? Title { get; set; }
    /// <summary>"Circular" or "Alert".</summary>
    public string Type { get; set; } = "Circular";
    public DateTime ValidUntil { get; set; }
    /// <summary>Whole days from today: 0 = last valid day is today.</summary>
    public int DaysLeft { get; set; }
}
