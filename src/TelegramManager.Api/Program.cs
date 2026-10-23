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
builder.Services.AddHttpClient();
builder.Services.AddHostedService<TelegramManager.Api.Publishing.PublicationWorker>();
builder.Services.AddHostedService<TelegramManager.Api.Notifications.NotificationWorker>();
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
    var check = string.Join("\n", pairs.Where(x => x.Key != "hash")
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
    var limit = int.TryParse(req.Query["limit"], out var n) ? Math.Clamp(n, 1, 100) : 100;
    await using var cmd = db.CreateCommand("""
        SELECT c.telegram_id,c.title,c.category,c.ad_price_rub,c.ad_description,c.username,
               (SELECT m.subscribers FROM channel_daily_metrics m
                WHERE m.channel_id=c.telegram_id AND m.subscribers IS NOT NULL
                ORDER BY m.day DESC LIMIT 1) AS subscribers,
               (SELECT COALESCE(SUM(m.posts),0) FROM channel_daily_metrics m
                WHERE m.channel_id=c.telegram_id AND m.day>=UTC_DATE()-INTERVAL 29 DAY) AS posts30d
        FROM channels c WHERE c.is_listed=TRUE
          AND (@category='' OR c.category=@category)
          AND (@search='' OR c.title LIKE @pattern)
        ORDER BY c.connected_at DESC LIMIT @limit
        """);
    cmd.Parameters.Add(new MySqlParameter("@category", category));
    cmd.Parameters.Add(new MySqlParameter("@search", search));
    cmd.Parameters.Add(new MySqlParameter("@pattern", "%" + search + "%"));
    cmd.Parameters.Add(new MySqlParameter("@limit", limit));
    var channels = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        channels.Add(new {
            id = reader.GetInt64(0), title = reader.GetString(1), category = reader.GetString(2),
            price = reader.GetDecimal(3), description = reader.IsDBNull(4) ? "" : reader.GetString(4),
            username = reader.IsDBNull(5) ? null : reader.GetString(5),
            subscribers = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6),
            posts30d = reader.IsDBNull(7) ? 0L : reader.GetInt64(7)
        });
    return Results.Ok(channels);
});
app.MapGet("/api/orders", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT a.id,c.title,a.price_rub,a.status,a.created_at,a.owner_id
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        WHERE a.advertiser_id=@user OR a.owner_id=@user
        ORDER BY a.created_at DESC LIMIT 50
        """);
    cmd.Parameters.Add(new MySqlParameter("@user", userId));
    var orders = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        orders.Add(new { id = reader.GetInt64(0), channel = reader.GetString(1),
            price = reader.GetDecimal(2), status = reader.GetString(3), createdAt = reader.GetDateTime(4), isOwner = reader.GetInt64(5) == userId });
    return Results.Ok(orders);
});
app.MapGet("/api/my-channels", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT telegram_id,title,is_listed,ad_price_rub FROM channels
        WHERE connected_by=@user ORDER BY connected_at DESC LIMIT 50
        """);
    cmd.Parameters.Add(new MySqlParameter("@user", userId));
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
    cmd.Parameters.Add(new MySqlParameter("@channel", channelId));
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
    cmd.Parameters.Add(new MySqlParameter("@channel", channelId));
    var slots = new List<DateTime>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) slots.Add(reader.GetDateTime(0));
    return Results.Ok(slots);
});
app.MapPost("/api/orders", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    var input = await req.ReadFromJsonAsync<CreateOrderInput>(cancellationToken: ct);
    if (input is null || string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 4096)
        return Results.BadRequest(new { error = "Текст объявления должен содержать от 1 до 4096 символов" });
    if (input.ChannelId == 0)
        return Results.BadRequest(new { error = "Некорректный идентификатор канала" });
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd = new MySqlCommand("""
        INSERT INTO ad_requests (channel_id,advertiser_id,owner_id,content_type,content_text,media_file_id,price_rub,status)
        SELECT c.telegram_id,@buyer,c.connected_by,'text',@text,NULL,c.ad_price_rub,'Draft'
        FROM channels c JOIN users u ON u.telegram_id=c.connected_by
        WHERE c.telegram_id=@channel AND c.connected_by<>@buyer AND c.is_listed=TRUE
          AND ((u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP())
               OR u.created_at>UTC_TIMESTAMP()-INTERVAL 30 DAY)
        """, conn);
    cmd.Parameters.Add(new MySqlParameter("@buyer", userId));
    cmd.Parameters.Add(new MySqlParameter("@channel", input.ChannelId));
    cmd.Parameters.Add(new MySqlParameter("@text", input.Text.Trim()));
    if (await cmd.ExecuteNonQueryAsync(ct) != 1)
    {
        await using var reason = new MySqlCommand("SELECT connected_by,is_listed FROM channels WHERE telegram_id=@channel", conn);
        reason.Parameters.Add(new MySqlParameter("@channel", input.ChannelId));
        await using var reader = await reason.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            if (reader.GetInt64(0) == userId) return Results.BadRequest(new { error = "Нельзя заказать рекламу в собственном канале. Для проверки используйте другой Telegram-аккаунт." });
            if (!reader.GetBoolean(1)) return Results.BadRequest(new { error = "Канал скрыт из каталога и недоступен для заказа." });
        }
        return Results.BadRequest(new { error = "Канал недоступен для заказа. Проверьте статус канала и подписку его владельца." });
    }
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
    cmd.Parameters.Add(new MySqlParameter("@rate", input.RateId));
    cmd.Parameters.Add(new MySqlParameter("@at", at));
    cmd.Parameters.Add(new MySqlParameter("@order", orderId));
    cmd.Parameters.Add(new MySqlParameter("@user", userId));
    if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        return Results.Conflict(new { error = "Время занято, заказ недоступен или срок некорректен" });
    await using var notice = db.CreateCommand("""
        INSERT INTO notification_outbox (recipient_id,order_id,body)
        SELECT owner_id,id,CONCAT('Новая заявка #',id,' на рекламу. Откройте бота для согласования.')
        FROM ad_requests WHERE id=@order AND advertiser_id=@user
        """);
    notice.Parameters.Add(new MySqlParameter("@order", orderId));
    notice.Parameters.Add(new MySqlParameter("@user", userId));
    // A failed notification must not undo an accepted booking.
    try { await notice.ExecuteNonQueryAsync(ct); } catch (MySqlException) { }
    return Results.Ok(new { id = orderId, status = "Pending" });
});
// The channel owner may make a decision only once while the request is pending.
// A conditional UPDATE prevents double approval/rejection and cross-account access.
app.MapPost("/api/orders/{orderId:long}/approve", async (long orderId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long ownerId) return Results.Unauthorized();
    if (orderId <= 0) return Results.BadRequest(new { error = "Некорректный заказ" });

    await using var conn = await db.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    await using var update = new MySqlCommand("""
        UPDATE ad_requests
        SET status='AwaitingPayment'
        WHERE id=@order AND owner_id=@owner AND status='Pending'
        """, conn, tx);
    update.Parameters.AddWithValue("@order", orderId);
    update.Parameters.AddWithValue("@owner", ownerId);
    if (await update.ExecuteNonQueryAsync(ct) != 1)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { error = "Заявка уже обработана или недоступна" });
    }

    await using var notify = new MySqlCommand("""
        INSERT INTO notification_outbox (recipient_id,order_id,body)
        SELECT advertiser_id,id,CONCAT('Заявка #',id,' одобрена владельцем канала. Ожидается оплата.')
        FROM ad_requests WHERE id=@order
        """, conn, tx);
    notify.Parameters.AddWithValue("@order", orderId);
    await notify.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { id = orderId, status = "AwaitingPayment" });
});

app.MapPost("/api/orders/{orderId:long}/reject", async (long orderId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long ownerId) return Results.Unauthorized();
    if (orderId <= 0) return Results.BadRequest(new { error = "Некорректный заказ" });

    await using var conn = await db.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    await using var update = new MySqlCommand("""
        UPDATE ad_requests
        SET status='Rejected'
        WHERE id=@order AND owner_id=@owner AND status='Pending'
        """, conn, tx);
    update.Parameters.AddWithValue("@order", orderId);
    update.Parameters.AddWithValue("@owner", ownerId);
    if (await update.ExecuteNonQueryAsync(ct) != 1)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { error = "Заявка уже обработана или недоступна" });
    }

    await using var notify = new MySqlCommand("""
        INSERT INTO notification_outbox (recipient_id,order_id,body)
        SELECT advertiser_id,id,CONCAT('Заявка #',id,' отклонена владельцем канала.')
        FROM ad_requests WHERE id=@order
        """, conn, tx);
    notify.Parameters.AddWithValue("@order", orderId);
    await notify.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new { id = orderId, status = "Rejected" });
});

// Payment endpoints are deliberately test-only. Standard test payments are not escrow.
// The service must be configured with test-shop credentials before this feature is enabled.
app.MapPost("/api/orders/{orderId:long}/payment", async (long orderId, HttpRequest req, MySqlDataSource db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (Authorized(req) is not long buyer) return Results.Unauthorized();
    if (Environment.GetEnvironmentVariable("YOOKASSA_MODE") != "test")
        return Results.Problem("Test payments are disabled", statusCode: 503);
    var shop = Environment.GetEnvironmentVariable("YOOKASSA_SHOP_ID");
    var key = Environment.GetEnvironmentVariable("YOOKASSA_SECRET_KEY");
    if (string.IsNullOrWhiteSpace(shop) || string.IsNullOrWhiteSpace(key))
        return Results.Problem("Test shop not configured", statusCode: 503);
    if (orderId <= 0) return Results.BadRequest();

    await using var conn = await db.OpenConnectionAsync(ct);
    // A database row and unique order lock make repeated taps reuse the same attempt.
    await using var tx = await conn.BeginTransactionAsync(ct);
    await using var lookup = new MySqlCommand("""
        SELECT price_rub FROM ad_requests
        WHERE id=@id AND advertiser_id=@buyer AND status='AwaitingPayment'
        FOR UPDATE
        """, conn, tx);
    lookup.Parameters.AddWithValue("@id", orderId);
    lookup.Parameters.AddWithValue("@buyer", buyer);
    var priceValue = await lookup.ExecuteScalarAsync(ct);
    if (priceValue is null) { await tx.RollbackAsync(ct); return Results.Conflict(new { error = "Заказ недоступен для оплаты" }); }
    var price = Convert.ToDecimal(priceValue);
    if (price < 1m || price > 1000000m) { await tx.RollbackAsync(ct); return Results.BadRequest(new { error = "Некорректная сумма" }); }

    await using var existing = new MySqlCommand("""
        SELECT id,idempotence_key,provider_payment_id,confirmation_url,status
        FROM reklio_payment_attempts
        WHERE order_id=@id AND mode='test' AND status IN ('Created','Pending','Succeeded')
        ORDER BY id DESC LIMIT 1 FOR UPDATE
        """, conn, tx);
    existing.Parameters.AddWithValue("@id", orderId);
    long attemptId;
    string idempotence;
    string? paymentId;
    string? confirmation;
    string attemptStatus;
    await using (var reader = await existing.ExecuteReaderAsync(ct))
    {
        if (await reader.ReadAsync(ct))
        {
            attemptId = reader.GetInt64(0);
            idempotence = reader.GetString(1);
            paymentId = reader.IsDBNull(2) ? null : reader.GetString(2);
            confirmation = reader.IsDBNull(3) ? null : reader.GetString(3);
            attemptStatus = reader.GetString(4);
        }
        else
        {
            attemptId = 0; idempotence = Guid.NewGuid().ToString();
            paymentId = null; confirmation = null; attemptStatus = "Created";
        }
    }
    if (attemptId == 0)
    {
        await using var insert = new MySqlCommand("""
            INSERT INTO reklio_payment_attempts
              (order_id,advertiser_id,amount_rub,idempotence_key,status,mode)
            VALUES (@id,@buyer,@price,@key,'Created','test')
            """, conn, tx);
        insert.Parameters.AddWithValue("@id", orderId);
        insert.Parameters.AddWithValue("@buyer", buyer);
        insert.Parameters.AddWithValue("@price", price);
        insert.Parameters.AddWithValue("@key", idempotence);
        await insert.ExecuteNonQueryAsync(ct);
        attemptId = insert.LastInsertedId;
    }
    // Keep the order row locked until the provider returns and the attempt is saved.
    // This serializes simultaneous clicks across API instances.
    if (attemptStatus == "Succeeded")
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new { error = "Платёж уже подтверждён" });
    }
    if (paymentId is not null && confirmation is not null)
    {
        await tx.CommitAsync(ct);
        return Results.Ok(new { confirmationUrl = confirmation });
    }

    // The provider idempotence key is persisted BEFORE the network request.
    // Do not mark the order paid based on this response.
    var client = clients.CreateClient();
    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.yookassa.ru/v3/payments");
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(shop + ":" + key)));
    request.Headers.Add("Idempotence-Key", idempotence);
    request.Content = System.Net.Http.Json.JsonContent.Create(new {
        amount = new { value = price.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), currency = "RUB" },
        capture = true,
        confirmation = new { type = "redirect", return_url = "https://reklio.site/" },
        description = "Тестовая оплата Reklio #" + orderId,
        metadata = new { order_id = orderId.ToString() }
    });
    using var response = await client.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
        return Results.Problem("ЮKassa отклонила создание тестового платежа", statusCode: 502);
    using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    var root = document.RootElement;
    var providerId = root.GetProperty("id").GetString();
    var url = root.GetProperty("confirmation").GetProperty("confirmation_url").GetString();
    if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(url))
        return Results.Problem("Некорректный ответ ЮKassa", statusCode: 502);
    await using var save = new MySqlCommand("""
        UPDATE reklio_payment_attempts
        SET provider_payment_id=@provider,confirmation_url=@url,status='Pending'
        WHERE id=@attempt AND (provider_payment_id IS NULL OR provider_payment_id=@provider)
        """, conn, tx);
    save.Parameters.AddWithValue("@provider", providerId);
    save.Parameters.AddWithValue("@url", url);
    save.Parameters.AddWithValue("@attempt", attemptId);
    if (await save.ExecuteNonQueryAsync(ct) != 1)
        throw new InvalidOperationException("Payment attempt was not saved");
    await tx.CommitAsync(ct);
    return Results.Ok(new { confirmationUrl = url });
});

app.MapPost("/api/orders/{orderId:long}/payment/check", async (long orderId, HttpRequest req, MySqlDataSource db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (Authorized(req) is not long buyer) return Results.Unauthorized();
    if (Environment.GetEnvironmentVariable("YOOKASSA_MODE") != "test") return Results.Problem(statusCode: 503);
    var shop = Environment.GetEnvironmentVariable("YOOKASSA_SHOP_ID");
    var key = Environment.GetEnvironmentVariable("YOOKASSA_SECRET_KEY");
    if (string.IsNullOrWhiteSpace(shop) || string.IsNullOrWhiteSpace(key)) return Results.Problem(statusCode: 503);
    await using var conn = await db.OpenConnectionAsync(ct);
    await using var cmd = new MySqlCommand("""
        SELECT p.id,p.provider_payment_id,p.amount_rub
        FROM reklio_payment_attempts p JOIN ad_requests a ON a.id=p.order_id
        WHERE p.order_id=@id AND a.advertiser_id=@buyer AND p.mode='test'
          AND p.provider_payment_id IS NOT NULL
        ORDER BY p.id DESC LIMIT 1
        """, conn);
    cmd.Parameters.AddWithValue("@id", orderId);
    cmd.Parameters.AddWithValue("@buyer", buyer);
    long attemptId; string providerId; decimal amount;
    await using (var reader = await cmd.ExecuteReaderAsync(ct))
    {
        if (!await reader.ReadAsync(ct)) return Results.NotFound();
        attemptId = reader.GetInt64(0); providerId = reader.GetString(1); amount = reader.GetDecimal(2);
    }
    var client = new TelegramManager.Api.Payments.YooKassaSafeDealClient(clients.CreateClient(), shop, key);
    var payment = await client.GetPaymentAsync(providerId, ct);
    if (payment.Id != providerId || payment.Metadata?.GetValueOrDefault("order_id") != orderId.ToString() ||
        payment.Amount.Currency != "RUB" ||
        !decimal.TryParse(payment.Amount.Value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var actual) || actual != amount)
        return Results.Problem("Данные платежа не совпадают с заказом", statusCode: 502);
    if (payment.Status == "succeeded" && payment.Paid)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var update = new MySqlCommand("""
            UPDATE ad_requests SET status='Scheduled'
            WHERE id=@id AND advertiser_id=@buyer AND status='AwaitingPayment'
            """, conn, tx);
        update.Parameters.AddWithValue("@id", orderId);
        update.Parameters.AddWithValue("@buyer", buyer);
        var transitioned = await update.ExecuteNonQueryAsync(ct);
        if (transitioned == 1)
        {
            await using var notice = new MySqlCommand("""
                INSERT IGNORE INTO notification_outbox (recipient_id,order_id,body)
                SELECT advertiser_id,id,CONCAT('Оплата заказа #',id,' подтверждена. Реклама запланирована.')
                FROM ad_requests WHERE id=@id
                UNION ALL
                SELECT owner_id,id,CONCAT('Заказ #',id,' оплачен. Ожидается публикация.')
                FROM ad_requests WHERE id=@id
                """, conn, tx);
            notice.Parameters.AddWithValue("@id", orderId);
            await notice.ExecuteNonQueryAsync(ct);
        }
        await using var mark = new MySqlCommand("""
            UPDATE reklio_payment_attempts SET status='Succeeded'
            WHERE id=@attempt AND status IN ('Created','Pending','Succeeded')
            """, conn, tx);
        mark.Parameters.AddWithValue("@attempt", attemptId);
        await mark.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { status = "Succeeded" });
    }
    return Results.Ok(new { status = payment.Status });
});

app.Run();

record CreateOrderInput(long ChannelId, string Text);
record ScheduleOrderInput(long RateId, DateTimeOffset ScheduledAt);
