using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);
var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
    ?? throw new InvalidOperationException("BOT_TOKEN is required");
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException("DATABASE_URL is required");
builder.Services.AddSingleton(new MySqlDataSourceBuilder(connectionString).Build());
var allowedOrigin = Environment.GetEnvironmentVariable("MINIAPP_ORIGIN");
if (!string.IsNullOrWhiteSpace(allowedOrigin))
    builder.Services.AddCors(o => o.AddPolicy("miniapp", p => p.WithOrigins(allowedOrigin).AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
if (!string.IsNullOrWhiteSpace(allowedOrigin)) app.UseCors("miniapp");

static long? VerifyTelegram(string? initData, string token)
{
    if (string.IsNullOrWhiteSpace(initData) || initData.Length > 8192) return null;
    var pairs = initData.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
        .Select(x => (Key: Uri.UnescapeDataString(x[0].Replace("+", " ")), Value: Uri.UnescapeDataString(x[1].Replace("+", " ")))).ToList();
    if (pairs.GroupBy(x => x.Key).Any(x => x.Count() > 1)) return null;
    var hash = pairs.FirstOrDefault(x => x.Key == "hash").Value;
    var auth = pairs.FirstOrDefault(x => x.Key == "auth_date").Value;
    var user = pairs.FirstOrDefault(x => x.Key == "user").Value;
    if (hash is null || auth is null || user is null || !long.TryParse(auth, out var unix)) return null;
    var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix;
    if (age < -60 || age > 3600) return null;
    var check = string.Join("\n", pairs.Where(x => x.Key != "hash" && x.Key != "signature")
        .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value));
    var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(token));
    var actual = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(check));
    byte[] expected;
    try { expected = Convert.FromHexString(hash); } catch (FormatException) { return null; }
    if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return null;
    using var doc = JsonDocument.Parse(user);
    return doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : null;
}

long? Authorized(HttpRequest req) => VerifyTelegram(req.Headers["X-Telegram-Init-Data"].ToString(), token);
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/me", (HttpRequest req) => Authorized(req) is long id
    ? Results.Ok(new { id }) : Results.Unauthorized());

app.MapGet("/api/channels", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is null) return Results.Unauthorized();
    var search = req.Query["search"].ToString().Trim();
    if (search.Length > 80) return Results.BadRequest();
    var category = req.Query["category"].ToString();
    if (category.Length > 32) return Results.BadRequest();
    var limit = int.TryParse(req.Query["limit"], out var n) ? Math.Clamp(n, 1, 30) : 20;
    await using var cmd = db.CreateCommand("""
        SELECT telegram_id,title,category,ad_price_rub,ad_description,username
        FROM channels WHERE is_listed=TRUE
          AND (@category='' OR category=@category)
          AND (@search='' OR title LIKE @pattern)
        ORDER BY connected_at DESC LIMIT @limit
        """);
    cmd.Parameters.AddWithValue("@category", category);
    cmd.Parameters.AddWithValue("@search", search);
    cmd.Parameters.AddWithValue("@pattern", "%" + search + "%");
    cmd.Parameters.AddWithValue("@limit", limit);
    var channels = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        channels.Add(new {
            id = reader.GetInt64(0), title = reader.GetString(1), category = reader.GetString(2),
            price = reader.GetDecimal(3), description = reader.IsDBNull(4) ? "" : reader.GetString(4),
            username = reader.IsDBNull(5) ? null : reader.GetString(5)
        });
    return Results.Ok(channels);
});
app.MapGet("/api/orders", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT a.id,c.title,a.price_rub,a.status,a.created_at
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        WHERE a.advertiser_id=@user OR a.owner_id=@user
        ORDER BY a.created_at DESC LIMIT 50
        """);
    cmd.Parameters.AddWithValue("@user", userId);
    var orders = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        orders.Add(new { id = reader.GetInt64(0), channel = reader.GetString(1),
            price = reader.GetDecimal(2), status = reader.GetString(3), createdAt = reader.GetDateTime(4) });
    return Results.Ok(orders);
});
app.MapGet("/api/my-channels", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT telegram_id,title,is_listed,ad_price_rub FROM channels
        WHERE connected_by=@user ORDER BY connected_at DESC LIMIT 50
        """);
    cmd.Parameters.AddWithValue("@user", userId);
    var channels = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        channels.Add(new { id = reader.GetInt64(0), title = reader.GetString(1),
            listed = reader.GetBoolean(2), price = reader.GetDecimal(3) });
    return Results.Ok(channels);
});
app.Run();
