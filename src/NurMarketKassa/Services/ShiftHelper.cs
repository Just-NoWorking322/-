using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Смена (construction/shifts) — как _pick_open_shift_id_from_list и статусы в main.py.</summary>
internal static class ShiftHelper
{
    public static string? PickOpenShiftId(JsonElement shiftsPayload, string? cashboxId)
    {
        var candidates = new List<(JsonElement Row, string Id)>();
        foreach (var row in EnumerateList(shiftsPayload))
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            if (!RowLooksLikeOpenShift(row))
                continue;
            var rid = CartDisplayHelper.TryCartId(row);
            if (string.IsNullOrEmpty(rid))
                continue;
            candidates.Add((row, rid));
        }

        if (candidates.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(cashboxId))
        {
            foreach (var (row, rid) in candidates)
            {
                if (RowMatchesCashbox(row, cashboxId))
                    return rid;
            }
        }

        return candidates[0].Id;
    }

    private static bool RowMatchesCashbox(JsonElement row, string cashboxId)
    {
        if (row.TryGetProperty("cashbox", out var cb))
        {
            if (cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("id", out var cid))
            {
                var s = JsonScalar(cid);
                if (!string.IsNullOrEmpty(s) && string.Equals(s, cashboxId, StringComparison.Ordinal))
                    return true;
            }
            else
            {
                var s = JsonScalar(cb);
                if (!string.IsNullOrEmpty(s) && string.Equals(s, cashboxId, StringComparison.Ordinal))
                    return true;
            }
        }

        if (row.TryGetProperty("cashbox_id", out var cbi))
        {
            var s = JsonScalar(cbi);
            if (!string.IsNullOrEmpty(s) && string.Equals(s, cashboxId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool RowLooksLikeOpenShift(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return false;

        if (TruthyBool(row, "is_open"))
            return true;

        if (row.TryGetProperty("status", out var st))
        {
            if (IsOpenStatusString(st))
                return true;
        }

        if (row.TryGetProperty("state", out var state))
            return IsOpenStatusString(state);

        return false;
    }

    private static bool IsOpenStatusString(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String)
            return false;
        var s = v.GetString()?.Trim().ToLowerInvariant() ?? "";
        return s is "open" or "active" or "opened" or "in_progress";
    }

    private static bool TruthyBool(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return false;
        if (v.ValueKind == JsonValueKind.True)
            return true;
        if (v.ValueKind == JsonValueKind.False)
            return false;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim().ToLowerInvariant();
            return s is "1" or "true" or "yes" or "on";
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && Math.Abs(d) > double.Epsilon;
    }

    private static IEnumerable<JsonElement> EnumerateList(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
                yield return el;
            yield break;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("results", out var r) &&
            r.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in r.EnumerateArray())
                yield return el;
        }
    }

    private static string? JsonScalar(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
}
