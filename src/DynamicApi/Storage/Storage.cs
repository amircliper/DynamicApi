using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace DynamicApi;

internal static class Disk
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static void Write(string path, object data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, Json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Invalid data file: {path}");
}
internal sealed class Records
{
    public long NextId { get; set; } = 1;
    public List<JsonObject> Items { get; set; } = [];
}
internal sealed class Store(string folder)
{
    private readonly object gate = new();
    private string PathFor(Resource r) => Path.Combine(folder, r.Name + ".json");
    private Records Read(Resource r) => File.Exists(PathFor(r)) ? Disk.Read<Records>(PathFor(r)) : new();
    public List<JsonObject> List(Resource r) { lock (gate) return Read(r).Items; }
    public JsonObject Get(Resource r, long id)
    {
        lock (gate) return Find(Read(r), id);
    }
    private static JsonObject Find(Records data, long id) => data.Items.FirstOrDefault(x => x["id"]?.GetValue<long>() == id)
        ?? throw new ApiError(404, "Record not found.");
    public JsonObject Save(Resource r, long? id, JsonObject input, bool patch, HashSet<string> uploaded)
    {
        lock (gate)
        {
            var data = Read(r);
            var old = id.HasValue ? Find(data, id.Value) : null;
            var record = old is null ? new JsonObject() : (JsonObject)old.DeepClone();
            // Preserve fields removed from schema on disk. PUT clears omitted current fields.
            if (!patch) foreach (var field in r.Fields.Keys) record.Remove(field);
            foreach (var (key, value) in input)
            {
                if (!r.Fields.TryGetValue(key, out var field)) throw new ApiError(400, $"Unknown or read-only property: {key}");
                Values.Validate(field, value);
                if (field.Type is "file" or "files")
                {
                    var previous = Values.FilePaths(old?[key]).ToHashSet(StringComparer.Ordinal);
                    if (Values.FilePaths(value).Any(p => !previous.Contains(p) && !uploaded.Contains(p)))
                        throw new ApiError(400, $"{key}: upload files using multipart/form-data; existing paths must belong to this record and field.");
                }
                record[key] = value?.DeepClone();
            }
            foreach (var field in r.Fields.Values)
            {
                // Validate the effective current schema, including retained values on PATCH.
                Values.Validate(field, record[field.Name]);
            }
            if (old is null)
            {
                if (data.NextId < 1 || data.NextId == long.MaxValue) throw new InvalidDataException("Invalid or exhausted id sequence.");
                record["id"] = data.NextId++;
                data.Items.Add(record);
            }
            else data.Items[data.Items.IndexOf(old)] = record;
            Disk.Write(PathFor(r), data);
            return record;
        }
    }
    public void Delete(Resource r, long id)
    {
        lock (gate)
        {
            var data = Read(r); data.Items.Remove(Find(data, id)); Disk.Write(PathFor(r), data);
        }
    }
    public static JsonObject Project(Resource r, JsonObject data)
    {
        var result = new JsonObject { ["id"] = data["id"]?.DeepClone() };
        foreach (var f in r.Fields.Keys) if (data.ContainsKey(f)) result[f] = data[f]?.DeepClone();
        return result;
    }
}
internal sealed record User(long Id, string Username, string PasswordHash);
internal sealed record Credentials(string? Username, string? Password);
internal sealed class Users(string folder)
{
    private readonly object gate = new();
    private readonly PasswordHasher<string> hasher = new();
    private string UserPath => Path.Combine(folder, "users.json");
    private List<User> Read() => File.Exists(UserPath) ? Disk.Read<List<User>>(UserPath) : [];
    public void Seed(string seedPath)
    {
        if (!File.Exists(seedPath)) return;
        var seeds = Disk.Read<List<Credentials>>(seedPath);
        lock (gate)
        {
            var users = Read(); var changed = false;
            foreach (var seed in seeds)
            {
                var (name, pass) = Validate(seed);
                if (users.Any(u => u.Username.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                users.Add(NewUser(users, name, pass)); changed = true;
            }
            if (changed) Disk.Write(UserPath, users);
        }
    }
    private static (string, string) Validate(Credentials c)
    {
        var name = c.Username?.Trim(); var pass = c.Password;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || string.IsNullOrEmpty(pass) || pass.Length > 256)
            throw new ApiError(400, "username and password are required (max 100/256 characters).");
        return (name, pass);
    }
    private User NewUser(List<User> users, string name, string pass) => new(users.Count == 0 ? 1 : checked(users.Max(x => x.Id) + 1), name, hasher.HashPassword(name, pass));
    public User Register(Credentials credentials)
    {
        var (name, pass) = Validate(credentials);
        lock (gate)
        {
            var users = Read();
            if (users.Any(u => u.Username.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ApiError(409, "Username already exists.");
            var user = NewUser(users, name, pass); users.Add(user); Disk.Write(UserPath, users); return user;
        }
    }
    public User Login(Credentials credentials)
    {
        var (name, pass) = Validate(credentials);
        lock (gate)
        {
            var user = Read().FirstOrDefault(u => u.Username.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (user is null || hasher.VerifyHashedPassword(user.Username, user.PasswordHash, pass) == PasswordVerificationResult.Failed)
                throw new ApiError(401, "Invalid username or password.");
            return user;
        }
    }
    public User ById(long id) { lock (gate) return Read().FirstOrDefault(u => u.Id == id) ?? throw new ApiError(401, "Unknown user."); }
    public static object Public(User user) => new { user.Id, user.Username };
    public static byte[] SigningKey(string folder)
    {
        var path = Path.Combine(folder, "jwt-key.json");
        if (!File.Exists(path)) Disk.Write(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var key = Convert.FromBase64String(Disk.Read<string>(path));
        if (key.Length < 32) throw new InvalidDataException("Invalid JWT signing key.");
        return key;
    }
}
