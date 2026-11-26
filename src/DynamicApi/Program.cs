using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using DynamicApi;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.IdentityModel.Tokens;

var root = AppContext.BaseDirectory;
#if DEBUG
// F5/dotnet run: edit the source schema directly, not a copy under bin.
if (Environment.GetEnvironmentVariable("DYNAMICAPI_USE_SOURCE_DATA") == "true")
{
    var projectDirectory = System.Reflection.CustomAttributeExtensions
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SchemaRegistry).Assembly)
        .FirstOrDefault(x => x.Key == "DynamicApiProjectDirectory")?.Value;
    if (!string.IsNullOrWhiteSpace(projectDirectory) && File.Exists(Path.Combine(projectDirectory, "schema.json")))
        root = projectDirectory;
}
#endif
var port = 5050;
var openBrowser = true;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-dir" when i + 1 < args.Length: root = Path.GetFullPath(args[++i]); break;
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p is >= 1024 and <= 65535: port = p; i++; break;
        case "--no-browser": openBrowser = false; break;
        default: Console.Error.WriteLine("Usage: DynamicApi [--data-dir PATH] [--port 5050] [--no-browser]"); return;
    }
}
var appData = Path.Combine(root, "App_Data");
var recordsFolder = Path.Combine(appData, "Data");
var uploadFolder = Path.Combine(appData, "Uploads");
Directory.CreateDirectory(recordsFolder);
Directory.CreateDirectory(uploadFolder);
// Reject a second writer process using the same data folder.
using var processLock = new FileStream(Path.Combine(appData, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, port); o.Limits.MaxRequestBodySize = Uploads.MaxBody; });
builder.Services.Configure<FormOptions>(o => { o.MultipartBodyLengthLimit = Uploads.MaxTotal; o.ValueLengthLimit = 1024 * 1024; o.ValueCountLimit = 512; });
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(IsLocalOrigin).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Location")));
var key = new SymmetricSecurityKey(Users.SigningKey(appData));
const string issuer = "DynamicApi";
const string audience = "DynamicApi.Frontend";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = issuer,
        ValidateAudience = true, ValidAudience = audience,
        ValidateLifetime = true, RequireExpirationTime = true,
        ValidateIssuerSigningKey = true, IssuerSigningKey = key, RequireSignedTokens = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256], ClockSkew = TimeSpan.Zero,
        NameClaimType = "username"
    };
});
var app = builder.Build();
var registry = new SchemaRegistry(Path.Combine(root, "schema.json"), app.Logger);
registry.LoadInitial();
var users = new Users(appData);
users.Seed(Path.Combine(root, "users.json"));
var store = new Store(recordsFolder);
var uploads = new Uploads(uploadFolder);

app.Use(async (ctx, next) =>
{
    try
    {
        // Prevent cross-site browser writes and DNS rebinding to the loopback server.
        if (ctx.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]")) throw new ApiError(403, "Only local hosts are allowed.");
        if (ctx.Request.Headers.TryGetValue("Origin", out var origin) && (origin.Count != 1 || !IsLocalOrigin(origin[0]!)))
            throw new ApiError(403, "Only localhost frontend origins are allowed.");
        await next(ctx);
    }
    catch (Exception ex) when (!ctx.Response.HasStarted)
    {
        var (status, message) = ex switch
        {
            ApiError e => (e.Status, e.Message),
            BadHttpRequestException e => (e.StatusCode, "Invalid request or request too large."),
            JsonException => (400, "Invalid JSON."),
            OperationCanceledException when ctx.RequestAborted.IsCancellationRequested => (499, "Request cancelled."),
            _ => (500, "Internal server error. Check the console.")
        };
        if (status == 500) app.Logger.LogError(ex, "Unhandled request failure");
        if (ctx.RequestAborted.IsCancellationRequested) return;
        if (status == 401) ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { message });
    }
});
app.UseStatusCodePages(async c =>
{
    var response = c.HttpContext.Response;
    await response.WriteAsJsonAsync(new { message = response.StatusCode switch { 404 => "Endpoint not found.", 405 => "Method not allowed.", _ => "Request failed." } });
});
app.UseCors();
app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    // Capture exactly one snapshot for routing, authorization, validation and output projection.
    var snapshot = registry.Current;
    ctx.Items["schema"] = snapshot;
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var path = (ctx.Request.Path.Value ?? "").TrimEnd('/');
        var publicAuth = path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/auth/register", StringComparison.OrdinalIgnoreCase);
        if (!publicAuth && (snapshot.Auth || path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)))
        {
            if (ctx.User.Identity?.IsAuthenticated != true || !long.TryParse(ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id))
                throw new ApiError(401, "Bearer token required or invalid.");
            _ = users.ById(id);
        }
    }
    await next(ctx);
});
app.UseSwaggerUI(o => { o.SwaggerEndpoint("/swagger/v1/swagger.json", "DynamicApi v1"); o.DocumentTitle = "DynamicApi"; });
app.MapGet("/", () => Results.Redirect("/swagger/index.html"));
app.MapGet("/swagger/v1/swagger.json", (HttpContext ctx) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    return Results.Json(OpenApi.Build(GetSchema(ctx)));
});
app.MapPost("/api/auth/register", async (HttpContext ctx) =>
{
    var user = users.Register(await ReadCredentials(ctx));
    return Results.Json(Users.Public(user), statusCode: 201);
});
app.MapPost("/api/auth/login", async (HttpContext ctx) =>
{
    var user = users.Login(await ReadCredentials(ctx));
    var now = DateTime.UtcNow; var expires = now.AddHours(8);
    var jwt = new JwtSecurityToken(issuer, audience,
        [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString(CultureInfo.InvariantCulture)), new Claim("username", user.Username), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))],
        now, expires, new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
    return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(jwt), tokenType = "Bearer", expiresIn = 28800, expiresAt = expires, user = Users.Public(user) });
});
app.MapGet("/api/auth/me", (HttpContext ctx) => Results.Ok(Users.Public(users.ById(long.Parse(ctx.User.FindFirstValue(JwtRegisteredClaimNames.Sub)!, CultureInfo.InvariantCulture)))));
app.MapGet("/api/files/{filename}", (HttpContext ctx, string filename) => uploads.Download(filename, ctx.Response));
app.MapGet("/api/{resource}", (HttpContext ctx, string resource) =>
{
    var r = Resolve(ctx, resource); return Results.Ok(Values.Query(r, store.List(r), ctx.Request.Query));
});
app.MapGet("/api/{resource}/{id:long}", (HttpContext ctx, string resource, long id) =>
{
    var r = Resolve(ctx, resource); return Results.Ok(Store.Project(r, store.Get(r, id)));
});
app.MapPost("/api/{resource}", async (HttpContext ctx, string resource) =>
{
    var r = Resolve(ctx, resource);
    using var input = await uploads.ReadAsync(ctx.Request, r, ctx.RequestAborted);
    var record = store.Save(r, null, input.Data, false, input.Paths); input.Committed = true;
    return Results.Created($"/api/{r.Name}/{record["id"]!.GetValue<long>()}", Store.Project(r, record));
});
app.MapMethods("/api/{resource}/{id:long}", ["PUT", "PATCH"], async (HttpContext ctx, string resource, long id) =>
{
    var r = Resolve(ctx, resource);
    using var input = await uploads.ReadAsync(ctx.Request, r, ctx.RequestAborted);
    var record = store.Save(r, id, input.Data, HttpMethods.IsPatch(ctx.Request.Method), input.Paths); input.Committed = true;
    return Results.Ok(Store.Project(r, record));
});
app.MapDelete("/api/{resource}/{id:long}", (HttpContext ctx, string resource, long id) =>
{
    var r = Resolve(ctx, resource); store.Delete(r, id); return Results.NoContent();
});
var url = $"http://127.0.0.1:{port}/swagger/index.html";
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Swagger: {url}\nEdit schema: {Path.Combine(root, "schema.json")}\nPress Ctrl+C to stop.");
    if (openBrowser && OperatingSystem.IsWindows())
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { app.Logger.LogWarning("Cannot open browser: {Message}", ex.Message); }
});
var watcher = registry.WatchAsync(app.Lifetime.ApplicationStopping);
await app.RunAsync();
await watcher;

static bool IsLocalOrigin(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") && (uri.Host is "localhost" or "127.0.0.1" or "[::1]") && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
static Schema GetSchema(HttpContext ctx) => (Schema)ctx.Items["schema"]!;
static Resource Resolve(HttpContext ctx, string name) => GetSchema(ctx).Resources.TryGetValue(name, out var r) ? r : throw new ApiError(404, "Resource not found.");
static async Task<Credentials> ReadCredentials(HttpContext ctx)
{
    if (!ctx.Request.HasJsonContentType()) throw new ApiError(415, "Use application/json.");
    if (ctx.Request.ContentLength > 8192) throw new ApiError(413, "Credentials payload too large.");
    var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 8192;
    return await ctx.Request.ReadFromJsonAsync<Credentials>(cancellationToken: ctx.RequestAborted) ?? throw new ApiError(400, "username and password are required.");
}
