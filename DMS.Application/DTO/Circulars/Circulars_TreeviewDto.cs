using System.ComponentModel.DataAnnotations.Schema;

namespace DMS.Application.DTO.Circulars;

public class Circulars_TreeviewDto
{
    public required string Key { get; set; }

    public string Label { get; set; } = null!;

    [NotMapped]
    public CircularsTreeviewDataDto data { get; set; } = null!;

    public List<Circulars_TreeviewDto> Children { get; set; } = new List<Circulars_TreeviewDto>();
}

public class CircularsTreeviewDataDto
{
    public long CIR_MasterID { get; set; }
    
    public long CIR_BlobID { get; set; }

    public string? CIR_Number { get; set; }

    public string Title { get; set; } = null!;

    public string? StatusString { get; set; }

    public string? Category { get; set; }

    public string? Reference { get; set; }

    public string? Priority { get; set; }

    public string? DateIssued { get; set; }

    public long ParentID { get; set; }

    public int CIRLevel { get; set; }

    public int? IsActive { get; set; }

    public int IsFavourite { get; set; }

    public int IsCategory { get; set; }

}
