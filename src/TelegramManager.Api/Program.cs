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
// Bootstrap only the role/audit tables. Existing payment and Safe Deal migrations remain manual.
try
{
    var staffDb=app.Services.GetRequiredService<MySqlDataSource>();
    await using var createStaff=staffDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_staff (
          telegram_id BIGINT NOT NULL PRIMARY KEY,
          role ENUM('admin','moderator') NOT NULL,
          granted_by BIGINT NOT NULL,
          created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await createStaff.ExecuteNonQueryAsync();
    await using var createAudit=staffDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_admin_audit (
          id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
          actor_id BIGINT NOT NULL,
          action VARCHAR(64) NOT NULL,
          target_id BIGINT NULL,
          details VARCHAR(512) NULL,
          created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
          INDEX idx_admin_audit_time (created_at)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await createAudit.ExecuteNonQueryAsync();
}
catch(MySqlException ex)
{
    app.Logger.LogWarning(ex,"Reklio staff schema could not be initialized; verify DB CREATE permission");
}

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
app.MapGet("/api/public/bot-link", async (IHttpClientFactory clients, CancellationToken ct) =>
{
    try
    {
        using var http=clients.CreateClient();
        using var response=await http.GetAsync($"https://api.telegram.org/bot{token}/getMe",ct);
        if(!response.IsSuccessStatusCode) return Results.Problem("Telegram bot is temporarily unavailable", statusCode:503);
        using var json=await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),cancellationToken:ct);
        var data=json.RootElement;
        if(!data.TryGetProperty("ok",out var ok) || !ok.GetBoolean()
           || !data.TryGetProperty("result",out var result)
           || !result.TryGetProperty("username",out var name))
            return Results.Problem("Telegram bot username is unavailable",statusCode:503);
        var username=name.GetString();
        if(string.IsNullOrWhiteSpace(username) || !username.All(ch=>char.IsLetterOrDigit(ch)||ch=='_'))
            return Results.Problem("Invalid Telegram bot username",statusCode:503);
        return Results.Ok(new { url=$"https://t.me/{username}" });
    }
    catch(HttpRequestException) { return Results.Problem("Telegram API unavailable",statusCode:503); }
});

app.MapGet("/api/me", async (HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long id) return Results.Unauthorized();
    var role=await StaffRole(db,id,ct);
    return Results.Ok(new {id,isDisputeAdmin=role is not null,adminRole=role});
});

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
               (SELECT CAST(COALESCE(SUM(m.posts),0) AS SIGNED) FROM channel_daily_metrics m
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
app.MapGet("/api/channels/{channelId:long}/history", async (long channelId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is null) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT m.day,m.subscribers,m.posts
        FROM channel_daily_metrics m
        JOIN channels c ON c.telegram_id=m.channel_id
        WHERE m.channel_id=@channel AND c.is_listed=TRUE
          AND m.day>=UTC_DATE()-INTERVAL 29 DAY
        ORDER BY m.day
        """);
    cmd.Parameters.Add(new MySqlParameter("@channel", channelId));
    var history = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        history.Add(new {
            day = reader.GetDateTime(0).ToString("yyyy-MM-dd"),
            subscribers = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
            posts = reader.GetInt32(2)
        });
    return Results.Ok(history);
});
app.MapGet("/api/finance", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        SELECT a.id,c.title,a.price_rub,a.status,a.payout_status,a.published_at,
               a.payout_eligible_at
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        WHERE a.owner_id=@owner AND a.payment_status='Paid' AND a.is_test_order=FALSE
        ORDER BY a.id DESC
        """);
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    var operations=new List<object>();
    const decimal configuredRate=5m;
    decimal paid=0m,refunded=0m;
    decimal pending=0m,ready=0m,blocked=0m;
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
    {
        var amount=reader.GetDecimal(2);
        var payout=reader.GetString(4);
        if(payout=="ReadyForManualReview") ready+=amount;
        else if(payout=="Paid") paid+=amount;
        else if(payout=="Refunded") refunded+=amount;
        else if(payout=="Blocked") blocked+=amount;
        else if(payout=="OnHold" || payout=="AwaitingVerification") pending+=amount;
        if(operations.Count>=100) continue;
        var commission=decimal.Round(amount*configuredRate/100m,2,MidpointRounding.AwayFromZero);
        operations.Add(new {
            id=reader.GetInt64(0),channel=reader.GetString(1),amount,commission,net=amount,
            status=reader.GetString(3),payoutStatus=payout,
            publishedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5),
            eligibleAt=reader.IsDBNull(6)?(DateTime?)null:reader.GetDateTime(6)
        });
    }
    return Results.Ok(new {pending,readyForReview=ready,blocked,paid,refunded,
        commissionPercent=configuredRate,readyNet=ready,operations,
        note="Сводка по всем оплаченным нетестовым заказам; история показывает последние 100. Комиссия 5% оплачивается покупателем сверх цены продавца. Выплаты не инициируются."});
});
bool IsRootAdmin(long userId) =>
    userId==992964625L ||
    (Environment.GetEnvironmentVariable("REKLIO_ADMIN_IDS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(part => long.TryParse(part,out var adminId) && adminId==userId);

async Task<string?> StaffRole(MySqlDataSource db,long userId,CancellationToken ct)
{
    if(IsRootAdmin(userId)) return "owner";
    await using var cmd=db.CreateCommand("SELECT role FROM reklio_staff WHERE telegram_id=@id");
    cmd.Parameters.Add(new MySqlParameter("@id",userId));
    return (string?)await cmd.ExecuteScalarAsync(ct);
}
app.MapGet("/api/admin/staff",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is not ("owner" or "admin")) return Results.Forbid();
    await using var cmd=db.CreateCommand("SELECT telegram_id,role,granted_by,created_at FROM reklio_staff ORDER BY created_at DESC LIMIT 100");
    var staff=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct)) staff.Add(new {id=reader.GetInt64(0),role=reader.GetString(1),grantedBy=reader.GetInt64(2),createdAt=reader.GetDateTime(3)});
    return Results.Ok(staff);
});
app.MapPost("/api/admin/staff",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    var role=await StaffRole(db,uid,ct);
    if(role is not ("owner" or "admin")) return Results.Forbid();
    var input=await req.ReadFromJsonAsync<StaffInput>(cancellationToken:ct);
    if(input is null || input.TelegramId<=0 || input.TelegramId==uid ||
       input.Role is not ("admin" or "moderator") || IsRootAdmin(input.TelegramId) ||
       (input.Role=="admin" && role!="owner"))
        return Results.BadRequest(new {error="Недопустимый пользователь или роль"});
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    if(role!="owner")
    {
        await using var checkExisting=new MySqlCommand("SELECT role FROM reklio_staff WHERE telegram_id=@target FOR UPDATE",conn,tx);
        checkExisting.Parameters.AddWithValue("@target",input.TelegramId);
        var currentRole=Convert.ToString(await checkExisting.ExecuteScalarAsync(ct));
        if(currentRole=="admin")
        {
            await tx.RollbackAsync(ct);
            return Results.Forbid();
        }
    }
    await using var cmd=new MySqlCommand("""
      INSERT INTO reklio_staff(telegram_id,role,granted_by) VALUES(@target,@role,@actor)
      ON DUPLICATE KEY UPDATE role=VALUES(role),granted_by=VALUES(granted_by)
      """,conn,tx);
    cmd.Parameters.AddWithValue("@target",input.TelegramId);
    cmd.Parameters.AddWithValue("@role",input.Role);
    cmd.Parameters.AddWithValue("@actor",uid);
    await cmd.ExecuteNonQueryAsync(ct);
    await using var audit=new MySqlCommand("INSERT INTO reklio_admin_audit(actor_id,action,target_id,details) VALUES(@actor,'StaffGranted',@target,@role)",conn,tx);
    audit.Parameters.AddWithValue("@actor",uid);
    audit.Parameters.AddWithValue("@target",input.TelegramId);
    audit.Parameters.AddWithValue("@role",input.Role);
    await audit.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {id=input.TelegramId,role=input.Role});
});
app.MapPost("/api/admin/staff/{staffId:long}/revoke",async(long staffId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    var role=await StaffRole(db,uid,ct);
    if(role is not ("owner" or "admin")) return Results.Forbid();
    if(staffId<=0 || staffId==uid || IsRootAdmin(staffId)) return Results.BadRequest();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("DELETE FROM reklio_staff WHERE telegram_id=@id AND (@owner=1 OR role='moderator')",conn,tx);
    cmd.Parameters.AddWithValue("@id",staffId);
    cmd.Parameters.AddWithValue("@owner",role=="owner"?1:0);
    if(await cmd.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Results.NotFound();}
    await using var audit=new MySqlCommand("INSERT INTO reklio_admin_audit(actor_id,action,target_id) VALUES(@actor,'StaffRevoked',@target)",conn,tx);
    audit.Parameters.AddWithValue("@actor",uid);audit.Parameters.AddWithValue("@target",staffId);
    await audit.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {removed=true});
});
app.MapGet("/api/admin/overview",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is null) return Results.Forbid();
    await using var cmd=db.CreateCommand("""
      SELECT (SELECT COUNT(*) FROM channels),
             (SELECT COUNT(*) FROM channels WHERE is_listed=TRUE),
             (SELECT COUNT(*) FROM ad_requests),
             (SELECT COUNT(*) FROM ad_requests WHERE status='Disputed'),
             (SELECT COUNT(*) FROM ad_requests WHERE status IN ('PublishFailed','DeleteFailed'))
      """);
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    return Results.Ok(new {channels=reader.GetInt64(0),listed=reader.GetInt64(1),orders=reader.GetInt64(2),openDisputes=reader.GetInt64(3),failedPublications=reader.GetInt64(4)});
});
app.MapGet("/api/admin/users",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is null) return Results.Forbid();
    var term=req.Query["q"].ToString().Trim();
    if(term.Length>80)return Results.BadRequest();
    await using var cmd=db.CreateCommand("""
        SELECT u.telegram_id,u.display_name,u.username,
          (SELECT COUNT(*) FROM channels c WHERE c.connected_by=u.telegram_id) AS channels,
          (SELECT COUNT(*) FROM ad_requests a WHERE a.advertiser_id=u.telegram_id) AS purchases
        FROM users u
        WHERE @q='' OR CAST(u.telegram_id AS CHAR)=@q
          OR u.display_name LIKE @like OR u.username LIKE @like
        ORDER BY u.telegram_id DESC LIMIT 100
        """);
    cmd.Parameters.Add(new MySqlParameter("@q",term));
    cmd.Parameters.Add(new MySqlParameter("@like","%"+term+"%"));
    var results=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct)) results.Add(new {
      id=reader.GetInt64(0),name=reader.IsDBNull(1)?"":reader.GetString(1),
      username=reader.IsDBNull(2)?null:reader.GetString(2),
      channels=Convert.ToInt64(reader.GetValue(3)),purchases=Convert.ToInt64(reader.GetValue(4))});
    return Results.Ok(results);
});
app.MapGet("/api/admin/orders",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is null) return Results.Forbid();
    var term=req.Query["q"].ToString().Trim();
    if(term.Length>80)return Results.BadRequest();
    await using var cmd=db.CreateCommand("""
       SELECT a.id,c.title,a.status,a.price_rub,a.advertiser_id,a.owner_id,a.created_at
       FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
       WHERE @q='' OR CAST(a.id AS CHAR)=@q OR CAST(a.advertiser_id AS CHAR)=@q
         OR CAST(a.owner_id AS CHAR)=@q OR c.title LIKE @like
       ORDER BY a.created_at DESC LIMIT 100
       """);
    cmd.Parameters.Add(new MySqlParameter("@q",term));
    cmd.Parameters.Add(new MySqlParameter("@like","%"+term+"%"));
    var results=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))results.Add(new{id=reader.GetInt64(0),channel=reader.GetString(1),
        status=reader.GetString(2),price=reader.GetDecimal(3),buyerId=reader.GetInt64(4),
        ownerId=reader.GetInt64(5),createdAt=reader.GetDateTime(6)});
    return Results.Ok(results);
});
app.MapGet("/api/admin/audit",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is not ("owner" or "admin"))return Results.Forbid();
    await using var cmd=db.CreateCommand("""
      SELECT id,actor_id,action,target_id,details,created_at
      FROM reklio_admin_audit ORDER BY created_at DESC LIMIT 100
      """);
    var results=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))results.Add(new{id=reader.GetInt64(0),actorId=reader.GetInt64(1),
       action=reader.GetString(2),targetId=reader.IsDBNull(3)?(long?)null:reader.GetInt64(3),
       details=reader.IsDBNull(4)?null:reader.GetString(4),createdAt=reader.GetDateTime(5)});
    return Results.Ok(results);
});
app.MapGet("/api/admin/channels",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is null) return Results.Forbid();
    await using var cmd=db.CreateCommand("""
      SELECT telegram_id,title,category,is_listed,connected_by,ad_price_rub
      FROM channels ORDER BY connected_at DESC LIMIT 100
      """);
    var channels=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        channels.Add(new {id=reader.GetInt64(0),title=reader.GetString(1),
          category=reader.IsDBNull(2)?"":reader.GetString(2),listed=reader.GetBoolean(3),
          ownerId=reader.GetInt64(4),price=reader.GetDecimal(5)});
    return Results.Ok(channels);
});
app.MapPost("/api/admin/channels/{channelId:long}/listing",async(long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is null) return Results.Forbid();
    var input=await req.ReadFromJsonAsync<UpdateListingInput>(cancellationToken:ct);
    if(input is null) return Results.BadRequest();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("UPDATE channels SET is_listed=@listed WHERE telegram_id=@id",conn,tx);
    cmd.Parameters.AddWithValue("@listed",input.Listed);
    cmd.Parameters.AddWithValue("@id",channelId);
    if(await cmd.ExecuteNonQueryAsync(ct)==0)
    {
        await using var check=new MySqlCommand("SELECT COUNT(*) FROM channels WHERE telegram_id=@id",conn,tx);
        check.Parameters.AddWithValue("@id",channelId);
        if(Convert.ToInt64(await check.ExecuteScalarAsync(ct))==0)
        {await tx.RollbackAsync(ct);return Results.NotFound();}
    }
    await using var audit=new MySqlCommand("INSERT INTO reklio_admin_audit(actor_id,action,target_id,details) VALUES(@actor,'ChannelListing',@target,@details)",conn,tx);
    audit.Parameters.AddWithValue("@actor",uid);audit.Parameters.AddWithValue("@target",channelId);
    audit.Parameters.AddWithValue("@details",input.Listed?"Listed":"Hidden");
    await audit.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {id=channelId,listed=input.Listed});
});
app.MapGet("/api/admin/mtproto-status",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is null) return Results.Forbid();
    try
    {
        await using var cmd=db.CreateCommand("""
            SELECT COUNT(*),COALESCE(SUM(last_result='PRESENT'),0),
                   COALESCE(SUM(last_result='MISSING'),0),MAX(checked_at)
            FROM reklio_post_monitor
            """);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var last=reader.IsDBNull(3)?(DateTime?)null:reader.GetDateTime(3);
        return Results.Ok(new {
            configured=true,checkedOrders=Convert.ToInt64(reader.GetValue(0)),
            present=Convert.ToInt64(reader.GetValue(1)),
            missing=Convert.ToInt64(reader.GetValue(2)),
            lastCheck=last,
            healthy=last is not null && last.Value>=DateTime.UtcNow.AddMinutes(-15)
        });
    }
    catch(MySqlException ex) when(ex.Number==1146)
    {
        return Results.Ok(new {configured=false,checkedOrders=0,present=0,missing=0,
            lastCheck=(DateTime?)null,healthy=false});
    }
});
app.MapGet("/api/admin/publication-issues",async(HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is null) return Results.Forbid();
    await using var cmd=db.CreateCommand("""
      SELECT a.id,c.title,a.status,a.last_error,a.scheduled_at,a.published_at
      FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
      LEFT JOIN reklio_publications p ON p.order_id=a.id
      WHERE a.status IN ('PublishFailed','DeleteFailed')
         OR p.state IN ('NeedsReview','NeedsDeleteReview')
         OR (a.payout_status='Blocked' AND a.last_error IS NOT NULL)
         OR (a.status IN ('Disputed','DisputeResolved') AND a.last_error IS NOT NULL)
      ORDER BY a.updated_at DESC LIMIT 100
      """);
    var issues=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        issues.Add(new {id=reader.GetInt64(0),channel=reader.GetString(1),
          status=reader.GetString(2),error=reader.IsDBNull(3)?"":reader.GetString(3),
          scheduledAt=reader.IsDBNull(4)?(DateTime?)null:reader.GetDateTime(4),
          publishedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5)});
    return Results.Ok(issues);
});
app.MapPost("/api/admin/publication-issues/{orderId:long}/review",async(long orderId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long adminId) return Results.Unauthorized();
    if(await StaffRole(db,adminId,ct) is null) return Results.Forbid();
    var input=await req.ReadFromJsonAsync<PublicationReviewInput>(cancellationToken:ct);
    var note=input?.Note?.Trim();
    if(input?.Decision is not ("ConfirmedProblem" or "NeedsInvestigation") ||
       string.IsNullOrWhiteSpace(note) || note.Length<10 || note.Length>500)
        return Results.BadRequest(new {error="Укажите решение и комментарий от 10 до 500 символов"});
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("""
        UPDATE ad_requests a
        LEFT JOIN reklio_publications p ON p.order_id=a.id
        SET a.payout_status='Blocked',
            a.last_error=LEFT(CONCAT('Проверка модератора: ',@decision,'. ',@note),512)
        WHERE a.id=@id AND a.payout_status NOT IN ('Paid','Refunded')
          AND (a.status IN ('PublishFailed','DeleteFailed')
               OR p.state IN ('NeedsReview','NeedsDeleteReview')
               OR (a.payout_status='Blocked' AND a.last_error IS NOT NULL))
        """,conn,tx);
    cmd.Parameters.AddWithValue("@id",orderId);
    cmd.Parameters.AddWithValue("@decision",input.Decision=="ConfirmedProblem"?"нарушение подтверждено":"нужна дополнительная проверка");
    cmd.Parameters.AddWithValue("@note",note);
    if(await cmd.ExecuteNonQueryAsync(ct)<1)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new {error="Заказ не найден либо его нельзя изменить"});
    }
    await using var audit=new MySqlCommand("""
        INSERT INTO reklio_admin_audit(actor_id,action,target_id,details)
        VALUES(@actor,@action,@target,@note)
        """,conn,tx);
    audit.Parameters.AddWithValue("@actor",adminId);
    audit.Parameters.AddWithValue("@action",input.Decision);
    audit.Parameters.AddWithValue("@target",orderId);
    audit.Parameters.AddWithValue("@note",note);
    await audit.ExecuteNonQueryAsync(ct);
    await using var notice=new MySqlCommand("""
        INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
        SELECT advertiser_id,id,CONCAT('Заказ #',id,': модератор проверил проблему публикации. Решение доступно в заказе.')
        FROM ad_requests WHERE id=@id
        UNION ALL
        SELECT owner_id,id,CONCAT('Заказ #',id,': модератор проверил проблему публикации. Выплата остаётся заблокированной.')
        FROM ad_requests WHERE id=@id
        """,conn,tx);
    notice.Parameters.AddWithValue("@id",orderId);
    await notice.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {reviewed=true,payoutStatus="Blocked"});
});
app.MapGet("/api/admin/disputes", async (HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long userId) return Results.Unauthorized();
    if(await StaffRole(db,userId,ct) is null) return Results.Forbid();
    await using var cmd=db.CreateCommand("""
        SELECT a.id,c.title,a.price_rub,a.content_text,a.dispute_reason,
               a.disputed_at,a.status,a.dispute_resolution,a.payment_status,
               a.is_test_order,a.payout_status
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        WHERE a.status IN ('Disputed','DisputeResolved')
        ORDER BY a.disputed_at DESC LIMIT 100
        """);
    var result=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        result.Add(new {id=reader.GetInt64(0),channel=reader.GetString(1),
          price=reader.GetDecimal(2),text=reader.IsDBNull(3)?"":reader.GetString(3),
          reason=reader.IsDBNull(4)?"":reader.GetString(4),
          openedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5),
          status=reader.GetString(6),resolution=reader.IsDBNull(7)?null:reader.GetString(7),
          paymentStatus=reader.GetString(8),isTest=reader.GetBoolean(9),
          payoutStatus=reader.GetString(10)});
    return Results.Ok(result);
});
app.MapPost("/api/admin/disputes/{orderId:long}/resolve",async(long orderId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long userId) return Results.Unauthorized();
    if(!IsRootAdmin(userId)) return Results.Forbid();
    var input=await req.ReadFromJsonAsync<ResolveDisputeInput>(cancellationToken:ct);
    if(input?.Decision is not ("RefundRecommended" or "PayoutRecommended"))
        return Results.BadRequest(new {error="Недопустимое решение"});
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("""
        UPDATE ad_requests SET status='DisputeResolved',payout_status='Blocked',
          dispute_resolution=@decision,dispute_resolved_at=UTC_TIMESTAMP(),
          dispute_resolved_by=@admin
        WHERE id=@id AND status='Disputed' AND payout_status='Blocked'
        """,conn,tx);
    cmd.Parameters.AddWithValue("@decision",input.Decision);
    cmd.Parameters.AddWithValue("@admin",userId);
    cmd.Parameters.AddWithValue("@id",orderId);
    if(await cmd.ExecuteNonQueryAsync(ct)!=1){
        await tx.RollbackAsync(ct);
        return Results.Conflict(new {error="Спор уже обработан или недоступен"});
    }
    await using var notice=new MySqlCommand("""
        INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
        SELECT advertiser_id,id,CONCAT('Спор по заказу #',id,' рассмотрен. Решение: ',@decision)
        FROM ad_requests WHERE id=@id
        UNION ALL
        SELECT owner_id,id,CONCAT('Спор по заказу #',id,' рассмотрен. Решение: ',@decision)
        FROM ad_requests WHERE id=@id
        """,conn,tx);
    notice.Parameters.AddWithValue("@id",orderId);
    notice.Parameters.AddWithValue("@decision",input.Decision=="RefundRecommended"?"рекомендован возврат":"рекомендована выплата");
    await notice.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {status="DisputeResolved",resolution=input.Decision});
});
app.MapPost("/api/orders/{orderId:long}/dispute", async (long orderId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long buyer) return Results.Unauthorized();
    var input=await req.ReadFromJsonAsync<OpenDisputeInput>(cancellationToken:ct);
    var reason=input?.Reason?.Trim();
    if (string.IsNullOrWhiteSpace(reason) || reason.Length<10 || reason.Length>512)
        return Results.BadRequest(new {error="Опишите проблему (от 10 до 512 символов)"});
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("""
        UPDATE ad_requests
        SET status='Disputed',payout_status='Blocked',dispute_reason=@reason,disputed_at=UTC_TIMESTAMP()
        WHERE id=@id AND advertiser_id=@buyer
          AND status IN ('Published','Deleted','DeleteFailed')
          AND payment_status IN ('Paid','TestPaid')
          AND payout_status NOT IN ('Paid','Refunded')
        """,conn,tx);
    cmd.Parameters.AddWithValue("@id",orderId);
    cmd.Parameters.AddWithValue("@buyer",buyer);
    cmd.Parameters.AddWithValue("@reason",reason);
    if(await cmd.ExecuteNonQueryAsync(ct)!=1)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new {error="Для этого заказа спор недоступен или уже открыт"});
    }
    await using var notice=new MySqlCommand("""
        INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
        SELECT owner_id,id,CONCAT('По заказу #',id,' открыт спор. Выплата приостановлена.')
        FROM ad_requests WHERE id=@id
        """,conn,tx);
    notice.Parameters.AddWithValue("@id",orderId);
    await notice.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {status="Disputed"});
});

app.MapGet("/api/orders/{orderId:long}/deal", async (long orderId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        SELECT a.price_rub,a.status,a.payment_status,a.payout_status,a.is_test_order,a.dispute_reason,
               a.published_at,a.placement_verified_at,a.payout_eligible_at,
               a.advertiser_id,a.owner_id
        FROM ad_requests a
        WHERE a.id=@id AND (a.advertiser_id=@user OR a.owner_id=@user)
        LIMIT 1
        """);
    cmd.Parameters.Add(new MySqlParameter("@id",orderId));
    cmd.Parameters.Add(new MySqlParameter("@user",userId));
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct)) return Results.NotFound();
    var sellerPrice=reader.GetDecimal(0);
    var fee=decimal.Round(sellerPrice*0.05m,2,MidpointRounding.AwayFromZero);
    var status=reader.GetString(1);
    var paymentStatus=reader.GetString(2);
    var payoutStatus=reader.GetString(3);
    var isTest=reader.GetBoolean(4);
    return Results.Ok(new {
        orderId,sellerPrice,serviceFee=fee,total=sellerPrice+fee,
        status,paymentStatus,payoutStatus,isTest,
        publishedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5),
        verifiedAt=reader.IsDBNull(6)?(DateTime?)null:reader.GetDateTime(6),
        eligibleAt=reader.IsDBNull(7)?(DateTime?)null:reader.GetDateTime(7),
        isSeller=reader.GetInt64(9)==userId,
        providerDealActive=false,
        message="Режим подготовки: реальные Safe Deal и выплаты YooKassa пока не активированы."
    });
});
app.MapGet("/api/orders/summary", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT
          COALESCE(SUM(CASE WHEN owner_id=@user AND status='Pending' THEN 1 ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN owner_id=@user AND status IN ('Published','Deleted') THEN 1 ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN advertiser_id=@user AND status IN ('Published','Deleted') THEN 1 ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN owner_id=@user AND payment_status='Paid' AND is_test_order=FALSE THEN price_rub ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN advertiser_id=@user AND payment_status='Paid' AND is_test_order=FALSE THEN price_rub ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN owner_id=@user AND status IN ('Scheduled','Publishing') THEN 1 ELSE 0 END),0)
        FROM ad_requests
        WHERE owner_id=@user OR advertiser_id=@user
        """);
    cmd.Parameters.Add(new MySqlParameter("@user",userId));
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    if(!await reader.ReadAsync(ct)) return Results.Ok(new {incomingPending=0,completedSales=0,completedPurchases=0,salesVolume=0m,purchaseVolume=0m,upcomingSales=0});
    return Results.Ok(new {
        incomingPending=Convert.ToInt32(reader.GetValue(0)),
        completedSales=Convert.ToInt32(reader.GetValue(1)),
        completedPurchases=Convert.ToInt32(reader.GetValue(2)),
        salesVolume=reader.GetDecimal(3),
        purchaseVolume=reader.GetDecimal(4),
        upcomingSales=Convert.ToInt32(reader.GetValue(5))
    });
});
app.MapGet("/api/orders", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT a.id,c.title,a.price_rub,a.status,a.created_at,a.owner_id,
               a.content_text,a.scheduled_at,r.duration_hours,a.payment_status,a.payout_status,a.is_test_order,a.dispute_reason,a.last_error
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        LEFT JOIN channel_duration_rates r ON r.id=a.time_rate_id
        WHERE a.advertiser_id=@user OR a.owner_id=@user
        ORDER BY a.created_at DESC LIMIT 50
        """);
    cmd.Parameters.Add(new MySqlParameter("@user", userId));
    var orders = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        orders.Add(new { id = reader.GetInt64(0), channel = reader.GetString(1),
            price = reader.GetDecimal(2), status = reader.GetString(3), createdAt = reader.GetDateTime(4), isOwner = reader.GetInt64(5) == userId,
            text = reader.IsDBNull(6) ? "" : reader.GetString(6),
            scheduledAt = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7),
            durationHours = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8),
            paymentStatus=reader.GetString(9),payoutStatus=reader.GetString(10),isTest=reader.GetBoolean(11),disputeReason=reader.IsDBNull(12)?null:reader.GetString(12),publicationIssue=reader.IsDBNull(13)?null:reader.GetString(13) });
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

app.MapGet("/api/my-channels/{channelId:long}/rates", async (long channelId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        SELECT r.id,r.duration_hours,r.price_rub,r.is_active
        FROM channel_duration_rates r JOIN channels c ON c.telegram_id=r.channel_id
        WHERE c.telegram_id=@channel AND c.connected_by=@owner ORDER BY r.duration_hours
        """);
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    var rates=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        rates.Add(new {id=reader.GetInt64(0),hours=reader.GetInt32(1),price=reader.GetDecimal(2),active=reader.GetBoolean(3)});
    return Results.Ok(rates);
});
app.MapPost("/api/my-channels/{channelId:long}/rates", async (long channelId, UpdateRateInput input, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    if(input.Hours<1 || input.Hours>720 || input.Price<1 || input.Price>1000000)
        return Results.BadRequest(new {error="Укажите длительность 1–720 ч и цену 1–1000000 ₽"});
    await using var cmd=db.CreateCommand("""
        INSERT INTO channel_duration_rates(channel_id,duration_hours,price_rub,is_active)
        SELECT telegram_id,@hours,@price,TRUE FROM channels
        WHERE telegram_id=@channel AND connected_by=@owner
        ON DUPLICATE KEY UPDATE price_rub=VALUES(price_rub),is_active=TRUE
        """);
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    cmd.Parameters.Add(new MySqlParameter("@hours",input.Hours));
    cmd.Parameters.Add(new MySqlParameter("@price",input.Price));
    var changed=await cmd.ExecuteNonQueryAsync(ct);
    return changed>0 ? Results.Ok(new {saved=true}) : Results.NotFound();
});
app.MapPost("/api/my-channels/{channelId:long}/rates/{rateId:long}/toggle", async (long channelId,long rateId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        UPDATE channel_duration_rates r JOIN channels c ON c.telegram_id=r.channel_id
        SET r.is_active=NOT r.is_active
        WHERE r.id=@rate AND c.telegram_id=@channel AND c.connected_by=@owner
        """);
    cmd.Parameters.Add(new MySqlParameter("@rate",rateId));
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    return await cmd.ExecuteNonQueryAsync(ct)>0 ? Results.Ok(new {saved=true}) : Results.NotFound();
});
app.MapPost("/api/my-channels/{channelId:long}/listing", async (long channelId,UpdateListingInput input,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        UPDATE channels SET is_listed=@listed WHERE telegram_id=@channel AND connected_by=@owner
        """);
    cmd.Parameters.Add(new MySqlParameter("@listed",input.Listed));
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    return await cmd.ExecuteNonQueryAsync(ct)>0 ? Results.Ok(new {saved=true}) : Results.NotFound();
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
app.MapPost("/api/orders/{orderId:long}/reschedule", async (long orderId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long buyer) return Results.Unauthorized();
    var input=await req.ReadFromJsonAsync<RescheduleInput>(cancellationToken:ct);
    if (input is null || input.ScheduledAt.Offset!=TimeSpan.Zero)
        return Results.BadRequest(new {error="Укажите время в UTC"});
    await using var cmd=db.CreateCommand("""
        UPDATE ad_requests a
        SET a.scheduled_at=@at,
            a.status=IF(a.status='PaidReschedule','PendingPaid','Pending')
        WHERE a.id=@id AND a.advertiser_id=@buyer
          AND (a.status='PaidReschedule' OR
               (a.status='Pending' AND a.scheduled_at<=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)))
          AND @at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
          AND @at<DATE_ADD(UTC_TIMESTAMP(),INTERVAL 90 DAY)
          AND NOT EXISTS(
            SELECT 1 FROM (SELECT id,channel_id,scheduled_at,status FROM ad_requests) other
            WHERE other.id<>a.id AND other.channel_id=a.channel_id AND other.scheduled_at=@at
              AND other.status IN ('Pending','PendingPaid','AwaitingPayment','Scheduled','Publishing','Published'))
        """);
    cmd.Parameters.Add(new MySqlParameter("@id",orderId));
    cmd.Parameters.Add(new MySqlParameter("@buyer",buyer));
    cmd.Parameters.Add(new MySqlParameter("@at",input.ScheduledAt.UtcDateTime));
    if(await cmd.ExecuteNonQueryAsync(ct)!=1)
        return Results.Conflict(new {error="Перенос недоступен, выбрано слишком раннее или занятое время"});
    return Results.Ok(new {status="Pending",message="Новое время отправлено владельцу для повторного согласования"});
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
        SET status=IF(status='PendingPaid','Scheduled','AwaitingPayment')
        WHERE id=@order AND owner_id=@owner AND status IN ('Pending','PendingPaid')
          AND scheduled_at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
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
        SELECT advertiser_id,id,CONCAT('Заявка #',id,' одобрена. ',IF(status='Scheduled','Размещение запланировано.','Ожидается оплата.'))
        FROM ad_requests WHERE id=@order
        """, conn, tx);
    notify.Parameters.AddWithValue("@order", orderId);
    await notify.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    await using var statusCmd=new MySqlCommand("SELECT status FROM ad_requests WHERE id=@order",conn);
    statusCmd.Parameters.AddWithValue("@order",orderId);
    var currentStatus=Convert.ToString(await statusCmd.ExecuteScalarAsync(ct)) ?? "AwaitingPayment";
    return Results.Ok(new { id = orderId, status = currentStatus });
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
          AND scheduled_at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
        FOR UPDATE
        """, conn, tx);
    lookup.Parameters.AddWithValue("@id", orderId);
    lookup.Parameters.AddWithValue("@buyer", buyer);
    var priceValue = await lookup.ExecuteScalarAsync(ct);
    if (priceValue is null) { await tx.RollbackAsync(ct); return Results.Conflict(new { error = "Заказ недоступен для оплаты" }); }
    var price = Convert.ToDecimal(priceValue);
    var buyerFee = decimal.Round(price*0.05m,2,MidpointRounding.AwayFromZero);
    var buyerTotal = price+buyerFee;
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
        insert.Parameters.AddWithValue("@price", buyerTotal);
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
        amount = new { value = buyerTotal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), currency = "RUB" },
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

app.MapPost("/api/payments/yookassa/notification", async (HttpRequest req, MySqlDataSource db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (Environment.GetEnvironmentVariable("YOOKASSA_MODE") != "test")
        return Results.StatusCode(503);
    var shop=Environment.GetEnvironmentVariable("YOOKASSA_SHOP_ID");
    var key=Environment.GetEnvironmentVariable("YOOKASSA_SECRET_KEY");
    if (string.IsNullOrWhiteSpace(shop) || string.IsNullOrWhiteSpace(key))
        return Results.StatusCode(503);

    // Webhook JSON is untrusted. Verify its payment ID with YooKassa before changing an order.
    System.Text.Json.JsonDocument notification;
    try { notification=await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken:ct); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
    using (notification)
    {
        var root=notification.RootElement;
        if (!root.TryGetProperty("type",out var type) || type.GetString()!="notification" ||
            !root.TryGetProperty("event",out var ev) || ev.GetString()!="payment.succeeded" ||
            !root.TryGetProperty("object",out var obj) ||
            !obj.TryGetProperty("id",out var paymentIdJson))
            return Results.Ok(new {ignored=true});
        var paymentId=paymentIdJson.GetString();
        if (string.IsNullOrEmpty(paymentId) || paymentId.Length>100)
            return Results.BadRequest();

        await using var conn=await db.OpenConnectionAsync(ct);
        await using var lookup=new MySqlCommand("""
            SELECT p.id,p.order_id,p.amount_rub
            FROM reklio_payment_attempts p
            WHERE p.provider_payment_id=@payment AND p.mode='test' LIMIT 1
            """,conn);
        lookup.Parameters.AddWithValue("@payment",paymentId);
        long attemptId,orderId;decimal expected;
        await using(var reader=await lookup.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct)) return Results.Ok(new {ignored=true});
            attemptId=reader.GetInt64(0);orderId=reader.GetInt64(1);expected=reader.GetDecimal(2);
        }
        var client=new TelegramManager.Api.Payments.YooKassaSafeDealClient(clients.CreateClient(),shop,key);
        var payment=await client.GetPaymentAsync(paymentId,ct);
        if(payment.Id!=paymentId || payment.Status!="succeeded" || !payment.Paid ||
            payment.Metadata?.GetValueOrDefault("order_id")!=orderId.ToString() ||
            payment.Amount.Currency!="RUB" ||
            !decimal.TryParse(payment.Amount.Value,System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,out var actual) || actual!=expected)
            return Results.Ok(new {ignored=true});
        await using var tx=await conn.BeginTransactionAsync(ct);
        await using var update=new MySqlCommand("""
            UPDATE ad_requests SET status='Scheduled',paid_confirmed=TRUE,
              payment_status='TestPaid',is_test_order=TRUE,payout_status='NotEligible'
            WHERE id=@order AND status='AwaitingPayment'
              AND scheduled_at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
            """,conn,tx);
        update.Parameters.AddWithValue("@order",orderId);
        var transitioned=await update.ExecuteNonQueryAsync(ct);
        if(transitioned==0)
        {
            await using var late=new MySqlCommand("""
                UPDATE ad_requests SET status='PaidReschedule', paid_confirmed=TRUE,
                  payment_status='TestPaid', is_test_order=TRUE, payout_status='NotEligible'
                WHERE id=@order AND status='AwaitingPayment'
                  AND scheduled_at<=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
                """,conn,tx);
            late.Parameters.AddWithValue("@order",orderId);
            await late.ExecuteNonQueryAsync(ct);
        }
        if(transitioned==1)
        {
            await using var notice=new MySqlCommand("""
                INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
                SELECT advertiser_id,id,CONCAT('Тестовая оплата заказа #',id,' подтверждена. Реклама запланирована.')
                  FROM ad_requests WHERE id=@order
                UNION ALL
                SELECT owner_id,id,CONCAT('Тестовый заказ #',id,' оплачен. Ожидается публикация.')
                  FROM ad_requests WHERE id=@order
                """,conn,tx);
            notice.Parameters.AddWithValue("@order",orderId);
            await notice.ExecuteNonQueryAsync(ct);
        }
        await using var mark=new MySqlCommand("""
            UPDATE reklio_payment_attempts SET status='Succeeded'
            WHERE id=@attempt AND status IN ('Created','Pending','Succeeded')
            """,conn,tx);
        mark.Parameters.AddWithValue("@attempt",attemptId);
        await mark.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new {received=true});
    }
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
            UPDATE ad_requests SET status='Scheduled', paid_confirmed=TRUE,
                payment_status='TestPaid', is_test_order=TRUE, payout_status='NotEligible'
            WHERE id=@id AND advertiser_id=@buyer AND status='AwaitingPayment'
              AND scheduled_at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
            """, conn, tx);
        update.Parameters.AddWithValue("@id", orderId);
        update.Parameters.AddWithValue("@buyer", buyer);
        var transitioned = await update.ExecuteNonQueryAsync(ct);
        if (transitioned == 0)
        {
            await using var late=new MySqlCommand("""
                UPDATE ad_requests SET status='PaidReschedule', paid_confirmed=TRUE,
                    payment_status='TestPaid', is_test_order=TRUE, payout_status='NotEligible'
                WHERE id=@id AND advertiser_id=@buyer AND status='AwaitingPayment'
                  AND scheduled_at<=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
                """, conn, tx);
            late.Parameters.AddWithValue("@id",orderId);
            late.Parameters.AddWithValue("@buyer",buyer);
            await late.ExecuteNonQueryAsync(ct);
        }
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
record PublicationReviewInput(string Decision,string Note);
record StaffInput(long TelegramId,string Role);
record ResolveDisputeInput(string Decision);
record OpenDisputeInput(string Reason);
record RescheduleInput(DateTimeOffset ScheduledAt);
record ScheduleOrderInput(long RateId, DateTimeOffset ScheduledAt);

record UpdateRateInput(int Hours, decimal Price);
record UpdateListingInput(bool Listed);
