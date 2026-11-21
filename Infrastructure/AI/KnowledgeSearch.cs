using System.Text.RegularExpressions;
using ChatPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.AI;

public static class KnowledgeSearch
{
    public record Match(Guid Id, string Title, string Body);
    // Local lexical retrieval, not embeddings and not an external model API.
    public static async Task<Match[]> FindAsync(PlatformDbContext db, string tenant, string text, CancellationToken ct)
    {
        var tokens = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(t => t.Length >= 2).Distinct().Take(12).ToArray();
        if (tokens.Length == 0) return Array.Empty<Match>();
        var rows = await db.KnowledgeArticles.AsNoTracking().Where(x => x.TenantId == tenant && x.IsPublished)
            .OrderByDescending(x => x.UpdatedAtUtc).Take(500).Select(x => new { x.Id, x.Title, x.Body }).ToListAsync(ct);
        return rows.Select(x => new { x, Score = tokens.Sum(t => (x.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
             + (x.Body.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1 : 0)) })
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(4)
            .Select(x => new Match(x.x.Id, x.x.Title, x.x.Body.Length > 3000 ? x.x.Body[..3000] : x.x.Body)).ToArray();
    }
}
