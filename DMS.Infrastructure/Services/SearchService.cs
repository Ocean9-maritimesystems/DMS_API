using System.Net;
using System.Text.RegularExpressions;
using DMS.Application.DTO.Features;
using DMS.Application.Interface;
using DMS.Data.EF.Context;
using Microsoft.EntityFrameworkCore;

namespace DMS.Data.EF.Features;

/// <summary>
/// Feature 8, phase 1: one search box over manuals (number, name and the text of the latest version), circulars and
/// alerts, other documents and suggestions. LIKE matching in dbo.Api_Search (Database/020_ApiProcedures.sql), every word must match.
/// Phase 2 (AI assistant over the same sources, through a cloud provider) plugs in behind this same service.
/// </summary>
public sealed partial class SearchService : ISearchService
{
    public static readonly string[] Types = { "Manual", "Circular", "Document", "Suggestion" };
    private const int PerType = 50;
    private const int MaxWords = 6;
    private readonly GeneralContext _db;

    public SearchService(GeneralContext db) => _db = db;

    public async Task<List<SearchHit>> SearchAsync(string query, string? types, OfficeUser user)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Length > 100 ? w[..100] : w).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxWords).ToList();
        if (words.Count == 0 || words.Sum(w => w.Length) < 2) return new List<SearchHit>();

        var wanted = string.IsNullOrWhiteSpace(types)
            ? Types.ToList()
            : types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => Types.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (wanted.Count == 0) return new List<SearchHit>();

        // dbo.Api_Search: every word (a LIKE pattern, or NULL when there are fewer words) must match.
        var w = Enumerable.Range(0, MaxWords).Select(i => i < words.Count ? "%" + Escape(words[i]) + "%" : null).ToArray();
        var hits = await _db.Database.SqlQuery<SearchHit>($@"EXEC dbo.Api_Search @Types = {string.Join(",", wanted)}, @First = {words[0]},
    @W0 = {w[0]}, @W1 = {w[1]}, @W2 = {w[2]}, @W3 = {w[3]}, @W4 = {w[4]}, @W5 = {w[5]}, @PerType = {PerType}")
            .ToListAsync();

        foreach (var h in hits) h.Snippet = Clean(h.Snippet);
        return hits;
    }

    /// <summary>LIKE wildcards in the user's words are matched literally.</summary>
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static string? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Tags().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ").Trim();
        // A cut tag or word at either end of the window
        var gt = text.IndexOf('>');
        if (gt >= 0 && gt < 80 && text.IndexOf('<') is < 0 or > 80) text = text[(gt + 1)..].TrimStart();
        var lt = text.LastIndexOf('<');
        if (lt >= 0 && text.Length - lt < 80) text = text[..lt].TrimEnd();
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    [GeneratedRegex("<[^>]*>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
