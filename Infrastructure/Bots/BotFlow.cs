using ChatPlatform.Infrastructure.Adapters.Business;

namespace ChatPlatform.Infrastructure.Bots;

// Pure flow evaluator shared by simulator and runtime / محرك محاكاة وتنفيذ مشترك.
public static class BotFlow
{
    // Version-compatible node model; optional fields preserve older definitions / نموذج العقدة.
    public record Node(string? Id, string? Type, string? Text, string? Next, string? Otherwise, int X, int Y, string? MatchMode = null, string? Variable = null, string? BusinessSource = null, string? BusinessOperation = null);
    // Start ID and complete graph / تعريف المخطط.
    public record Definition(string? Start, Node[]? Nodes);
    // Return path, replies, resume cursor, handoff and variables / نتيجة التنفيذ.
    public record Evaluation(List<string> Path, List<string> Replies, string? NextNode, bool Handoff, Dictionary<string,string>? Variables = null, string? BusinessSource = null, string? BusinessOperation = null);

    // Bound IDs to safe ASCII characters / تحقق معرف العقدة.
    private static bool ValidId(string? id) => id is { Length: >= 1 and <= 40 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');

    // Restrict variable names for literal placeholder substitution / تحقق اسم المتغير.
    private static bool ValidVariable(string? name) => name is { Length: >= 1 and <= 24 } && char.IsAsciiLetter(name[0]) && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    // Reject malformed or unsafe graphs before execution / تحقق قبل التنفيذ.
    public static string? Validate(Definition? flow)
    {
        // Require bounded node count and valid start / حد العقد.
        if (flow?.Nodes is not { Length: >= 2 and <= 40 } nodes || !ValidId(flow.Start)) return "invalid_graph";
        // Map unique IDs using exact ordinal comparison / فهرس العقد.
        var map = new Dictionary<string, Node>(StringComparer.Ordinal);
        // Validate each node individually / تحقق كل عقدة.
        foreach (var n in nodes)
        {
            // Reject duplicate IDs and out-of-range layout / منع التكرار.
            if (n == null || !ValidId(n.Id) || !map.TryAdd(n.Id!, n) || n.X is < 0 or > 3000 || n.Y is < 0 or > 3000)
                return "invalid_node";
            // Allow only implemented node types / أنواع مدعومة فقط.
            if (n.Type is not ("start" or "contains" or "reply" or "wait" or "capture" or "handoff" or "business" or "end")) return "invalid_type";
            // Bound text and require it for prompt/condition/reply / حد النص.
            if ((n.Text?.Length ?? 0) > 2000 || (n.Type is "contains" or "reply" or "wait" && string.IsNullOrWhiteSpace(n.Text))) return "invalid_text";
            // Only capture or condition nodes may name variables / قيد المتغير.
            if ((n.Type == "capture" && !ValidVariable(n.Variable)) || (n.Variable != null && (n.Type is not ("capture" or "contains") || !ValidVariable(n.Variable)))) return "invalid_variable";
            // Condition mode must be a recognized value / قيد المطابقة.
            if (n.MatchMode != null && (n.Type != "contains" || n.MatchMode is not ("contains" or "equals" or "starts_with")))
                return "invalid_match_mode";
            // Both condition branches must be present / فرعا الشرط.
            if (n.Type == "contains" && (!ValidId(n.Next) || !ValidId(n.Otherwise))) return "invalid_branch";
            // Single-successor nodes cannot have an else branch / انتقال واحد.
            if (n.Type is "start" or "reply" or "wait" or "capture" && (!ValidId(n.Next) || !string.IsNullOrEmpty(n.Otherwise))) return "invalid_next";
            // Business requests are terminal, require a named source and operation, and never execute I/O in the evaluator.
            // طلب مالي ينتهي بتحويل للموظف؛ لا اتصال خارجي داخل المحرك.
            if (n.Type == "business" ? (!BusinessReadRegistry.ValidSourceAlias(n.BusinessSource) || !BusinessReadRegistry.TryKind(n.BusinessOperation,out _)) : (n.BusinessSource != null || n.BusinessOperation != null)) return "invalid_business_request";
            // Terminal nodes cannot link onward / عقدة نهائية.
            if (n.Type is "end" or "handoff" or "business" && (!string.IsNullOrEmpty(n.Next) || !string.IsNullOrEmpty(n.Otherwise))) return "invalid_end";
        }
        // Bound memory footprint / ثمانية متغيرات كحد أقصى.
        if (nodes.Where(n => n.Type == "capture").Select(n => n.Variable).Distinct().Count() > 8) return "too_many_variables";
        // Require exactly one valid start node / بداية واحدة.
        if (!map.TryGetValue(flow.Start!, out var start) || start.Type != "start" || nodes.Count(n => n.Type == "start") != 1) return "invalid_start";
        // Track recursion stack and visited nodes separately / كشف الدورات.
        var visiting = new HashSet<string>(); var visited = new HashSet<string>();
        // Depth-first graph verification / فحص المسارات.
        bool Walk(string? id)
        {
            // No edge means terminal success / انتهاء الفرع.
            if (string.IsNullOrEmpty(id)) return true;
            // Missing target or recursion cycle is invalid / منع الدورة والرابط المفقود.
            if (!map.TryGetValue(id, out var node) || visiting.Contains(id)) return false;
            // Previously verified shared path is valid / مسار سبق التحقق منه.
            if (visited.Contains(id)) return true;
            // Mark current recursion stack / تحديد العقدة قيد الفحص.
            visiting.Add(id);
            // Validate both edges / تحقق الفرعين.
            if (!Walk(node.Next) || !Walk(node.Otherwise)) return false;
            // Complete the verified node / إنهاء فحص العقدة.
            visiting.Remove(id); visited.Add(id); return true;
        }
        // Require all nodes reachable from start / لا عقد معزولة.
        return Walk(flow.Start) && visited.Count == nodes.Length ? null : "cycle_or_unreachable_node";
    }


    // Ordinal case-insensitive comparison independent of server locale / مطابقة مستقلة عن اللغة.
    private static bool Matches(Node node, string input) => node.MatchMode switch
    {
        // Trim outer whitespace for exact comparison / مطابقة كاملة.
        "equals" => string.Equals(input.Trim(), node.Text!.Trim(), StringComparison.OrdinalIgnoreCase),
        // Ignore leading input whitespace for prefix comparison / مطابقة البداية.
        "starts_with" => input.TrimStart().StartsWith(node.Text!.Trim(), StringComparison.OrdinalIgnoreCase),
        // Preserve legacy contains behavior / توافق سابق.
        _ => input.Contains(node.Text!, StringComparison.OrdinalIgnoreCase)
    };

    // Evaluate validated graph without I/O or persistence / تنفيذ نقي.
    public static Evaluation Evaluate(Definition flow, string input, string? resumeAt = null, IReadOnlyDictionary<string,string>? previous = null)
    {
        // Fast lookup by node ID / فهرس العقد.
        var map = flow.Nodes!.ToDictionary(n => n.Id!);
        // Collect diagnostics and replies / مسار وردود.
        var path = new List<string>(); var replies = new List<string>();
        // Fresh memory per evaluation / ذاكرة مستقلة.
        var variables = new Dictionary<string,string>(StringComparer.Ordinal);
        // Only names declared by capture nodes may be restored / متغيرات معلنة.
        var allowed = flow.Nodes!.Where(n => n.Type == "capture").Select(n => n.Variable!).ToHashSet(StringComparer.Ordinal);
        // Restore memory only when resuming / استعادة عند الاستئناف.
        if (resumeAt != null && previous != null)
            // Revalidate stored values / تحقق القيم.
            foreach (var item in previous)
                // Enforce count and size limits / حدود الذاكرة.
                if (allowed.Contains(item.Key) && item.Value is { Length: <= 500 } && variables.Count < 8)
                    // Copy approved value / نسخ القيمة.
                    variables[item.Key] = item.Value;
        // One-pass replacement; captured text is never executed / استبدال نصي فقط.
        string Expand(string text) => System.Text.RegularExpressions.Regex.Replace(text,
            @"\{\{([A-Za-z][A-Za-z0-9_]{0,23})\}\}", match =>
                variables.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
        // Resume existing wait or start at entry / نقطة البدء.
        string? cursor = resumeAt ?? flow.Start;
        // Hard step cap even after validation / حد التنفيذ.
        for (int i = 0; cursor != null && i < 40; i++)
        {
            // Record each visited node / تسجيل المسار.
            var node = map[cursor]; path.Add(cursor);
            // Terminate normally / نهاية.
            if (node.Type == "end") break;
            // Stop automation and discard prior replies in this turn / تحويل بشري.
            if (node.Type == "handoff") return new(path, new(), null, true);
            // Transfer only approved operation metadata; no customer reference, balances or payment data.
            if (node.Type == "business") return new(path, new(), null, true, null, node.BusinessSource, node.BusinessOperation);
            // Save bounded current input as a variable / حفظ الإجابة.
            if (node.Type == "capture") { var value = input.Trim(); variables[node.Variable!] = value.Length <= 500 ? value : value[..500]; }
            // Return prompt and next cursor; caller persists state / انتظار الإجابة.
            if (node.Type == "wait") { replies.Add(Expand(node.Text!)); return new(path, replies, node.Next, false, variables); }
            // Append expanded text / إضافة الرد.
            if (node.Type == "reply") replies.Add(Expand(node.Text!));
            // Branch on current input or saved variable; missing value is non-match / اختيار الفرع.
            cursor = node.Type == "contains" && !(node.Variable == null ? Matches(node, input) : variables.TryGetValue(node.Variable, out var stored) && Matches(node, stored))
                ? node.Otherwise : node.Next;
        }
        // Return completed path and replies without a resume cursor / نتيجة نهائية.
        return new(path, replies, null, false, variables);
    }
}
