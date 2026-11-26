using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

namespace DynamicApi;

internal sealed class Input : IDisposable
{
    public JsonObject Data { get; } = new();
    public HashSet<string> Paths { get; } = new(StringComparer.Ordinal);
    public List<string> DiskPaths { get; } = [];
    public bool Committed { get; set; }
    public void Dispose()
    {
        if (Committed) return;
        foreach (var path in DiskPaths) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
internal sealed class Uploads(string folder)
{
    public const long MaxFile = 20L * 1024 * 1024;
    public const long MaxTotal = 50L * 1024 * 1024;
    public const long MaxBody = 64L * 1024 * 1024;
    private readonly FileExtensionContentTypeProvider types = new();
    public async Task<Input> ReadAsync(HttpRequest request, Resource resource, CancellationToken ct)
    {
        var input = new Input();
        try
        {
            if (request.HasJsonContentType())
            {
                using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ApiError(400, "A JSON object is required.");
                RejectDuplicates(doc.RootElement);
                foreach (var p in doc.RootElement.EnumerateObject()) input.Data.Add(p.Name, JsonNode.Parse(p.Value.GetRawText()));
                return input;
            }
            if (request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
                throw new ApiError(415, "Use application/json or multipart/form-data.");
            if (!resource.HasFiles) throw new ApiError(415, "This resource has no file fields; use application/json.");
            IFormCollection form;
            try { form = await request.ReadFormAsync(ct); }
            catch (InvalidDataException ex) { throw new ApiError(ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase) ? 413 : 400, "Invalid multipart form or form limit exceeded."); }
            foreach (var (key, value) in form)
            {
                if (!resource.Fields.TryGetValue(key, out var f)) throw new ApiError(400, $"Unknown property: {key}");
                if (value.Count != 1) throw new ApiError(400, $"Only one value for {key} is allowed.");
                // Empty strings on file inputs mean no new file; use JSON null or [] to clear explicitly.
                if (f.Type is "file" or "files")
                {
                    if (!string.IsNullOrEmpty(value[0])) throw new ApiError(400, $"{key}: use a file input, not text.");
                    continue;
                }
                input.Data.Add(key, Values.FromText(f, value[0] ?? ""));
            }
            if (form.Files.Count > 50 || form.Files.Sum(x => x.Length) > MaxTotal) throw new ApiError(413, "Maximum 50 files and 50 MiB total.");
            foreach (var group in form.Files.GroupBy(f => f.Name, StringComparer.Ordinal))
            {
                if (!resource.Fields.TryGetValue(group.Key, out var field) || field.Type is not ("file" or "files")) throw new ApiError(400, $"Unknown file property: {group.Key}");
                if (field.Type == "file" && group.Count() != 1) throw new ApiError(400, $"{group.Key} accepts one file.");
                var urls = new JsonArray();
                foreach (var file in group)
                {
                    if (file.Length == 0) throw new ApiError(400, "Empty files are not accepted.");
                    if (file.Length > MaxFile) throw new ApiError(413, "Maximum file size is 20 MiB.");
                    var original = Path.GetFileName(file.FileName.Replace('\\', '/'));
                    var ext = Path.GetExtension(original).ToLowerInvariant();
                    if (!Regex.IsMatch(ext, @"^\.[a-z0-9]{1,10}$")) ext = "";
                    var filename = Guid.NewGuid().ToString("N") + ext;
                    var diskPath = Path.Combine(folder, filename);
                    Directory.CreateDirectory(folder);
                    await using (var stream = new FileStream(diskPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                    {
                        input.DiskPaths.Add(diskPath);
                        await file.CopyToAsync(stream, ct);
                        await stream.FlushAsync(ct);
                        stream.Flush(flushToDisk: true);
                    }
                    var url = "/api/files/" + filename;
                    input.Paths.Add(url); urls.Add(url);
                }
                input.Data[group.Key] = field.Type == "file" ? urls[0]!.DeepClone() : urls;
            }
            return input;
        }
        catch { input.Dispose(); throw; }
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw new ApiError(400, $"Duplicate property: {p.Name}");
                RejectDuplicates(p.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    public IResult Download(string filename, HttpResponse response)
    {
        if (!Regex.IsMatch(filename, @"^[a-f0-9]{32}(\.[a-z0-9]{1,10})?$")) throw new ApiError(404, "File not found.");
        var path = Path.Combine(folder, filename);
        if (!File.Exists(path)) throw new ApiError(404, "File not found.");
        var ext = Path.GetExtension(filename);
        var inline = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico";
        var contentType = inline && types.TryGetContentType(filename, out var mime) ? mime : "application/octet-stream";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        response.Headers["Cache-Control"] = "no-store";
        return Results.File(path, contentType, fileDownloadName: inline ? null : filename, enableRangeProcessing: true);
    }
}
