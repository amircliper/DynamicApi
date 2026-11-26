using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace DynamicApi;

internal static class Values
{
    public static IEnumerable<string> FilePaths(JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var s)) yield return s;
        else if (value is JsonArray a)
            foreach (var item in a)
                if (item is JsonValue x && x.TryGetValue<string>(out var path)) yield return path;
    }
    private static bool IsFilePath(string path) => System.Text.RegularExpressions.Regex.IsMatch(path, @"^/api/files/[a-f0-9]{32}(\.[a-z0-9]{1,10})?$");
    public static void Validate(Field f, JsonNode? node)
    {
        if (node is null)
        {
            if (f.Required) throw new ApiError(400, $"{f.Name} is required.");
            return;
        }
        var kind = node.GetValueKind();
        var isString = kind == JsonValueKind.String;
        var valid = f.Type switch
        {
            "string" => isString,
            "file" => isString && IsFilePath(node.GetValue<string>()),
            "number" => kind == JsonValueKind.Number && decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            "integer" => kind == JsonValueKind.Number && long.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "date" => isString && DateOnly.TryParseExact(node.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "datetime" => isString && System.Text.RegularExpressions.Regex.IsMatch(node.GetValue<string>(), @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$") && DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "files" => node is JsonArray arr && arr.All(x => x?.GetValueKind() == JsonValueKind.String && IsFilePath(x.GetValue<string>())),
            _ => false
        };
        if (!valid) throw new ApiError(400, $"{f.Name}: expected {f.Type}.");
        if (f.Required && (isString && string.IsNullOrWhiteSpace(node.GetValue<string>()) || node is JsonArray a && a.Count == 0))
            throw new ApiError(400, $"{f.Name} is required and cannot be empty.");
    }
    public static JsonNode? FromText(Field field, string text)
    {
        if (field.Type is "string" or "date" or "datetime" or "file") return JsonValue.Create(text);
        try { return JsonNode.Parse(text); }
        catch (JsonException) { throw new ApiError(400, $"{field.Name}: expected {field.Type}."); }
    }
    public static int Compare(JsonNode? a, JsonNode? b, string type)
    {
        if (a is null) return b is null ? 0 : -1;
        if (b is null) return 1;
        // Old data may have a different type after a hot reload. Use a deterministic fallback.
        if ((type is "integer" or "number") && a.GetValueKind() == JsonValueKind.Number && b.GetValueKind() == JsonValueKind.Number &&
            decimal.TryParse(a.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && decimal.TryParse(b.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return x.CompareTo(y);
        if (type == "datetime" && a.GetValueKind() == JsonValueKind.String && b.GetValueKind() == JsonValueKind.String &&
            DateTimeOffset.TryParse(a.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var da) && DateTimeOffset.TryParse(b.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var db)) return da.CompareTo(db);
        if (a.GetValueKind() == JsonValueKind.String && b.GetValueKind() == JsonValueKind.String)
            return StringComparer.OrdinalIgnoreCase.Compare(a.GetValue<string>(), b.GetValue<string>());
        return StringComparer.Ordinal.Compare(a.ToJsonString(), b.ToJsonString());
    }
    public static string SearchText(JsonNode? value) => value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value?.ToJsonString() ?? "";
    public static object Query(Resource resource, List<JsonObject> data, IQueryCollection query)
    {
        string? Get(string key)
        {
            if (!query.TryGetValue(key, out var v)) return null;
            if (v.Count != 1) throw new ApiError(400, $"Only one {key} value is allowed.");
            return v[0];
        }
        int Int(string key, int fallback, int max)
        {
            var raw = Get(key);
            if (raw is null) return fallback;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > max) throw new ApiError(400, $"{key} must be between 1 and {max}.");
            return n;
        }
        var page = Int("page", 1, int.MaxValue); var size = Int("pageSize", 20, 1000);
        var sort = Get("sort") ?? "id"; var order = Get("order") ?? "asc"; var search = Get("search");
        if (order is not ("asc" or "desc")) throw new ApiError(400, "order must be asc or desc.");
        Field FindField(string name) => name == "id" ? new("id", "integer") : resource.Fields.TryGetValue(name, out var f) ? f : throw new ApiError(400, $"Unknown field: {name}");
        var sortField = FindField(sort);
        if (sortField.Type is "object" or "array" or "file" or "files") throw new ApiError(400, "sort requires a scalar field.");
        IEnumerable<JsonObject> rows = data;
        foreach (var key in query.Keys)
        {
            if (key is "page" or "pageSize" or "sort" or "order" or "search") continue;
            if (!key.StartsWith("filter.", StringComparison.Ordinal)) throw new ApiError(400, $"Unknown query parameter: {key}");
            var f = FindField(key[7..]);
            if (f.Type is "object" or "array" or "file" or "files") throw new ApiError(400, "filter requires a scalar field.");
            var value = FromText(f, Get(key)!); Validate(f, value);
            rows = rows.Where(r => r[f.Name] is not null && Compare(r[f.Name], value, f.Type) == 0);
        }
        if (!string.IsNullOrEmpty(search)) rows = rows.Where(r => resource.Fields.Values.Where(f => f.Type == "string").Any(f => SearchText(r[f.Name]).Contains(search, StringComparison.OrdinalIgnoreCase)));
        var all = rows.ToList();
        // Type-changed legacy data is grouped by JSON kind before comparisons for a total order.
        var comparer = Comparer<JsonNode?>.Create((a,b) => {
            if (a is not null && b is not null && a.GetValueKind() != b.GetValueKind()) return a.GetValueKind().CompareTo(b.GetValueKind());
            return Compare(a,b,sortField.Type);
        });
        var sorted = order == "asc" ? all.OrderBy(x => x[sort], comparer) : all.OrderByDescending(x => x[sort], comparer);
        var skip = (long)(page - 1) * size;
        JsonObject[] items = skip >= all.Count ? [] : sorted.ThenBy(x => x["id"]!.GetValue<long>()).Skip((int)skip).Take(size).Select(x => Store.Project(resource, x)).ToArray();
        return new { items, page, pageSize = size, totalCount = all.Count, totalPages = (int)Math.Ceiling((double)all.Count / size) };
    }
}
