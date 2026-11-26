using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicApi;

internal sealed record Field(string Name, string Type, bool Required = false);
internal sealed record Resource(string Name, IReadOnlyDictionary<string, Field> Fields)
{
    public bool HasFiles => Fields.Values.Any(f => f.Type is "file" or "files");
}
internal sealed record Schema(bool Auth, IReadOnlyDictionary<string, Resource> Resources);
internal sealed class ApiError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

// A whole immutable-by-convention snapshot is swapped only after successful parsing.
// Polling detects atomic editor saves, missed watcher events and temporary invalid files.
internal sealed class SchemaRegistry(string path, ILogger logger)
{
    private Schema? current;
    private string? lastAttempt;
    public Schema Current => Volatile.Read(ref current) ?? throw new InvalidOperationException("Schema not loaded.");
    public void LoadInitial()
    {
        var text = File.ReadAllText(path);
        current = Parse(text);
        lastAttempt = text;
    }
    public async Task WatchAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(path, ct);
                    if (text == lastAttempt) continue;
                    lastAttempt = text;
                    var next = Parse(text);
                    Volatile.Write(ref current, next);
                    logger.LogInformation("Schema reloaded: {Count} resources, auth={Auth}", next.Resources.Count, next.Auth);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    logger.LogWarning("Schema reload rejected; previous schema remains active: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public static Schema Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Schema must be an object.");
        var auth = false;
        var resources = new Dictionary<string, Resource>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in root.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new InvalidDataException($"Duplicate setting: {p.Name}");
            if (p.Name == "auth")
            {
                if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("auth must be true or false.");
                auth = p.Value.GetBoolean(); continue;
            }
            if (!Regex.IsMatch(p.Name, "^[a-z][a-z0-9_-]{0,63}$") || p.Name is "users" or "files" or "auth" or "con" or "prn" or "aux" or "nul" || Regex.IsMatch(p.Name, "^(com|lpt)[0-9]$"))
                throw new InvalidDataException($"Invalid or reserved resource name: {p.Name}");
            if (p.Value.ValueKind != JsonValueKind.Object || !p.Value.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{p.Name}: properties object required.");
            if (p.Value.EnumerateObject().Count() != 1) throw new InvalidDataException($"{p.Name}: only properties is supported.");
            var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
            var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in props.EnumerateObject())
            {
                if (!Regex.IsMatch(f.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$") || f.Name.Equals("id", StringComparison.OrdinalIgnoreCase) || !fieldNames.Add(f.Name))
                    throw new InvalidDataException($"Invalid or duplicate property: {p.Name}.{f.Name}");
                if (f.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{f.Name}: type must be a string.");
                var raw = f.Value.GetString()!;
                var required = raw.EndsWith('!');
                var type = required ? raw[..^1] : raw;
                if (type is not ("string" or "number" or "integer" or "boolean" or "date" or "datetime" or "object" or "array" or "file" or "files"))
                    throw new InvalidDataException($"Unknown type: {raw}");
                fields.Add(f.Name, new(f.Name, type, required));
            }
            resources.Add(p.Name, new(p.Name, fields));
        }
        return new(auth, resources);
    }
}
