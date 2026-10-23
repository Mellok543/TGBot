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

app.MapGet("/api/channels/{channelId:long}/rates", async (long channelId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is null) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT r.id,r.duration_hours,r.price_rub FROM channel_duration_rates r
        JOIN channels c ON c.telegram_id=r.channel_id
        WHERE r.channel_id=@channel AND r.is_active=TRUE AND c.is_listed=TRUE
        ORDER BY r.duration_hours
        """);
    cmd.Parameters.AddWithValue("@channel", channelId);
    var rates = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        rates.Add(new { id = reader.GetInt64(0), hours = reader.GetInt32(1), price = reader.GetDecimal(2) });
    return Results.Ok(rates);
});
app.MapGet("/api/channels/{channelId:long}/availability", async (long channelId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is null) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT scheduled_at FROM ad_requests
        WHERE channel_id=@channel AND scheduled_at>=UTC_TIMESTAMP()
          AND scheduled_at<DATE_ADD(UTC_TIMESTAMP(), INTERVAL 90 DAY)
          AND status IN ('Pending','AwaitingPayment','Scheduled','Publishing','Published')
        ORDER BY scheduled_at LIMIT 200
        """);
    cmd.Parameters.AddWithValue("@channel", channelId);
    var slots = new List<DateTime>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) slots.Add(reader.GetDateTime(0));
    return Results.Ok(slots);
});
app.MapPost("/api/orders", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    var input = await req.ReadFromJsonAsync<CreateOrderInput>(cancellationToken: ct);
    if (input is null || input.ChannelId <= 0 || string.IsNullOrWhiteSpace(input.Text)
        || input.Text.Length > 4096) return Results.BadRequest(new { error = "Текст объявления должен содержать от 1 до 4096 символов" });
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd = new MySqlCommand("""
        INSERT INTO ad_requests (channel_id,advertiser_id,owner_id,content_type,content_text,media_file_id,price_rub,status)
        SELECT c.telegram_id,@buyer,c.connected_by,'text',@text,NULL,c.ad_price_rub,'Draft'
        FROM channels c JOIN users u ON u.telegram_id=c.connected_by
        WHERE c.telegram_id=@channel AND c.connected_by<>@buyer AND c.is_listed=TRUE
          AND ((u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP())
               OR u.created_at>UTC_TIMESTAMP()-INTERVAL 30 DAY)
        """, conn);
    cmd.Parameters.AddWithValue("@buyer", userId);
    cmd.Parameters.AddWithValue("@channel", input.ChannelId);
    cmd.Parameters.AddWithValue("@text", input.Text.Trim());
    if (await cmd.ExecuteNonQueryAsync(ct) != 1) return Results.BadRequest(new { error = "Канал недоступен для заказа" });
    return Results.Ok(new { id = cmd.LastInsertedId, status = "Draft" });
});
app.MapPost("/api/orders/{orderId:long}/schedule", async (long orderId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    var input = await req.ReadFromJsonAsync<ScheduleOrderInput>(cancellationToken: ct);
    if (input is null || input.RateId <= 0 || input.ScheduledAt.Offset != TimeSpan.Zero)
        return Results.BadRequest(new { error = "Укажите тариф и время в UTC" });
    var at = input.ScheduledAt.UtcDateTime;
    await using var cmd = db.CreateCommand("""
        UPDATE ad_requests a
        JOIN channel_duration_rates r ON r.channel_id=a.channel_id AND r.id=@rate AND r.is_active=TRUE
        SET a.status='Pending',a.scheduled_at=@at,a.time_rate_id=r.id,a.price_rub=r.price_rub,
            a.delete_after_hours=r.duration_hours
        WHERE a.id=@order AND a.advertiser_id=@user AND a.status='Draft'
          AND @at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 2 MINUTE)
          AND @at<DATE_ADD(UTC_TIMESTAMP(),INTERVAL 90 DAY)
          AND NOT EXISTS (
            SELECT 1 FROM (SELECT channel_id,scheduled_at,status FROM ad_requests) other
            WHERE other.channel_id=a.channel_id AND other.scheduled_at=@at
              AND other.status IN ('Pending','AwaitingPayment','Scheduled','Publishing','Published'))
        """);
    cmd.Parameters.AddWithValue("@rate", input.RateId);
    cmd.Parameters.AddWithValue("@at", at);
    cmd.Parameters.AddWithValue("@order", orderId);
    cmd.Parameters.AddWithValue("@user", userId);
    if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        return Results.Conflict(new { error = "Время занято, заказ недоступен или срок некорректен" });
    await using var notice = db.CreateCommand("""
        INSERT INTO notification_outbox (recipient_id,order_id,body)
        SELECT owner_id,id,CONCAT('Новая заявка #',id,' на рекламу. Откройте бота для согласования.')
        FROM ad_requests WHERE id=@order AND advertiser_id=@user
        """);
    notice.Parameters.AddWithValue("@order", orderId);
    notice.Parameters.AddWithValue("@user", userId);
    // A failed notification must not undo an accepted booking.
    try { await notice.ExecuteNonQueryAsync(ct); } catch (MySqlException) { }
    return Results.Ok(new { id = orderId, status = "Pending" });
});
app.Run();

record CreateOrderInput(long ChannelId, string Text);
record ScheduleOrderInput(long RateId, DateTimeOffset ScheduledAt);
