using System.Text.Json;

namespace Ymuse.Api;

/// <summary>Мелкие помощники для разбора рыхлого JSON Яндекс Музыки.</summary>
static class J
{
    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static string? StrAny(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var s = Str(e, n);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    public static long? Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p)) return p;
        return null;
    }

    public static int? Int(JsonElement e, string name) => (int?)Long(e, name);

    public static bool Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.Number && v.GetRawText() == "1");
    }

    public static JsonElement Obj(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            return v;
        return default;
    }

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in v.EnumerateArray()) yield return item;
    }

    public static string? Url(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var u = uri.Trim();
        if (u.StartsWith("http://") || u.StartsWith("https://")) return u;
        return "https://" + u;
    }

    public static string? Cover(JsonElement e, string size = "400x400")
    {
        var direct = StrAny(e, "coverUri", "imageUrl");
        if (direct != null) return Url(direct.Replace("%%", size));
        var cover = Obj(e, "cover");
        if (cover.ValueKind == JsonValueKind.Object)
        {
            var uri = StrAny(cover, "uri", "imageUrl");
            if (uri != null) return Url(uri.Replace("%%", size));
        }
        var og = StrAny(e, "ogImage");
        if (og != null) return Url(og.Replace("%%", size));
        return null;
    }
}
