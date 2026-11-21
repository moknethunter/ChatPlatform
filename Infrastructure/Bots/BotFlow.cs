namespace ChatPlatform.Infrastructure.Bots;

public static class BotFlow
{
    public record Node(string? Id, string? Type, string? Text, string? Next, string? Otherwise, int X, int Y);
    public record Definition(string? Start, Node[]? Nodes);
    public record Evaluation(List<string> Path, List<string> Replies);

    private static bool ValidId(string? id) => id is { Length: >= 1 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');

    public static string? Validate(Definition? flow)
    {
        if (flow?.Nodes is not { Length: >= 2 and <= 40 } nodes || !ValidId(flow.Start)) return "invalid_graph";
        var map = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n == null || !ValidId(n.Id) || !map.TryAdd(n.Id!, n) || n.X is < 0 or > 3000 || n.Y is < 0 or > 3000)
                return "invalid_node";
            if (n.Type is not ("start" or "contains" or "reply" or "end")) return "invalid_type";
            if ((n.Text?.Length ?? 0) > 2000 || (n.Type is "contains" or "reply" && string.IsNullOrWhiteSpace(n.Text))) return "invalid_text";
            if (n.Type == "contains" && (!ValidId(n.Next) || !ValidId(n.Otherwise))) return "invalid_branch";
            if (n.Type is "start" or "reply" && (!ValidId(n.Next) || !string.IsNullOrEmpty(n.Otherwise))) return "invalid_next";
            if (n.Type == "end" && (!string.IsNullOrEmpty(n.Next) || !string.IsNullOrEmpty(n.Otherwise))) return "invalid_end";
        }
        if (!map.TryGetValue(flow.Start!, out var start) || start.Type != "start" || nodes.Count(n => n.Type == "start") != 1) return "invalid_start";
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        bool Walk(string? id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            if (!map.TryGetValue(id, out var node) || visiting.Contains(id)) return false;
            if (visited.Contains(id)) return true;
            visiting.Add(id);
            if (!Walk(node.Next) || !Walk(node.Otherwise)) return false;
            visiting.Remove(id); visited.Add(id); return true;
        }
        return Walk(flow.Start) && visited.Count == nodes.Length ? null : "cycle_or_unreachable_node";
    }


    public static Evaluation Evaluate(Definition flow, string input)
    {
        var map = flow.Nodes!.ToDictionary(n => n.Id!);
        var path = new List<string>(); var replies = new List<string>();
        string? cursor = flow.Start;
        for (int i = 0; cursor != null && i < 40; i++)
        {
            var node = map[cursor]; path.Add(cursor);
            if (node.Type == "end") break;
            if (node.Type == "reply") replies.Add(node.Text!);
            cursor = node.Type == "contains" && !input.Contains(node.Text!, StringComparison.OrdinalIgnoreCase)
                ? node.Otherwise : node.Next;
        }
        return new(path, replies);
    }
}
