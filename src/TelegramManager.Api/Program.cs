using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
    ?? throw new InvalidOperationException("BOT_TOKEN is required");
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException("DATABASE_URL is required");
builder.Services.AddSingleton(new MySqlDataSourceBuilder(connectionString).Build());
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Reklio.Payments.TributePaymentWorker>();
builder.Services.AddHostedService<Reklio.Orders.OrderLifecycleWorker>();
builder.Services.AddMemoryCache();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode=StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(context =>
    {
        var path=context.Request.Path.Value??"";
        // Signed provider deliveries must not compete with the shared per-IP bucket.
        if(path=="/api/payments/payanyway/notification")
            return RateLimitPartition.GetFixedWindowLimiter("payanyway-notification",_=>new FixedWindowRateLimiterOptions
            {
                PermitLimit=300,Window=TimeSpan.FromMinutes(1),
                QueueLimit=0,AutoReplenishment=true
            });
        // Keep health checks and YooKassa callbacks available during traffic spikes.
        if(path=="/api/health")
            return RateLimitPartition.GetNoLimiter("health-or-webhook");
        var identity=context.User.FindFirst("id")?.Value;
        var ip=context.Connection.RemoteIpAddress?.ToString()??"unknown";
        var key=(identity is null?"ip:"+ip:"user:"+identity)+
            // A limiter's options are fixed by the first request for its key, so each limit needs its own key.
            (path=="/api/my-channels/connect"?":connect":path.StartsWith("/api/auth/")?":auth":
             path=="/api/payments/yookassa/notification"?":webhook":
             path.StartsWith("/api/orders/")&&path.EndsWith("/payment")?":payment":":general");
        var isSensitive=path=="/api/my-channels/connect"||path.StartsWith("/api/auth/");
        var isWebhook=path=="/api/payments/yookassa/notification";
        return RateLimitPartition.GetFixedWindowLimiter(key,_=>new FixedWindowRateLimiterOptions
        {
            PermitLimit=isWebhook?60:isSensitive?12:180,Window=TimeSpan.FromMinutes(1),
            QueueLimit=0,AutoReplenishment=true
        });
    });
});
// OIDC credentials must be configured in VPS environment, never in Git.
var loginClientId=Environment.GetEnvironmentVariable("TELEGRAM_LOGIN_CLIENT_ID");
var loginSecret=Environment.GetEnvironmentVariable("TELEGRAM_LOGIN_CLIENT_SECRET");
var loginEnabled=!string.IsNullOrWhiteSpace(loginClientId)&&!string.IsNullOrWhiteSpace(loginSecret);
// Persist signing/encryption keys across app restarts and deployments.
var keyFolder=Environment.GetEnvironmentVariable("REKLIO_AUTH_KEY_DIRECTORY") ?? "/opt/reklio/auth-keys";
if(loginEnabled)
{
    System.IO.Directory.CreateDirectory(keyFolder);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyFolder))
        .SetApplicationName("ReklioWebAuth");
}
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme=CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme=OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name="__Host-reklio-session";
    options.Cookie.HttpOnly=true;
    options.Cookie.SecurePolicy=CookieSecurePolicy.Always;
    options.Cookie.SameSite=SameSiteMode.Lax;
    options.Cookie.Path="/";
    options.ExpireTimeSpan=TimeSpan.FromDays(14);
    options.SlidingExpiration=true;
    options.Events.OnRedirectToLogin=context => {context.Response.StatusCode=401;return Task.CompletedTask;};
    options.Events.OnRedirectToAccessDenied=context => {context.Response.StatusCode=403;return Task.CompletedTask;};
});
if(loginEnabled)
{
    var oidcStateStore=new MemoryCache(new MemoryCacheOptions {SizeLimit=10000});
    builder.Services.AddAuthentication().AddOpenIdConnect(options =>
    {
        options.Authority="https://oauth.telegram.org";
        options.MetadataAddress="https://oauth.telegram.org/.well-known/openid-configuration";
        options.ClientId=loginClientId!;
        options.ClientSecret=loginSecret!;
        options.ResponseType="code";
        // Telegram rejects ASP.NET Core's long protected state parameter.
        // Keep the full state server-side, send only a single-use opaque token.
        options.StateDataFormat=new ShortOidcStateFormat(oidcStateStore);
        options.UsePkce=true;
        options.CallbackPath="/signin-telegram";
        options.Events.OnRedirectToIdentityProvider=context =>
        {
            // Explicit public HTTPS callback: nginx terminates TLS upstream.
            context.ProtocolMessage.RedirectUri="https://api.reklio.site/signin-telegram";
            return Task.CompletedTask;
        };
        options.SignInScheme=CookieAuthenticationDefaults.AuthenticationScheme;
        options.RequireHttpsMetadata=true;
        options.GetClaimsFromUserInfoEndpoint=false;
        options.SaveTokens=false;
        options.MapInboundClaims=false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.TokenValidationParameters=new TokenValidationParameters
        {
            ValidIssuer="https://oauth.telegram.org",
            ValidateIssuer=true,
            ValidateAudience=true,
            ValidateLifetime=true,
            NameClaimType="name"
        };
    });
}
builder.Services.AddAuthorization();
builder.Services.AddHostedService<TelegramManager.Api.Publishing.PublicationWorker>();
builder.Services.AddHostedService<TelegramManager.Api.Notifications.NotificationWorker>();
var allowedOrigin = Environment.GetEnvironmentVariable("MINIAPP_ORIGIN");
builder.Services.AddCors(o => o.AddPolicy("miniapp", p => p
    .WithOrigins(new[] { allowedOrigin, "https://reklio.site", "https://www.reklio.site" }
        .Where(origin => !string.IsNullOrWhiteSpace(origin)).Select(origin => origin!).Distinct().ToArray())
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
var app = builder.Build();
app.UseCors("miniapp");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/api/auth/config", () => Results.Ok(new {telegramLoginAvailable=loginEnabled}));
app.MapGet("/api/auth/login", (HttpContext ctx) =>
{
    if(!loginEnabled)return Results.Problem("Telegram Login is not configured yet",statusCode:503);
    return Results.Challenge(new AuthenticationProperties {RedirectUri="https://reklio.site/"}, new[] {OpenIdConnectDefaults.AuthenticationScheme});
});
app.MapGet("/api/auth/me", (HttpContext ctx) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Ok(new {authenticated=false});
    var id=ctx.User.FindFirst("id")?.Value;
    if(!long.TryParse(id,out var telegramId)||telegramId<=0)return Results.Unauthorized();
    return Results.Ok(new {authenticated=true,telegramId,
        name=ctx.User.FindFirst("name")?.Value,
        username=ctx.User.FindFirst("preferred_username")?.Value});
});
app.MapGet("/api/web/profile", async (HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true ||
       !long.TryParse(ctx.User.FindFirst("id")?.Value,out var telegramId) || telegramId<=0)
        return Results.Unauthorized();
    var role=await StaffRole(db,telegramId,ct);
    await using var cmd=db.CreateCommand("""
        SELECT
          (SELECT COUNT(*) FROM channels WHERE connected_by=@uid),
          (SELECT COUNT(*) FROM ad_requests WHERE advertiser_id=@uid),
          (SELECT COUNT(*) FROM ad_requests WHERE owner_id=@uid),
          (SELECT COALESCE(GREATEST(subscription_until,pro_granted_until),subscription_until,pro_granted_until)
             FROM users WHERE telegram_id=@uid)
        """);
    cmd.Parameters.Add(new MySqlParameter("@uid",telegramId));
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    var until=reader.IsDBNull(3)?(DateTime?)null:reader.GetDateTime(3);
    var pro=until.HasValue&&until.Value>DateTime.UtcNow;
    return Results.Ok(new {
       telegramId,name=ctx.User.FindFirst("name")?.Value,
       username=ctx.User.FindFirst("preferred_username")?.Value,
       plan=pro?"PRO":"FREE",proUntil=pro?until:null,
       channelLimit=pro?10:1,
       channels=reader.GetInt64(0),purchases=reader.GetInt64(1),
       sales=reader.GetInt64(2),staffRole=role
    });
});
app.MapGet("/api/web/channels", async (HttpContext ctx, MySqlDataSource db, CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true ||
       !long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId) || userId<=0)
        return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        SELECT c.telegram_id,c.title,c.is_listed,c.ad_price_rub,c.category,a.alias,
          (SELECT m.subscribers FROM channel_daily_metrics m WHERE m.channel_id=c.telegram_id ORDER BY m.day DESC LIMIT 1) AS subscribers,
          (SELECT CAST(ROUND(AVG(v.views)) AS SIGNED) FROM reklio_post_views v WHERE v.channel_id=c.telegram_id) AS avg_views,
          EXISTS(SELECT 1 FROM reklio_channel_blocks b WHERE b.channel_id=c.telegram_id AND (b.blocked_until IS NULL OR b.blocked_until>UTC_TIMESTAMP())) AS blocked,
          (SELECT b.blocked_until FROM reklio_channel_blocks b WHERE b.channel_id=c.telegram_id) AS blocked_until
        FROM channels c
        LEFT JOIN reklio_channel_aliases a ON a.channel_id=c.telegram_id
        WHERE c.connected_by=@user ORDER BY c.connected_at DESC LIMIT 50
        """);
    cmd.Parameters.Add(new MySqlParameter("@user",userId));
    var channels=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        channels.Add(new {id=reader.GetInt64(0),title=reader.GetString(1),listed=reader.GetBoolean(2),
          price=reader.GetDecimal(3),category=reader.IsDBNull(4)?"":reader.GetString(4),
          alias=reader.IsDBNull(5)?null:reader.GetString(5),subscribers=reader.IsDBNull(6)?(int?)null:reader.GetInt32(6),avgViews=reader.IsDBNull(7)?(long?)null:reader.GetInt64(7),
          blocked=reader.GetInt64(8)!=0,blockedUntil=reader.IsDBNull(9)?(DateTime?)null:reader.GetDateTime(9)});
    return Results.Ok(channels);
});
app.MapPost("/api/web/channels/{channelId:long}/listing", async (long channelId,UpdateListingInput input,HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true ||
       !long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId) || userId<=0)
        return Results.Unauthorized();
    if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))
        return Results.StatusCode(403);
    if(input.Listed&&await ChannelIsBlocked(db,channelId,ct))return ChannelBlockedConflict();
    if(input.Listed&&await ChannelListingProblem(db,channelId,ct) is {} listingProblem)return Results.Conflict(new {error=listingProblem});
    await using var cmd=db.CreateCommand("UPDATE channels SET is_listed=@listed WHERE telegram_id=@channel AND connected_by=@owner");
    cmd.Parameters.Add(new MySqlParameter("@listed",input.Listed));
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    return await cmd.ExecuteNonQueryAsync(ct)>0?Results.Ok(new{saved=true}):Results.NotFound();
});
// Browser rate management uses the same channel_duration_rates rows as the Mini App.
app.MapGet("/api/web/channels/{channelId:long}/rates", async (long channelId,HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true || !long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId) || userId<=0)
        return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        SELECT r.id,r.duration_hours,r.price_rub,r.is_active FROM channel_duration_rates r
        JOIN channels c ON c.telegram_id=r.channel_id
        WHERE c.telegram_id=@channel AND c.connected_by=@owner ORDER BY r.duration_hours
        """);
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    var rates=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        rates.Add(new{id=reader.GetInt64(0),hours=reader.GetInt32(1),price=reader.GetDecimal(2),active=reader.GetBoolean(3)});
    return Results.Ok(rates);
});
app.MapPost("/api/web/channels/{channelId:long}/rates", async (long channelId,UpdateRateInput input,HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true || !long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId) || userId<=0)
        return Results.Unauthorized();
    if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))return Results.StatusCode(403);
    if(input.Hours<0||input.Hours>720||input.Price<1||input.Price>1000000)
        return Results.BadRequest(new{error="Длительность: 0 (навсегда) или 1–720 часов; цена 1–1000000 ₽"});
    await using var cmd=db.CreateCommand("""
        INSERT INTO channel_duration_rates(channel_id,duration_hours,price_rub,is_active)
        SELECT telegram_id,@hours,@price,TRUE FROM channels
        WHERE telegram_id=@channel AND connected_by=@owner
        ON DUPLICATE KEY UPDATE price_rub=VALUES(price_rub),is_active=TRUE
        """);
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    cmd.Parameters.Add(new MySqlParameter("@hours",input.Hours));cmd.Parameters.Add(new MySqlParameter("@price",input.Price));
    return await cmd.ExecuteNonQueryAsync(ct)>0?Results.Ok(new{saved=true}):Results.NotFound();
});
app.MapPost("/api/web/channels/{channelId:long}/rates/{rateId:long}/toggle", async (long channelId,long rateId,HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true || !long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId) || userId<=0)
        return Results.Unauthorized();
    if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))return Results.StatusCode(403);
    await using var cmd=db.CreateCommand("""
        UPDATE channel_duration_rates r JOIN channels c ON c.telegram_id=r.channel_id
        SET r.is_active=NOT r.is_active
        WHERE r.id=@rate AND c.telegram_id=@channel AND c.connected_by=@owner
        """);
    cmd.Parameters.Add(new MySqlParameter("@rate",rateId));cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    return await cmd.ExecuteNonQueryAsync(ct)>0?Results.Ok(new{saved=true}):Results.NotFound();
});
app.MapGet("/api/web/orders", async (HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId)||userId<=0)
        return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        SELECT a.id,c.title,a.price_rub,a.status,a.created_at,a.owner_id,
               a.content_text,a.scheduled_at,r.duration_hours,a.payment_status,a.payout_status,a.is_test_order,a.dispute_reason,a.last_error
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        LEFT JOIN channel_duration_rates r ON r.id=a.time_rate_id
        WHERE a.advertiser_id=@user OR a.owner_id=@user
        ORDER BY a.created_at DESC LIMIT 100
        """);
    cmd.Parameters.Add(new MySqlParameter("@user",userId));
    var orders=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        orders.Add(new{id=reader.GetInt64(0),channel=reader.GetString(1),price=reader.GetDecimal(2),
          status=reader.GetString(3),createdAt=reader.GetDateTime(4),isOwner=reader.GetInt64(5)==userId,
          text=reader.IsDBNull(6)?"":reader.GetString(6),
          scheduledAt=reader.IsDBNull(7)?(DateTime?)null:reader.GetDateTime(7),
          durationHours=reader.IsDBNull(8)?(int?)null:reader.GetInt32(8),
          paymentStatus=reader.GetString(9),payoutStatus=reader.GetString(10),
          isTest=reader.GetBoolean(11),disputeReason=reader.IsDBNull(12)?null:reader.GetString(12),
          publicationIssue=reader.IsDBNull(13)?null:reader.GetString(13)});
    return Results.Ok(orders);
});
app.MapPost("/api/web/orders/{orderId:long}/decision", async (long orderId,WebOrderDecision input,HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var ownerId)||ownerId<=0)
        return Results.Unauthorized();
    if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))return Results.StatusCode(403);
    if(input.Action is not ("approve" or "reject"))return Results.BadRequest(new{error="Некорректное действие"});
    if(input.Action=="approve"&&await OrderNeedsPaymentDetails(db,orderId,ownerId,ct))return PaymentDetailsRequired();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    var approve=input.Action=="approve";
    await using var cmd=new MySqlCommand(approve?"""
        UPDATE ad_requests SET status=IF(status='PendingPaid','Scheduled','AwaitingPayment')
        WHERE id=@order AND owner_id=@owner AND status IN ('Pending','PendingPaid')
        AND ((status='Pending' AND scheduled_at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR))
         OR (status='PendingPaid' AND scheduled_at>UTC_TIMESTAMP()))
        """:"""
        UPDATE ad_requests SET status='Rejected' WHERE id=@order AND owner_id=@owner AND status='Pending'
        """,conn,tx);
    cmd.Parameters.AddWithValue("@order",orderId);cmd.Parameters.AddWithValue("@owner",ownerId);
    if(await cmd.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Заказ недоступен или его время/статус изменились. Обновите список."});}
    await using var notify=new MySqlCommand("""
        INSERT INTO notification_outbox(recipient_id,order_id,body)
        SELECT advertiser_id,id,CONCAT(@prefix,' Заявка #',id,@suffix)
        FROM ad_requests WHERE id=@order
        """,conn,tx);
    notify.Parameters.AddWithValue("@order",orderId);
    notify.Parameters.AddWithValue("@prefix",approve?"✅":"❌");
    notify.Parameters.AddWithValue("@suffix",approve?" одобрена. Откройте заказ в Reklio.":" отклонена владельцем.");
    await notify.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new{saved=true});
});
app.MapGet("/api/web/orders/{orderId:long}/events", async (long orderId,HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true || !long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId)||userId<=0)
        return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        SELECT e.event_type,e.created_at FROM ad_order_events e
        JOIN ad_requests a ON a.id=e.order_id
        WHERE e.order_id=@order AND (a.owner_id=@user OR a.advertiser_id=@user)
        ORDER BY e.created_at DESC LIMIT 40
        """);
    cmd.Parameters.Add(new MySqlParameter("@order",orderId));
    cmd.Parameters.Add(new MySqlParameter("@user",userId));
    var events=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        events.Add(new{type=reader.GetString(0),createdAt=reader.GetDateTime(1)});
    return Results.Ok(events);
});
app.MapGet("/api/web/finance", async (HttpContext ctx,MySqlDataSource db,CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true || !long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId)||userId<=0)
        return Results.Unauthorized();
    await using var summary=db.CreateCommand("""
        SELECT
          COALESCE(SUM(CASE WHEN owner_id=@user AND payment_status='Paid' AND is_test_order=FALSE THEN price_rub ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN advertiser_id=@user AND payment_status='Paid' AND is_test_order=FALSE THEN price_rub ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN advertiser_id=@user AND payment_status='TestPaid' THEN price_rub ELSE 0 END),0)
        FROM ad_requests WHERE owner_id=@user OR advertiser_id=@user
        """);
    summary.Parameters.Add(new MySqlParameter("@user",userId));
    decimal revenue,expenses,testPayments;
    await using(var reader=await summary.ExecuteReaderAsync(ct))
    {
        await reader.ReadAsync(ct);
        revenue=reader.GetDecimal(0);expenses=reader.GetDecimal(1);testPayments=reader.GetDecimal(2);
    }
    await using var cmd=db.CreateCommand("""
        SELECT a.id,c.title,a.price_rub,a.status,a.payout_status,a.published_at,a.payout_eligible_at
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        WHERE a.owner_id=@owner AND a.payment_status='Paid' AND a.is_test_order=FALSE
        ORDER BY a.id DESC LIMIT 100
        """);
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    var operations=new List<object>();
    decimal paid=0m,ready=0m,pending=0m,blocked=0m,refunded=0m;
    await using(var reader=await cmd.ExecuteReaderAsync(ct))
    while(await reader.ReadAsync(ct))
    {
        var amount=reader.GetDecimal(2);var payout=reader.GetString(4);
        if(payout=="Paid")paid+=amount;
        else if(payout=="ReadyForManualReview")ready+=amount;
        else if(payout=="Blocked")blocked+=amount;
        else if(payout=="Refunded")refunded+=amount;
        else if(payout is "OnHold" or "AwaitingVerification")pending+=amount;
        operations.Add(new{id=reader.GetInt64(0),channel=reader.GetString(1),amount,
           status=reader.GetString(3),payoutStatus=payout,
           publishedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5),
           eligibleAt=reader.IsDBNull(6)?(DateTime?)null:reader.GetDateTime(6)});
    }
    await using var history=db.CreateCommand("""
        SELECT a.id,c.title,a.price_rub,a.created_at,a.payment_status,a.payout_status,
               a.is_test_order,a.owner_id,a.advertiser_id
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        WHERE (a.owner_id=@user OR a.advertiser_id=@user)
          AND a.payment_status IN ('Paid','TestPaid')
        ORDER BY a.created_at DESC,a.id DESC LIMIT 300
        """);
    history.Parameters.Add(new MySqlParameter("@user",userId));
    var transactions=new List<object>();
    await using(var reader=await history.ExecuteReaderAsync(ct))
    while(await reader.ReadAsync(ct))
    {
        var isOwner=reader.GetInt64(7)==userId;
        var isBuyer=reader.GetInt64(8)==userId;
        var payment=reader.GetString(4);
        var isTest=reader.GetBoolean(6);
        // Own-channel self-tests must never be presented as real income.
        if(isOwner)transactions.Add(new{id=reader.GetInt64(0),channel=reader.GetString(1),
           amount=reader.GetDecimal(2),createdAt=reader.GetDateTime(3),
           type=isTest?"test_sale":"sale",payoutStatus=reader.GetString(5),paymentStatus=payment});
        if(isBuyer)transactions.Add(new{id=reader.GetInt64(0),channel=reader.GetString(1),
           amount=reader.GetDecimal(2),createdAt=reader.GetDateTime(3),
           type=isTest?"test_purchase":"purchase",payoutStatus=reader.GetString(5),paymentStatus=payment});
    }
    return Results.Ok(new{revenue,expenses,testPayments,paid,readyForReview=ready,pending,blocked,refunded,
      operations,transactions,note="Учитываются реальные оплаченные заказы; тестовые платежи показаны отдельно. Выплаты здесь не инициируются."});
});
app.MapPost("/api/web/channels/{channelId:long}/category",async(long channelId,UpdateCategoryInput input,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>{
 if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId)||userId<=0)return Results.Unauthorized();
 if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))return Results.StatusCode(403);
 var category=input.Category?.Trim()??"";
 var allowed=new HashSet<string>(StringComparer.Ordinal){"Технологии","Игры","Новости","Бизнес","Финансы","Образование","Развлечения","Юмор","Спорт","Путешествия","Авто","Красота и стиль","Здоровье","Кулинария","Музыка","Кино","Другое"};
 if(!allowed.Contains(category))return Results.BadRequest(new{error="Выберите категорию из списка"});
 await using var cmd=db.CreateCommand("UPDATE channels SET category=@category WHERE telegram_id=@id AND connected_by=@owner");
 cmd.Parameters.Add(new MySqlParameter("@category",category));cmd.Parameters.Add(new MySqlParameter("@id",channelId));cmd.Parameters.Add(new MySqlParameter("@owner",userId));
 return await cmd.ExecuteNonQueryAsync(ct)>0?Results.Ok(new{category}):Results.NotFound();
});
app.MapPost("/api/web/channels/{channelId:long}/alias",async(long channelId,ChannelAliasInput input,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>{
 if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId)||userId<=0)return Results.Unauthorized();
 if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))return Results.StatusCode(403);
 var alias=(input.Alias??"").Trim().ToLowerInvariant();
 if(!System.Text.RegularExpressions.Regex.IsMatch(alias,@"^[a-z0-9_]{4,32}$"))return Results.BadRequest(new{error="Используйте 4–32 латинских символа, цифры или _"});
 if(alias is "admin" or "api" or "app" or "help" or "catalog" or "login" or "support" or "reklio" or "account" or "pricing" or "terms" or "privacy")return Results.BadRequest(new{error="Этот адрес зарезервирован"});
 await using var conn=await db.OpenConnectionAsync(ct);
 await using var tx=await conn.BeginTransactionAsync(ct);
 await using(var verify=new MySqlCommand("SELECT telegram_id FROM channels WHERE telegram_id=@id AND connected_by=@owner FOR UPDATE",conn,tx)){
 verify.Parameters.AddWithValue("@id",channelId);verify.Parameters.AddWithValue("@owner",userId);
 if(await verify.ExecuteScalarAsync(ct) is null){await tx.RollbackAsync(ct);return Results.NotFound();}
 }
 try{
 await using(var remove=new MySqlCommand("DELETE FROM reklio_channel_aliases WHERE channel_id=@id",conn,tx)){
 remove.Parameters.AddWithValue("@id",channelId);await remove.ExecuteNonQueryAsync(ct);
 }
 await using(var cmd=new MySqlCommand("INSERT INTO reklio_channel_aliases(channel_id,alias) VALUES(@id,@alias)",conn,tx)){
 cmd.Parameters.AddWithValue("@id",channelId);cmd.Parameters.AddWithValue("@alias",alias);await cmd.ExecuteNonQueryAsync(ct);
 }
 await tx.CommitAsync(ct);
 return Results.Ok(new{alias,url="https://reklio.site/ad/"+alias});
 }catch(MySqlException ex) when(ex.Number==1062){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Этот адрес уже занят. Выберите другой."});}
});
app.MapGet("/api/web/channels/{channelId:long}/analytics", async (long channelId, HttpContext ctx, MySqlDataSource db, CancellationToken ct) =>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var owner)||owner<=0)return Results.Unauthorized();
    var days=int.TryParse(ctx.Request.Query["days"],out var requested) && requested is 7 or 30 or 90 ? requested : 30;
    await using var planCmd=db.CreateCommand("SELECT COALESCE(GREATEST(subscription_until,pro_granted_until),subscription_until,pro_granted_until) FROM users WHERE telegram_id=@id");
    planCmd.Parameters.Add(new MySqlParameter("@id",owner));
    var planUntil=await planCmd.ExecuteScalarAsync(ct);
    var analyticsPro=planUntil is DateTime expiry&&expiry>DateTime.UtcNow;
    if(!analyticsPro)days=7;
    await using var verify=db.CreateCommand("SELECT COUNT(*) FROM channels WHERE telegram_id=@channel AND connected_by=@owner");
    verify.Parameters.Add(new MySqlParameter("@channel",channelId));
    verify.Parameters.Add(new MySqlParameter("@owner",owner));
    if(Convert.ToInt32(await verify.ExecuteScalarAsync(ct))==0)return Results.NotFound();
    await using var metrics=db.CreateCommand("""
        SELECT m.day,m.subscribers,m.posts,
               (SELECT CAST(ROUND(AVG(v.views)) AS SIGNED)
                FROM reklio_post_views v JOIN channel_observed_posts op
                ON op.channel_id=v.channel_id AND op.message_id=v.message_id
                WHERE op.channel_id=m.channel_id AND DATE(op.posted_at)=m.day) AS avgViews
        FROM channel_daily_metrics m WHERE m.channel_id=@channel
        AND m.day>=UTC_DATE()-INTERVAL @days DAY ORDER BY m.day
        """);
    metrics.Parameters.Add(new MySqlParameter("@channel",channelId));
    metrics.Parameters.Add(new MySqlParameter("@days",days-1));
    var points=new List<object>();
    await using(var rd=await metrics.ExecuteReaderAsync(ct))
        while(await rd.ReadAsync(ct))points.Add(new {day=rd.GetDateTime(0).ToString("yyyy-MM-dd"),subscribers=rd.IsDBNull(1)?(int?)null:rd.GetInt32(1),posts=rd.GetInt32(2),avgViews=rd.IsDBNull(3)?(long?)null:rd.GetInt64(3)});
    await using var orders=db.CreateCommand("""
        SELECT COUNT(*),
          COALESCE(SUM(CASE WHEN status IN ('Published','Deleted','Disputed','DisputeResolved','EarlyDeletionPending') THEN 1 ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN payment_status='Paid' AND is_test_order=FALSE THEN price_rub ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN status='Pending' THEN 1 ELSE 0 END),0)
        FROM ad_requests WHERE channel_id=@channel AND created_at>=DATE_SUB(UTC_TIMESTAMP(),INTERVAL @days DAY)
        """);
    orders.Parameters.Add(new MySqlParameter("@channel",channelId));
    orders.Parameters.Add(new MySqlParameter("@days",days));
    long totalOrders,published,pending;decimal revenue;
    await using(var stats=await orders.ExecuteReaderAsync(ct)){
      await stats.ReadAsync(ct);totalOrders=stats.GetInt64(0);published=stats.GetInt64(1);
      revenue=stats.GetDecimal(2);pending=stats.GetInt64(3);
    }
    await using var previous=db.CreateCommand("""
      SELECT COUNT(*),COALESCE(SUM(CASE WHEN payment_status='Paid' AND is_test_order=FALSE THEN price_rub ELSE 0 END),0)
      FROM ad_requests WHERE channel_id=@channel
        AND created_at>=DATE_SUB(UTC_TIMESTAMP(),INTERVAL @twice DAY)
        AND created_at<DATE_SUB(UTC_TIMESTAMP(),INTERVAL @days DAY)
      """);
    previous.Parameters.Add(new MySqlParameter("@channel",channelId));
    previous.Parameters.Add(new MySqlParameter("@days",days));
    previous.Parameters.Add(new MySqlParameter("@twice",days*2));
    long previousOrders;decimal previousRevenue;
    await using(var rd=await previous.ExecuteReaderAsync(ct)){
      await rd.ReadAsync(ct);previousOrders=rd.GetInt64(0);previousRevenue=rd.GetDecimal(1);
    }
    await using var daily=db.CreateCommand("""
      SELECT DATE(created_at),COALESCE(SUM(price_rub),0)
      FROM ad_requests
      WHERE channel_id=@channel AND payment_status='Paid' AND is_test_order=FALSE
        AND created_at>=DATE_SUB(UTC_TIMESTAMP(),INTERVAL @days DAY)
      GROUP BY DATE(created_at) ORDER BY DATE(created_at)
      """);
    daily.Parameters.Add(new MySqlParameter("@channel",channelId));
    daily.Parameters.Add(new MySqlParameter("@days",days));
    var revenuePoints=new List<object>();
    await using(var rd=await daily.ExecuteReaderAsync(ct))
      while(await rd.ReadAsync(ct))
        revenuePoints.Add(new {day=rd.GetDateTime(0).ToString("yyyy-MM-dd"),amount=rd.GetDecimal(1)});
    return Results.Ok(new {days,points,orders=totalOrders,published,revenue,pending,
      previousOrders,previousRevenue,revenuePoints});
});
app.MapGet("/api/web/channels/{channelId:long}/orders",async(long channelId,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>{
 if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var userId)||userId<=0)return Results.Unauthorized();
 await using var cmd=db.CreateCommand("""
 SELECT a.id,a.status,a.price_rub,a.created_at,a.scheduled_at,a.payment_status
 FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
 WHERE c.telegram_id=@channel AND c.connected_by=@owner AND a.owner_id=@owner
 ORDER BY a.created_at DESC LIMIT 30
 """);
 cmd.Parameters.Add(new MySqlParameter("@channel",channelId));cmd.Parameters.Add(new MySqlParameter("@owner",userId));
 var items=new List<object>();
 await using var reader=await cmd.ExecuteReaderAsync(ct);
 while(await reader.ReadAsync(ct))items.Add(new{id=reader.GetInt64(0),status=reader.GetString(1),price=reader.GetDecimal(2),
 createdAt=reader.GetDateTime(3),scheduledAt=reader.IsDBNull(4)?(DateTime?)null:reader.GetDateTime(4),paymentStatus=reader.GetString(5)});
 return Results.Ok(items);
});
app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    var origin=ctx.Request.Headers.Origin.ToString();
    if(origin!="https://reklio.site"&&origin!="https://www.reklio.site")
        return Results.StatusCode(403);
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new {authenticated=false});
});

try
{
    var directDb=app.Services.GetRequiredService<MySqlDataSource>();
    await using var links=directDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_channel_direct_links(
          channel_id BIGINT NOT NULL PRIMARY KEY,
          owner_id BIGINT NOT NULL,
          payment_url VARCHAR(512) NOT NULL,
          updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          INDEX idx_direct_link_owner(owner_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await links.ExecuteNonQueryAsync();
    await using var claims=directDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_direct_payments(
          order_id BIGINT NOT NULL PRIMARY KEY,
          owner_id BIGINT NOT NULL,
          buyer_id BIGINT NOT NULL,
          status ENUM('BuyerClaimed','OwnerConfirmed','Rejected') NOT NULL,
          claimed_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          confirmed_at DATETIME NULL,
          INDEX idx_direct_owner(owner_id,status)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await claims.ExecuteNonQueryAsync();
    await using var paymentDetails=directDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_channel_payment_details(
          channel_id BIGINT NOT NULL PRIMARY KEY,
          owner_id BIGINT NOT NULL,
          card_number VARCHAR(19) NULL,
          phone VARCHAR(16) NULL,
          bank VARCHAR(64) NOT NULL,
          updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          INDEX idx_payment_details_owner(owner_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await paymentDetails.ExecuteNonQueryAsync();
    // A NULL blocked_until means a permanent block.
    await using var channelBlocks=directDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_channel_blocks(
          channel_id BIGINT NOT NULL PRIMARY KEY,
          blocked_until DATETIME NULL,
          order_id BIGINT NULL,
          blocked_by BIGINT NOT NULL,
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await channelBlocks.ExecuteNonQueryAsync();
}
catch(MySqlException ex){app.Logger.LogError(ex,"Direct seller payment schema bootstrap failed");}

// Earnings and withdrawal requests; no custody, wallet top-ups, or automatic transfers.
try
{
    var payoutDb=app.Services.GetRequiredService<MySqlDataSource>();
    await using var earn=payoutDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_owner_earnings(
          order_id BIGINT NOT NULL PRIMARY KEY,
          owner_id BIGINT NOT NULL,
          amount_rub DECIMAL(15,2) NOT NULL,
          credited_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          INDEX idx_owner_earnings_owner(owner_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await earn.ExecuteNonQueryAsync();
    await using var requests=payoutDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_payout_requests(
          id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
          owner_id BIGINT NOT NULL,
          amount_rub DECIMAL(15,2) NOT NULL,
          status ENUM('Pending','Paid','Rejected') NOT NULL DEFAULT 'Pending',
          created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
          processed_at DATETIME NULL,
          processed_by BIGINT NULL,
          transfer_reference VARCHAR(120) NULL,
          INDEX idx_payout_owner(owner_id,created_at),
          INDEX idx_payout_status(status,created_at)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await requests.ExecuteNonQueryAsync();
}
catch(MySqlException ex){app.Logger.LogError(ex,"Payout accounting schema bootstrap failed");}

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
    await using var createPromotions=staffDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS channel_promotions (
            channel_id BIGINT NOT NULL PRIMARY KEY,
            active_until DATETIME NOT NULL,
            updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            INDEX idx_promotions_active_until(active_until)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await createPromotions.ExecuteNonQueryAsync();
}
catch(MySqlException ex)
{
    app.Logger.LogWarning(ex,"Reklio staff schema could not be initialized; verify DB CREATE permission");
}

var likesAvailable=false;
try
{
    var likesDb=app.Services.GetRequiredService<MySqlDataSource>();
    await using var createLikes=likesDb.CreateCommand("""
        CREATE TABLE IF NOT EXISTS reklio_channel_likes (
            user_id BIGINT NOT NULL,
            channel_id BIGINT NOT NULL,
            created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY(user_id,channel_id),
            INDEX idx_channel_likes(channel_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """);
    await createLikes.ExecuteNonQueryAsync();
    likesAvailable=true;
}
catch(MySqlException ex)
{
    app.Logger.LogWarning(ex,"Could not initialize channel likes table");
}
try {
    var voteDb=app.Services.GetRequiredService<MySqlDataSource>();
    await using var initVotes=voteDb.CreateCommand("""
    CREATE TABLE IF NOT EXISTS reklio_channel_votes (
      user_id BIGINT NOT NULL,channel_id BIGINT NOT NULL,
      voted_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
      PRIMARY KEY(user_id,channel_id,voted_at),INDEX idx_vote_channel(channel_id,voted_at)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
    """);
    await initVotes.ExecuteNonQueryAsync();
} catch(MySqlException ex) {app.Logger.LogWarning(ex,"Votes table setup failed");}

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

long? Authorized(HttpRequest req)
{
    var initData=req.Headers["X-Telegram-Init-Data"].ToString();
    if(!string.IsNullOrEmpty(initData)) return VerifyTelegram(initData,token);
    // Browser cookie access is narrowly limited to existing customer-order actions.
    // Cross-origin requests must carry the exact trusted website Origin (CSRF defense).
    var path=req.Path.Value??"";
    if(!System.Text.RegularExpressions.Regex.IsMatch(path,
        @"^(?:/api/orders/[0-9]+/(?:reschedule|dispute|payment|payment/check|deal)|/api/my-channels/connect)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return null;
    if(req.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))return null;
    if(req.HttpContext.User.Identity?.IsAuthenticated!=true)return null;
    return long.TryParse(req.HttpContext.User.FindFirst("id")?.Value,out var id)&&id>0?id:null;
}
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

// Favorites are private to the Telegram account and are independent of channel listing state.
app.MapGet("/api/favorites", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long user) return Results.Unauthorized();
    await using var create=db.CreateCommand("CREATE TABLE IF NOT EXISTS reklio_favorites (user_id BIGINT NOT NULL,channel_id BIGINT NOT NULL,created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,PRIMARY KEY(user_id,channel_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
    await create.ExecuteNonQueryAsync(ct);
    await using var cmd=db.CreateCommand("SELECT f.channel_id FROM reklio_favorites f JOIN channels c ON c.telegram_id=f.channel_id AND c.is_listed=TRUE WHERE f.user_id=@user ORDER BY f.created_at DESC LIMIT 100");
    cmd.Parameters.Add(new MySqlParameter("@user",user));
    var ids=new List<long>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct)) ids.Add(reader.GetInt64(0));
    return Results.Ok(ids);
});
app.MapPost("/api/favorites/{channelId:long}", async (long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long user) return Results.Unauthorized();
    await using var create=db.CreateCommand("CREATE TABLE IF NOT EXISTS reklio_favorites (user_id BIGINT NOT NULL,channel_id BIGINT NOT NULL,created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,PRIMARY KEY(user_id,channel_id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
    await create.ExecuteNonQueryAsync(ct);
    await using var cmd=db.CreateCommand("INSERT IGNORE INTO reklio_favorites(user_id,channel_id) SELECT @user,telegram_id FROM channels WHERE telegram_id=@channel AND is_listed=TRUE");
    cmd.Parameters.Add(new MySqlParameter("@user",user));cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.Ok(new {favorite=true});
});
app.MapDelete("/api/favorites/{channelId:long}", async (long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long user) return Results.Unauthorized();
    await using var cmd=db.CreateCommand("DELETE FROM reklio_favorites WHERE user_id=@user AND channel_id=@channel");
    cmd.Parameters.Add(new MySqlParameter("@user",user));cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.Ok(new {favorite=false});
});
app.MapGet("/api/channels/{channelId:long}/reviews", async (long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is null) return Results.Unauthorized();
    await using var cmd=db.CreateCommand("SELECT stars,comment,created_at FROM channel_reviews WHERE channel_id=@id ORDER BY created_at DESC LIMIT 20");
    cmd.Parameters.Add(new MySqlParameter("@id",channelId));
    var reviews=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct)) reviews.Add(new {stars=reader.GetInt32(0),comment=reader.IsDBNull(1)?null:reader.GetString(1),createdAt=reader.GetDateTime(2)});
    return Results.Ok(reviews);
});
// Public discovery is read-only and exposes no owner or buyer identifiers.
// One reaction per Telegram account and channel; ranking is based on genuine reactions.
app.MapGet("/api/channel-likes",async(HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long user)return Results.Unauthorized();
    await using var cmd=db.CreateCommand("SELECT DISTINCT channel_id FROM reklio_channel_votes WHERE user_id=@user AND voted_at>UTC_TIMESTAMP()-INTERVAL 24 HOUR");
    cmd.Parameters.Add(new MySqlParameter("@user",user));
    var ids=new List<long>();
    await using var rd=await cmd.ExecuteReaderAsync(ct);
    while(await rd.ReadAsync(ct))ids.Add(rd.GetInt64(0));
    return Results.Ok(ids);
});
app.MapPost("/api/channel-likes/{channelId:long}",async(long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long user)return Results.Unauthorized();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using(var guard=new MySqlCommand("SELECT telegram_id FROM users WHERE telegram_id=@user FOR UPDATE",conn,tx))
    {
        guard.Parameters.AddWithValue("@user",user);
        if(await guard.ExecuteScalarAsync(ct) is null)return Results.Unauthorized();
    }
    await using(var allowed=new MySqlCommand("SELECT COUNT(*) FROM channels WHERE telegram_id=@channel AND is_listed=TRUE AND connected_by<>@user",conn,tx))
    {
        allowed.Parameters.AddWithValue("@user",user);
        allowed.Parameters.AddWithValue("@channel",channelId);
        if(Convert.ToInt32(await allowed.ExecuteScalarAsync(ct))==0)return Results.Conflict(new {error="Канал недоступен или принадлежит вам"});
    }
    await using(var recent=new MySqlCommand("SELECT COUNT(*) FROM reklio_channel_votes WHERE user_id=@user AND channel_id=@channel AND voted_at>UTC_TIMESTAMP()-INTERVAL 24 HOUR",conn,tx))
    {
        recent.Parameters.AddWithValue("@user",user);
        recent.Parameters.AddWithValue("@channel",channelId);
        if(Convert.ToInt64(await recent.ExecuteScalarAsync(ct))>0)return Results.Conflict(new {error="Вы уже голосовали за этот канал за последние 24 часа"});
    }
    await using(var insert=new MySqlCommand("INSERT INTO reklio_channel_votes(user_id,channel_id,voted_at) VALUES(@user,@channel,UTC_TIMESTAMP())",conn,tx))
    {
        insert.Parameters.AddWithValue("@user",user);
        insert.Parameters.AddWithValue("@channel",channelId);
        await insert.ExecuteNonQueryAsync(ct);
    }
    await tx.CommitAsync(ct);
    return Results.Ok(new {voted=true});
});
app.MapGet("/api/rankings/monthly",async(MySqlDataSource db,CancellationToken ct)=>
{
    var month=DateTime.UtcNow.ToString("yyyy-MM");
    var items=new List<object>();
    try
    {
        await using var cmd=db.CreateCommand("""
        SELECT c.telegram_id,c.title,c.category,COUNT(*) AS votes
        FROM reklio_channel_votes v JOIN channels c ON c.telegram_id=v.channel_id AND c.is_listed=TRUE
        WHERE v.voted_at>=DATE_FORMAT(UTC_TIMESTAMP(),'%Y-%m-01')
        GROUP BY c.telegram_id,c.title,c.category
        ORDER BY votes DESC,c.title ASC LIMIT 30
        """);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        var place=0;
        while(await reader.ReadAsync(ct))items.Add(new {
            place=++place,id=reader.GetInt64(0),title=reader.GetString(1),
            category=reader.IsDBNull(2)?"Другое":reader.GetString(2),votes=reader.GetInt64(3)
        });
    }
    catch(MySqlException ex){app.Logger.LogWarning("Monthly ranking unavailable: MySQL {Code}",ex.Number);}
    return Results.Ok(new {month,channels=items});
});
// Catalog base query deliberately uses only the channels table. Optional metrics and likes
// are enriched separately so a missing auxiliary table never takes the catalog offline.
async Task<List<CatalogChannel>> LoadCatalog(MySqlDataSource db,int max,CancellationToken ct)
{
    var rows=new List<CatalogChannel>();
    await using(var cmd=db.CreateCommand("""
        SELECT telegram_id,title,category,ad_price_rub,ad_description,username
        FROM channels WHERE is_listed=TRUE ORDER BY connected_at DESC LIMIT @limit
        """))
    {
        cmd.Parameters.Add(new MySqlParameter("@limit",max));
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
            rows.Add(new CatalogChannel(reader.GetInt64(0),reader.GetString(1),
                reader.IsDBNull(2)?"Другое":reader.GetString(2),
                reader.IsDBNull(3)?0m:reader.GetDecimal(3),
                reader.IsDBNull(4)?"":reader.GetString(4),
                reader.IsDBNull(5)?null:reader.GetString(5)));
    }
    if(rows.Count==0)return rows;
    var ids=string.Join(",",rows.Select(c=>c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    var byId=rows.ToDictionary(c=>c.Id);
    async Task Optional(string name,string sql,Action<System.Data.Common.DbDataReader,CatalogChannel> assign)
    {
        try
        {
            await using var cmd=db.CreateCommand(sql.Replace("@ids",ids,StringComparison.Ordinal));
            await using var reader=await cmd.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
                if(byId.TryGetValue(reader.GetInt64(0),out var channel))assign(reader,channel);
        }
        catch(MySqlException ex)
        {
            app.Logger.LogWarning("Catalog {Field} unavailable: MySQL {ErrorCode}",name,ex.Number);
        }
    }
    // Catalog prices must reflect the cheapest active rate, not the legacy channel base price.
    await Optional("rates","SELECT channel_id,MIN(price_rub) FROM channel_duration_rates WHERE channel_id IN (@ids) AND is_active=TRUE GROUP BY channel_id",
        (r,c)=>c.Price=r.GetDecimal(1));
    await Optional("likes","SELECT channel_id,COUNT(*) FROM reklio_channel_votes WHERE channel_id IN (@ids) AND voted_at>=DATE_FORMAT(UTC_TIMESTAMP(),'%Y-%m-01') GROUP BY channel_id",
        (r,c)=>c.Likes=r.GetInt64(1));
    await Optional("promotion","SELECT channel_id,active_until FROM channel_promotions WHERE channel_id IN (@ids) AND active_until>UTC_TIMESTAMP()",
        (r,c)=>c.PromotedUntil=r.GetDateTime(1));
    await Optional("subscribers", """
        SELECT m.channel_id,m.subscribers FROM channel_daily_metrics m
        JOIN (SELECT channel_id,MAX(day) AS last_day FROM channel_daily_metrics
            WHERE channel_id IN (@ids) AND subscribers IS NOT NULL GROUP BY channel_id) newest
        ON newest.channel_id=m.channel_id AND newest.last_day=m.day
        """,(r,c)=>c.Subscribers=r.IsDBNull(1)?null:r.GetInt32(1));
    await Optional("posts","SELECT channel_id,COALESCE(SUM(posts),0) FROM channel_daily_metrics WHERE channel_id IN (@ids) AND day>=UTC_DATE()-INTERVAL 29 DAY GROUP BY channel_id",
        (r,c)=>c.Posts30d=r.GetInt64(1));
    await Optional("views", """
        SELECT op.channel_id,CAST(ROUND(AVG(v.views)) AS SIGNED),COUNT(*)
        FROM reklio_post_views v
        JOIN channel_observed_posts op ON op.channel_id=v.channel_id AND op.message_id=v.message_id
        WHERE op.channel_id IN (@ids) AND op.posted_at>=UTC_TIMESTAMP()-INTERVAL 30 DAY
        GROUP BY op.channel_id
        """,(r,c)=>{c.AvgViews=r.IsDBNull(1)?null:r.GetInt64(1);c.ViewsSamples=r.GetInt64(2);});
    return rows.OrderByDescending(c=>c.PromotedUntil.HasValue).ThenByDescending(c=>c.Likes).ToList();
}
app.MapGet("/api/public/channels", async (HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    var limit=int.TryParse(req.Query["limit"],out var requested)?Math.Clamp(requested,1,60):40;
    try
    {
        var channels=await LoadCatalog(db,Math.Max(500,limit),ct);
        return Results.Ok(channels.Take(limit));
    }
    catch(Exception ex)
    {
        app.Logger.LogError(ex,"Public catalog request failed");
        return Results.Problem("Каталог временно недоступен",statusCode:503);
    }
});
// Proxy channel photos without exposing the Telegram bot token or file URLs to browsers.
app.MapGet("/api/public/channels/{id:long}/avatar", async (long id, MySqlDataSource db, IHttpClientFactory clients, IMemoryCache cache, CancellationToken ct) =>
{
    var cacheKey="channel-avatar:"+id;
    if(cache.TryGetValue(cacheKey,out byte[]? cached))
        return cached is null ? Results.NotFound() : Results.File(cached,"image/jpeg");
    await using var check=db.CreateCommand("SELECT 1 FROM channels WHERE telegram_id=@id AND is_listed=TRUE LIMIT 1");
    check.Parameters.Add(new MySqlParameter("@id",id));
    if(await check.ExecuteScalarAsync(ct) is null)return Results.NotFound();
    try
    {
        using var http=clients.CreateClient();
        http.Timeout=TimeSpan.FromSeconds(8);
        using var chat=await http.GetFromJsonAsync<JsonDocument>($"https://api.telegram.org/bot{token}/getChat?chat_id={id}",ct);
        var result=chat?.RootElement.GetProperty("result");
        if(result is null || !result.Value.TryGetProperty("photo",out var photo) ||
           !photo.TryGetProperty("small_file_id",out var fileId))
        {
            cache.Set<byte[]?>(cacheKey,null,TimeSpan.FromMinutes(15));
            return Results.NotFound();
        }
        var idValue=Uri.EscapeDataString(fileId.GetString()??"");
        using var file=await http.GetFromJsonAsync<JsonDocument>($"https://api.telegram.org/bot{token}/getFile?file_id={idValue}",ct);
        if(file is null || !file.RootElement.GetProperty("ok").GetBoolean() ||
           !file.RootElement.GetProperty("result").TryGetProperty("file_path",out var path))
            return Results.NotFound();
        using var response=await http.GetAsync($"https://api.telegram.org/file/bot{token}/{path.GetString()}",ct);
        if(!response.IsSuccessStatusCode || response.Content.Headers.ContentLength>1024*1024)return Results.NotFound();
        var bytes=await response.Content.ReadAsByteArrayAsync(ct);
        if(bytes.Length==0||bytes.Length>1024*1024)return Results.NotFound();
        cache.Set(cacheKey,bytes,TimeSpan.FromHours(2));
        return Results.File(bytes,"image/jpeg");
    }
    catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
    {
        app.Logger.LogWarning("Channel avatar unavailable for {ChannelId}: {Type}",id,ex.GetType().Name);
        return Results.NotFound();
    }
});
app.MapGet("/api/public/channels/{id:long}", async (long id,MySqlDataSource db,CancellationToken ct) =>
{
    var channels=await LoadCatalog(db,10000,ct);
    var channel=channels.FirstOrDefault(c=>c.Id==id);
    return channel is null?Results.NotFound():Results.Ok(channel);
});
app.MapGet("/api/public/channels/{id:long}/details", async (long id,MySqlDataSource db,CancellationToken ct) =>
{
    await using var cmd=db.CreateCommand("""
      SELECT r.duration_hours,r.price_rub FROM channel_duration_rates r
      JOIN channels c ON c.telegram_id=r.channel_id
      WHERE c.telegram_id=@id AND c.is_listed=TRUE AND r.is_active=TRUE
      ORDER BY r.duration_hours LIMIT 20
    """);
    cmd.Parameters.Add(new MySqlParameter("@id",id));
    var rates=new List<object>();
    await using(var reader=await cmd.ExecuteReaderAsync(ct))
       while(await reader.ReadAsync(ct))rates.Add(new {hours=reader.GetInt32(0),price=reader.GetDecimal(1)});
    await using var reviewsCmd=db.CreateCommand("""
      SELECT stars,comment,created_at FROM channel_reviews
      WHERE channel_id=@id AND EXISTS(SELECT 1 FROM channels c WHERE c.telegram_id=@id AND c.is_listed=TRUE)
      ORDER BY created_at DESC LIMIT 10
    """);
    reviewsCmd.Parameters.Add(new MySqlParameter("@id",id));
    var reviews=new List<object>();
    await using(var reader=await reviewsCmd.ExecuteReaderAsync(ct))
       while(await reader.ReadAsync(ct))reviews.Add(new {stars=reader.GetInt32(0),comment=reader.IsDBNull(1)?null:reader.GetString(1),createdAt=reader.GetDateTime(2)});
    return Results.Ok(new {rates,reviews});
});
app.MapGet("/api/channels", async (HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is null)return Results.Unauthorized();
    var search=req.Query["search"].ToString().Trim();
    var category=req.Query["category"].ToString().Trim();
    if(search.Length>80||category.Length>32)return Results.BadRequest();
    var limit=int.TryParse(req.Query["limit"],out var requested)?Math.Clamp(requested,1,100):100;
    var channels=await LoadCatalog(db,10000,ct);
    return Results.Ok(channels.Where(c=>(category.Length==0||c.Category==category)
        &&(search.Length==0||c.Title.Contains(search,StringComparison.OrdinalIgnoreCase))).Take(limit));
});
app.MapGet("/api/channels/{channelId:long}/history", async (long channelId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is null) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT m.day,m.subscribers,m.posts,
               (SELECT CAST(ROUND(AVG(v.views)) AS SIGNED)
                  FROM reklio_post_views v JOIN channel_observed_posts op
                    ON op.channel_id=v.channel_id AND op.message_id=v.message_id
                 WHERE op.channel_id=m.channel_id AND DATE(op.posted_at)=m.day) AS avg_views
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
            posts = reader.GetInt32(2),
            avgViews = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3)
        });
    return Results.Ok(history);
});
app.MapGet("/api/me/publication-events", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if(Authorized(req) is not long user) return Results.Unauthorized();
    await using var cmd=db.CreateCommand("""
        SELECT e.order_id,c.title,e.event_type,e.created_at
        FROM ad_order_events e JOIN ad_requests a ON a.id=e.order_id
        JOIN channels c ON c.telegram_id=a.channel_id
        WHERE a.owner_id=@user AND e.event_type IN ('EarlyDeletionDetected','EarlyDeletionRepublishRequested','EarlyDeletionDisputeOpened')
        ORDER BY e.created_at DESC LIMIT 30
        """);
    cmd.Parameters.Add(new MySqlParameter("@user",user));
    var events=new List<object>();
    await using var rd=await cmd.ExecuteReaderAsync(ct);
    while(await rd.ReadAsync(ct)) events.Add(new {orderId=rd.GetInt64(0),channel=rd.GetString(1),type=rd.GetString(2),createdAt=rd.GetDateTime(3)});
    return Results.Ok(events);
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
        operations.Add(new {
            id=reader.GetInt64(0),channel=reader.GetString(1),amount,net=amount,
            status=reader.GetString(3),payoutStatus=payout,
            publishedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5),
            eligibleAt=reader.IsDBNull(6)?(DateTime?)null:reader.GetDateTime(6)
        });
    }
    return Results.Ok(new {pending,readyForReview=ready,blocked,paid,refunded,
        readyNet=ready,operations,
        note="Reklio не взимает комиссию и не инициирует выплаты."});
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
      SELECT telegram_id,title,category,is_listed,connected_by,ad_price_rub,
        EXISTS(SELECT 1 FROM reklio_channel_blocks b WHERE b.channel_id=channels.telegram_id AND (b.blocked_until IS NULL OR b.blocked_until>UTC_TIMESTAMP())),
        (SELECT b.blocked_until FROM reklio_channel_blocks b WHERE b.channel_id=channels.telegram_id)
      FROM channels ORDER BY connected_at DESC LIMIT 100
      """);
    var channels=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))
        channels.Add(new {id=reader.GetInt64(0),title=reader.GetString(1),
          category=reader.IsDBNull(2)?"":reader.GetString(2),listed=reader.GetBoolean(3),
          ownerId=reader.GetInt64(4),price=reader.GetDecimal(5),
          blocked=reader.GetInt64(6)!=0,blockedUntil=reader.IsDBNull(7)?(DateTime?)null:reader.GetDateTime(7)});
    return Results.Ok(channels);
});
app.MapPost("/api/admin/channels/{channelId:long}/listing",async(long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(await StaffRole(db,uid,ct) is null) return Results.Forbid();
    var input=await req.ReadFromJsonAsync<UpdateListingInput>(cancellationToken:ct);
    if(input is null) return Results.BadRequest();
    if(input.Listed&&await ChannelIsBlocked(db,channelId,ct))return ChannelBlockedConflict();
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
        SELECT advertiser_id,id,CONCAT('🛡️ Проверка публикации · заказ #',id,CHAR(10),'Модератор изучил проблему.',CHAR(10),'📋 Результат доступен в разделе заказов.')
        FROM ad_requests WHERE id=@id
        UNION ALL
        SELECT owner_id,id,CONCAT('🛡️ Проверка публикации · заказ #',id,CHAR(10),'Проверка завершена.',CHAR(10),'⏳ Выплата пока заблокирована.')
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
static async Task<bool> ChannelIsBlocked(MySqlDataSource db,long channelId,CancellationToken ct)
{
    await using var cmd=db.CreateCommand("SELECT COUNT(*) FROM reklio_channel_blocks WHERE channel_id=@id AND (blocked_until IS NULL OR blocked_until>UTC_TIMESTAMP())");
    cmd.Parameters.Add(new MySqlParameter("@id",channelId));
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct))>0;
}
static IResult ChannelBlockedConflict()=>Results.Conflict(new {error="Канал заблокирован администрацией Reklio и не может быть опубликован в каталоге."});

// Money moves outside Reklio, so a decision cannot refund anyone: its only enforcement is the channel block.
app.MapPost("/api/admin/disputes/{orderId:long}/resolve",async(long orderId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long userId) return Results.Unauthorized();
    if(!IsRootAdmin(userId)) return Results.Forbid();
    var input=await req.ReadFromJsonAsync<ResolveDisputeInput>(cancellationToken:ct);
    if(input?.Decision is not ("BuyerRight" or "OwnerRight"))
        return Results.BadRequest(new {error="Недопустимое решение"});
    var sanction=input.Decision=="BuyerRight"?input.Sanction??"none":"none";
    if(sanction is not ("none" or "temporary" or "permanent"))
        return Results.BadRequest(new {error="Недопустимая санкция"});
    if(sanction=="temporary"&&input.Days is not (>=1 and <=365))
        return Results.BadRequest(new {error="Срок блокировки: от 1 до 365 дней"});
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("""
        UPDATE ad_requests SET status='DisputeResolved',payout_status='Blocked',
          dispute_resolution=@decision,dispute_resolved_at=UTC_TIMESTAMP(),
          dispute_resolved_by=@admin
        WHERE id=@id AND status='Disputed'
        """,conn,tx);
    cmd.Parameters.AddWithValue("@decision",input.Decision);
    cmd.Parameters.AddWithValue("@admin",userId);
    cmd.Parameters.AddWithValue("@id",orderId);
    if(await cmd.ExecuteNonQueryAsync(ct)!=1){
        await tx.RollbackAsync(ct);
        return Results.Conflict(new {error="Спор уже обработан или недоступен"});
    }
    if(sanction!="none")
    {
        long channelId;
        await using(var find=new MySqlCommand("SELECT channel_id FROM ad_requests WHERE id=@id",conn,tx))
        {
            find.Parameters.AddWithValue("@id",orderId);
            channelId=Convert.ToInt64(await find.ExecuteScalarAsync(ct));
        }
        // A permanent block is never shortened by a later temporary one; lifting it is a separate admin action.
        await using var block=new MySqlCommand("""
            INSERT INTO reklio_channel_blocks(channel_id,blocked_until,order_id,blocked_by)
            VALUES(@channel,IF(@days IS NULL,NULL,DATE_ADD(UTC_TIMESTAMP(),INTERVAL @days DAY)),@order,@admin)
            ON DUPLICATE KEY UPDATE order_id=VALUES(order_id),blocked_by=VALUES(blocked_by),
              blocked_until=IF(blocked_until IS NULL OR VALUES(blocked_until) IS NULL,NULL,GREATEST(blocked_until,VALUES(blocked_until)))
            """,conn,tx);
        block.Parameters.AddWithValue("@channel",channelId);
        block.Parameters.AddWithValue("@days",sanction=="temporary"?input.Days!.Value:DBNull.Value);
        block.Parameters.AddWithValue("@order",orderId);
        block.Parameters.AddWithValue("@admin",userId);
        await block.ExecuteNonQueryAsync(ct);
        await using var hide=new MySqlCommand("UPDATE channels SET is_listed=FALSE WHERE telegram_id=@channel",conn,tx);
        hide.Parameters.AddWithValue("@channel",channelId);
        await hide.ExecuteNonQueryAsync(ct);
        // Orders a buyer says were already paid stay open, so that buyer can still open a dispute.
        await using var warn=new MySqlCommand("""
            INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
            SELECT advertiser_id,id,CONCAT('❌ Заявка #',id,' отменена',CHAR(10),'Канал заблокирован администрацией Reklio. Не переводите деньги по этому заказу.')
            FROM ad_requests a
            WHERE a.channel_id=@channel AND a.status IN ('Pending','AwaitingPayment')
              AND NOT EXISTS(SELECT 1 FROM reklio_direct_payments p WHERE p.order_id=a.id AND p.status='BuyerClaimed')
            """,conn,tx);
        warn.Parameters.AddWithValue("@channel",channelId);
        await warn.ExecuteNonQueryAsync(ct);
        await using var cancel=new MySqlCommand("""
            UPDATE ad_requests a SET a.status='Rejected'
            WHERE a.channel_id=@channel AND a.status IN ('Draft','Pending','AwaitingPayment')
              AND NOT EXISTS(SELECT 1 FROM reklio_direct_payments p WHERE p.order_id=a.id AND p.status='BuyerClaimed')
            """,conn,tx);
        cancel.Parameters.AddWithValue("@channel",channelId);
        await cancel.ExecuteNonQueryAsync(ct);
    }
    var outcome=input.Decision=="OwnerRight"?"нарушение не подтверждено":
        sanction=="permanent"?"нарушение подтверждено, канал заблокирован навсегда":
        sanction=="temporary"?$"нарушение подтверждено, канал заблокирован на {input.Days} дн.":"нарушение подтверждено";
    await using var notice=new MySqlCommand("""
        INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
        SELECT advertiser_id,id,CONCAT('⚖️ Спор рассмотрен · заказ #',id,CHAR(10),'📋 Решение: ',@outcome)
        FROM ad_requests WHERE id=@id
        UNION ALL
        SELECT owner_id,id,CONCAT('⚖️ Спор рассмотрен · заказ #',id,CHAR(10),'📋 Решение: ',@outcome)
        FROM ad_requests WHERE id=@id
        """,conn,tx);
    notice.Parameters.AddWithValue("@id",orderId);
    notice.Parameters.AddWithValue("@outcome",outcome);
    await notice.ExecuteNonQueryAsync(ct);
    await using var audit=new MySqlCommand("INSERT INTO reklio_admin_audit(actor_id,action,target_id,details) VALUES(@actor,'DisputeResolved',@target,@details)",conn,tx);
    audit.Parameters.AddWithValue("@actor",userId);
    audit.Parameters.AddWithValue("@target",orderId);
    audit.Parameters.AddWithValue("@details",input.Decision+" "+sanction+(sanction=="temporary"?" "+input.Days:""));
    await audit.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {status="DisputeResolved",resolution=input.Decision,sanction});
});
// Lifting a block does not list the channel: its owner decides when to publish it again.
app.MapPost("/api/admin/channels/{channelId:long}/unblock",async(long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long uid) return Results.Unauthorized();
    if(!IsRootAdmin(uid)) return Results.Forbid();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var remove=new MySqlCommand("DELETE FROM reklio_channel_blocks WHERE channel_id=@id",conn,tx);
    remove.Parameters.AddWithValue("@id",channelId);
    if(await remove.ExecuteNonQueryAsync(ct)==0){await tx.RollbackAsync(ct);return Results.NotFound();}
    await using var audit=new MySqlCommand("INSERT INTO reklio_admin_audit(actor_id,action,target_id,details) VALUES(@actor,'ChannelUnblocked',@target,NULL)",conn,tx);
    audit.Parameters.AddWithValue("@actor",uid);
    audit.Parameters.AddWithValue("@target",channelId);
    await audit.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {id=channelId,blocked=false});
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
        UPDATE ad_requests a SET a.status='Disputed',a.payout_status='Blocked',a.dispute_reason=@reason,a.disputed_at=UTC_TIMESTAMP()
        WHERE a.id=@id AND a.advertiser_id=@buyer AND
        """+Reklio.Orders.OrderRules.Disputable,conn,tx);
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
        SELECT owner_id,id,CONCAT('⚖️ Открыт спор · заказ #',id,CHAR(10),'Покупатель сообщил о проблеме, решение примет администрация Reklio.',CHAR(10),'📋 Следите за решением в Reklio.')
        FROM ad_requests WHERE id=@id
        UNION ALL
        SELECT 992964625,id,CONCAT('⚖️ Новый спор · заказ #',id,CHAR(10),'Откройте панель модерации Reklio.')
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
    var status=reader.GetString(1);
    var paymentStatus=reader.GetString(2);
    var payoutStatus=reader.GetString(3);
    var isTest=reader.GetBoolean(4);
    return Results.Ok(new {
        orderId,sellerPrice,
        status,paymentStatus,payoutStatus,isTest,
        publishedAt=reader.IsDBNull(6)?(DateTime?)null:reader.GetDateTime(6),
        verifiedAt=reader.IsDBNull(7)?(DateTime?)null:reader.GetDateTime(7),
        eligibleAt=reader.IsDBNull(8)?(DateTime?)null:reader.GetDateTime(8),
        isSeller=reader.GetInt64(10)==userId,
        providerDealActive=false,
        message="Реклама оплачивается напрямую владельцу канала."
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
               a.content_text,a.scheduled_at,COALESCE(r.duration_hours,a.delete_after_hours),a.payment_status,a.dispute_reason,a.last_error,
               a.published_at,pay.status,pay.claimed_at,a.dispute_resolution,
               (SELECT u.username FROM users u WHERE u.telegram_id=IF(a.owner_id=@user,a.advertiser_id,a.owner_id)),
               (
        """ + Reklio.Orders.OrderRules.Disputable + """
               )
        FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
        LEFT JOIN channel_duration_rates r ON r.id=a.time_rate_id
        LEFT JOIN reklio_direct_payments pay ON pay.order_id=a.id
        WHERE (a.advertiser_id=@user OR a.owner_id=@user) AND a.status<>'Draft'
        ORDER BY a.created_at DESC LIMIT 50
        """);
    cmd.Parameters.Add(new MySqlParameter("@user", userId));
    var orders = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        var status = reader.GetString(3);
        var isOwner = reader.GetInt64(5) == userId;
        // The other party's contact is shared only once both sides have agreed to work together.
        var shareContact = status is not ("Pending" or "Rejected" or "Expired");
        orders.Add(new { id = reader.GetInt64(0), channel = reader.GetString(1),
            price = reader.GetDecimal(2), status, createdAt = reader.GetDateTime(4), isOwner,
            text = reader.IsDBNull(6) ? "" : reader.GetString(6),
            scheduledAt = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7),
            durationHours = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8),
            paymentStatus = reader.GetString(9),
            disputeReason = reader.IsDBNull(10) ? null : reader.GetString(10),
            publicationIssue = reader.IsDBNull(11) ? null : reader.GetString(11),
            publishedAt = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12),
            claimStatus = reader.IsDBNull(13) ? null : reader.GetString(13),
            claimedAt = reader.IsDBNull(14) ? (DateTime?)null : reader.GetDateTime(14),
            resolution = reader.IsDBNull(15) ? null : reader.GetString(15),
            counterpart = !shareContact || reader.IsDBNull(16) ? null : reader.GetString(16),
            canDispute = !isOwner && !reader.IsDBNull(17) && reader.GetInt64(17) != 0 });
    }
    return Results.Ok(orders);
});
app.MapGet("/api/my-channels/{channelId:long}/analytics", async (long channelId, HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long owner) return Results.Unauthorized();
    var days=int.TryParse(req.Query["days"],out var requested) && requested is 7 or 30 or 90 ? requested : 30;
    await using var planCmd=db.CreateCommand("SELECT COALESCE(GREATEST(subscription_until,pro_granted_until),subscription_until,pro_granted_until) FROM users WHERE telegram_id=@id");
    planCmd.Parameters.Add(new MySqlParameter("@id",owner));
    var planUntil=await planCmd.ExecuteScalarAsync(ct);
    var analyticsPro=planUntil is DateTime expiry&&expiry>DateTime.UtcNow;
    if(!analyticsPro)days=7;
    await using var verify=db.CreateCommand("SELECT COUNT(*) FROM channels WHERE telegram_id=@channel AND connected_by=@owner");
    verify.Parameters.Add(new MySqlParameter("@channel",channelId));
    verify.Parameters.Add(new MySqlParameter("@owner",owner));
    if(Convert.ToInt32(await verify.ExecuteScalarAsync(ct))==0)return Results.NotFound();
    await using var metrics=db.CreateCommand("""
        SELECT m.day,m.subscribers,m.posts,
               (SELECT CAST(ROUND(AVG(v.views)) AS SIGNED)
                FROM reklio_post_views v JOIN channel_observed_posts op
                ON op.channel_id=v.channel_id AND op.message_id=v.message_id
                WHERE op.channel_id=m.channel_id AND DATE(op.posted_at)=m.day) AS avgViews
        FROM channel_daily_metrics m WHERE m.channel_id=@channel
        AND m.day>=UTC_DATE()-INTERVAL @days DAY ORDER BY m.day
        """);
    metrics.Parameters.Add(new MySqlParameter("@channel",channelId));
    metrics.Parameters.Add(new MySqlParameter("@days",days-1));
    var points=new List<object>();
    await using(var rd=await metrics.ExecuteReaderAsync(ct))
        while(await rd.ReadAsync(ct))points.Add(new {day=rd.GetDateTime(0).ToString("yyyy-MM-dd"),subscribers=rd.IsDBNull(1)?(int?)null:rd.GetInt32(1),posts=rd.GetInt32(2),avgViews=rd.IsDBNull(3)?(long?)null:rd.GetInt64(3)});
    await using var orders=db.CreateCommand("""
        SELECT COUNT(*),
          COALESCE(SUM(CASE WHEN status IN ('Published','Deleted','Disputed','DisputeResolved','EarlyDeletionPending') THEN 1 ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN payment_status='Paid' AND is_test_order=FALSE THEN price_rub ELSE 0 END),0),
          COALESCE(SUM(CASE WHEN status='Pending' THEN 1 ELSE 0 END),0)
        FROM ad_requests WHERE channel_id=@channel AND created_at>=DATE_SUB(UTC_TIMESTAMP(),INTERVAL @days DAY)
        """);
    orders.Parameters.Add(new MySqlParameter("@channel",channelId));
    orders.Parameters.Add(new MySqlParameter("@days",days));
    await using var stats=await orders.ExecuteReaderAsync(ct);
    await stats.ReadAsync(ct);
    return Results.Ok(new {days,points,orders=stats.GetInt64(0),published=stats.GetInt64(1),
        revenue=stats.GetDecimal(2),pending=stats.GetInt64(3)});
});
app.MapPost("/api/my-channels/connect", async (HttpRequest req, MySqlDataSource db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    ConnectChannelRequest? input;
    try { input=await req.ReadFromJsonAsync<ConnectChannelRequest>(cancellationToken:ct); }
    catch (JsonException) { return Results.BadRequest(new {error="Неверный формат запроса"}); }
    var username=input?.Username?.Trim().TrimStart('@');
    if (string.IsNullOrWhiteSpace(username) || !System.Text.RegularExpressions.Regex.IsMatch(username,@"^[a-zA-Z][a-zA-Z0-9_]{4,31}$"))
        return Results.BadRequest(new {error="Укажите публичный @username канала (5–32 символа)"});
    using var http=clients.CreateClient();
    async Task<JsonDocument?> Telegram(string method,string parameters)
    {
        using var response=await http.GetAsync($"https://api.telegram.org/bot{token}/{method}?{parameters}",ct);
        var body=await response.Content.ReadAsStringAsync(ct);
        using var data=JsonDocument.Parse(body);
        return data.RootElement.TryGetProperty("ok",out var ok)&&ok.GetBoolean()
            ? JsonDocument.Parse(body) : null;
    }
    var chatParam="chat_id="+Uri.EscapeDataString("@"+username);
    using var chat=await Telegram("getChat",chatParam);
    if(chat is null)return Results.BadRequest(new {error="Канал не найден. Убедитесь, что он публичный, а бот добавлен в администраторы."});
    var info=chat.RootElement.GetProperty("result");
    if(info.GetProperty("type").GetString()!="channel")return Results.BadRequest(new {error="Нужен Telegram-канал, не группа"});
    var channelId=info.GetProperty("id").GetInt64();
    using var owner=await Telegram("getChatMember", "chat_id="+channelId+"&user_id="+userId);
    var ownerStatus=owner?.RootElement.GetProperty("result").GetProperty("status").GetString();
    if(ownerStatus is not ("creator" or "administrator"))
        return Results.BadRequest(new {error="Вы должны быть администратором этого канала"});
    using var me=await Telegram("getMe","");
    if(me is null)return Results.Json(new {error="Не удалось проверить бота" },statusCode:503);
    var botId=me.RootElement.GetProperty("result").GetProperty("id").GetInt64();
    using var botMember=await Telegram("getChatMember","chat_id="+channelId+"&user_id="+botId);
    var botStatus=botMember?.RootElement.GetProperty("result").GetProperty("status").GetString();
    var canPost=botMember is not null && botMember.RootElement.GetProperty("result").TryGetProperty("can_post_messages",out var post)&&post.ValueKind==JsonValueKind.True;
    if(botStatus!="administrator"||!canPost)
        return Results.BadRequest(new {error="Добавьте @ReklioBot в администраторы канала с правом публикации"});
    var title=info.GetProperty("title").GetString()??username;
    await using var connection=await db.OpenConnectionAsync(ct);
    await using var tx=await connection.BeginTransactionAsync(ct);
    await using var access=new MySqlCommand("SELECT first_channel_at, COALESCE(GREATEST(subscription_until,pro_granted_until),subscription_until,pro_granted_until) FROM users WHERE telegram_id=@id FOR UPDATE",connection,tx);
    access.Parameters.Add(new MySqlParameter("@id",userId));
    DateTime? proUntil;
    await using(var reader=await access.ExecuteReaderAsync(ct))
    {
        if(!await reader.ReadAsync(ct))return Results.BadRequest(new {error="Откройте профиль Reklio и попробуйте снова"});
        proUntil=reader.IsDBNull(1)?null:reader.GetDateTime(1);
    }
    bool pro=proUntil>DateTime.UtcNow;
    await using var count=new MySqlCommand("SELECT COUNT(*) FROM channels WHERE connected_by=@id",connection,tx);
    count.Parameters.Add(new MySqlParameter("@id",userId));
    var total=Convert.ToInt32(await count.ExecuteScalarAsync(ct));
    await using var existing=new MySqlCommand("SELECT connected_by FROM channels WHERE telegram_id=@ch FOR UPDATE",connection,tx);
    existing.Parameters.Add(new MySqlParameter("@ch",channelId));
    var ownerId=await existing.ExecuteScalarAsync(ct);
    if(ownerId is not null&&Convert.ToInt64(ownerId)!=userId)return Results.Conflict(new {error="Канал уже подключён другим пользователем"});
    if(ownerId is null&&total>=(pro?10:1))return Results.BadRequest(new {error="Лимит каналов исчерпан. Для подключения дополнительных каналов нужен PRO"});
    await using var save=new MySqlCommand("INSERT INTO channels (telegram_id,title,username,connected_by) VALUES (@ch,@title,@username,@id) ON DUPLICATE KEY UPDATE title=VALUES(title),username=VALUES(username)",connection,tx);
    save.Parameters.Add(new MySqlParameter("@ch",channelId));
    save.Parameters.Add(new MySqlParameter("@title",title.Length>255?title[..255]:title));
    save.Parameters.Add(new MySqlParameter("@username",username));
    save.Parameters.Add(new MySqlParameter("@id",userId));
    await save.ExecuteNonQueryAsync(ct);
    await using var initial=new MySqlCommand("UPDATE users SET first_channel_at=UTC_TIMESTAMP() WHERE telegram_id=@id AND first_channel_at IS NULL",connection,tx);
    initial.Parameters.Add(new MySqlParameter("@id",userId));
    await initial.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {id=channelId,title});
});

// Tribute Pro checkout: payment state is always verified server-to-server.
await using (var tributeSchema=app.Services.GetRequiredService<MySqlDataSource>().CreateCommand("""
CREATE TABLE IF NOT EXISTS reklio_tribute_payments (
 order_uuid CHAR(36) NOT NULL PRIMARY KEY,
 user_id BIGINT NOT NULL,
 product VARCHAR(32) NOT NULL,
 amount_kopecks BIGINT NOT NULL,
 status VARCHAR(16) NOT NULL DEFAULT 'pending',
 created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
 applied_at DATETIME NULL,
 INDEX idx_tribute_user(user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
""")) await tributeSchema.ExecuteNonQueryAsync();

// Tribute Shop webhook: authenticate raw payload before accepting an event.
// Fulfilment is performed by the reconciliation worker after independently checking Tribute's order status.
app.MapPost("/api/tribute/webhook",async(HttpRequest request,CancellationToken ct)=>{
 var secret=Environment.GetEnvironmentVariable("TRIBUTE_API_KEY");
 if(string.IsNullOrWhiteSpace(secret))return Results.StatusCode(503);
 var signature=request.Headers["trbt-signature"].ToString().Trim();
 if(signature.StartsWith("sha256=",StringComparison.OrdinalIgnoreCase))signature=signature[7..];
 if(signature.Length!=64)return Results.Unauthorized();
 byte[] provided;
 try{provided=Convert.FromHexString(signature);}
 catch(FormatException){return Results.Unauthorized();}
 using var buffer=new MemoryStream();
 await request.Body.CopyToAsync(buffer,ct);
 if(buffer.Length>65536)return Results.StatusCode(413);
 var expected=HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),buffer.ToArray());
 if(!CryptographicOperations.FixedTimeEquals(expected,provided))return Results.Unauthorized();
 // A valid HMAC authenticates the exact payload, including Tribute test events.
 // Do not require a particular event property: test and transaction payloads can differ.
 try
 {
  using var json=JsonDocument.Parse(buffer.ToArray());
  if(json.RootElement.ValueKind!=JsonValueKind.Object)return Results.BadRequest();
 }
 catch(JsonException){return Results.BadRequest();}
 return Results.Ok(new{status="ok"});
});

// Safe diagnostic: validates Tribute API credentials without creating a paid order.
app.MapGet("/api/tribute/diagnostics",async(HttpRequest req,MySqlDataSource db,IHttpClientFactory clients,CancellationToken ct)=>
{
 if(Authorized(req) is not long userId)return Results.Unauthorized();
 if(await StaffRole(db,userId,ct)!="owner")return Results.StatusCode(403);
 var key=Environment.GetEnvironmentVariable("TRIBUTE_API_KEY");
 if(string.IsNullOrWhiteSpace(key))return Results.Ok(new{configured=false,apiReachable=false,message="TRIBUTE_API_KEY отсутствует"});
 try
 {
  var tribute=new Reklio.Payments.TributeShopClient(clients.CreateClient(),key);
  using var shops=await tribute.GetShopsAsync(ct);
  return Results.Ok(new{configured=true,apiReachable=true,message="Tribute API отвечает",responseKind=shops.RootElement.ValueKind.ToString()});
 }
 catch(HttpRequestException e)
 {
  return Results.Ok(new{configured=true,apiReachable=false,message="Tribute API вернул ошибку",httpStatus=e.StatusCode is null?(int?)null:(int)e.StatusCode.Value});
 }
 catch(JsonException)
 {
  return Results.Ok(new{configured=true,apiReachable=false,message="Некорректный JSON от Tribute"});
 }
});

app.MapPost("/api/tribute/pro/checkout", async (HttpRequest req, MySqlDataSource db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    var key=Environment.GetEnvironmentVariable("TRIBUTE_API_KEY");
    if (string.IsNullOrWhiteSpace(key)) return Results.Problem("Tribute is not configured",statusCode:503);
    try
    {
        var tribute=new Reklio.Payments.TributeShopClient(clients.CreateClient(),key);
        int price;
        await using(var priceCmd=db.CreateCommand("SELECT price_rub FROM reklio_service_prices WHERE service_key='pro30'"))price=Convert.ToInt32(await priceCmd.ExecuteScalarAsync(ct));
        using var order=await tribute.CreateOrderAsync(price*100,"Reklio Pro","Доступ Pro на 30 дней",userId.ToString(),
            "https://reklio.site/profile","https://reklio.site/profile",ct);
        var root=order.RootElement;
        var uuid=root.GetProperty("uuid").GetString();
        var url=root.TryGetProperty("webappPaymentUrl",out var webUrl)&&webUrl.ValueKind==JsonValueKind.String
            ? webUrl.GetString():null;
        url ??= root.TryGetProperty("paymentUrl",out var paymentUrl)&&paymentUrl.ValueKind==JsonValueKind.String
            ? paymentUrl.GetString():null;
        if(!Guid.TryParse(uuid,out _)||!Uri.TryCreate(url,UriKind.Absolute,out var parsed)||parsed.Scheme!="https")
            return Results.Problem("Tribute returned an invalid checkout response",statusCode:502);
        await using var save=db.CreateCommand("""
            INSERT INTO reklio_tribute_payments(order_uuid,user_id,product,amount_kopecks,status)
            VALUES(@uuid,@user,'pro30',@amount,'pending')
            """);
        ((MySqlConnector.MySqlParameterCollection)save.Parameters).AddWithValue("@uuid",uuid);
        ((MySqlConnector.MySqlParameterCollection)save.Parameters).AddWithValue("@user",userId);
        ((MySqlConnector.MySqlParameterCollection)save.Parameters).AddWithValue("@amount",price*100);
        await save.ExecuteNonQueryAsync(ct);
        return Results.Ok(new {orderUuid=uuid,paymentUrl=url});
    }
    catch(HttpRequestException e) {return Results.Problem($"Tribute order creation failed (provider HTTP {(e.StatusCode is null ? "network" : ((int)e.StatusCode.Value).ToString())})",statusCode:502);}
});

app.MapPost("/api/tribute/pro/confirm/{orderUuid:guid}",async(Guid orderUuid,HttpRequest req,MySqlDataSource db,IHttpClientFactory clients,CancellationToken ct)=>
{
    if(Authorized(req) is not long userId)return Results.Unauthorized();
    var key=Environment.GetEnvironmentVariable("TRIBUTE_API_KEY");
    if(string.IsNullOrWhiteSpace(key))return Results.StatusCode(503);
    await using var connection=await db.OpenConnectionAsync(ct);
    await using var tx=await connection.BeginTransactionAsync(ct);
    await using var check=new MySqlCommand("""
        SELECT status FROM reklio_tribute_payments WHERE order_uuid=@uuid AND user_id=@user AND product='pro30' FOR UPDATE
        """,connection,tx);
    ((MySqlConnector.MySqlParameterCollection)check.Parameters).AddWithValue("@uuid",orderUuid.ToString());
    ((MySqlConnector.MySqlParameterCollection)check.Parameters).AddWithValue("@user",userId);
    var existing=await check.ExecuteScalarAsync(ct) as string;
    if(existing is null)return Results.NotFound();
    if(existing=="paid")return Results.Ok(new{status="paid"});
    try
    {
        var tribute=new Reklio.Payments.TributeShopClient(clients.CreateClient(),key);
        using var status=await tribute.GetOrderStatusAsync(orderUuid,ct);
        var state=status.RootElement.GetProperty("status").GetString();
        if(state!="paid")return Results.Ok(new{status=state});
        await using var update=new MySqlCommand("""
            UPDATE users SET subscription_until=DATE_ADD(
              GREATEST(COALESCE(subscription_until,UTC_TIMESTAMP()),UTC_TIMESTAMP()),INTERVAL 30 DAY)
            WHERE telegram_id=@user
            """,connection,tx);
        ((MySqlConnector.MySqlParameterCollection)update.Parameters).AddWithValue("@user",userId);
        if(await update.ExecuteNonQueryAsync(ct)==0)
        {
            await using var exists=new MySqlCommand("SELECT COUNT(*) FROM users WHERE telegram_id=@user",connection,tx);
            ((MySqlConnector.MySqlParameterCollection)exists.Parameters).AddWithValue("@user",userId);
            if(Convert.ToInt64(await exists.ExecuteScalarAsync(ct))==0)return Results.NotFound();
        }
        await using var mark=new MySqlCommand("UPDATE reklio_tribute_payments SET status='paid',applied_at=UTC_TIMESTAMP() WHERE order_uuid=@uuid",connection,tx);
        ((MySqlConnector.MySqlParameterCollection)mark.Parameters).AddWithValue("@uuid",orderUuid.ToString());
        await mark.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new{status="paid"});
    }
    catch(HttpRequestException){return Results.Problem("Tribute verification failed",statusCode:502);}
});

await using(var schema=app.Services.GetRequiredService<MySqlDataSource>().CreateCommand("CREATE TABLE IF NOT EXISTS reklio_service_prices (service_key VARCHAR(32) PRIMARY KEY,price_rub INT NOT NULL) ENGINE=InnoDB"))await schema.ExecuteNonQueryAsync();
await using(var defaults=app.Services.GetRequiredService<MySqlDataSource>().CreateCommand("INSERT IGNORE INTO reklio_service_prices(service_key,price_rub) VALUES('pro30',399),('promotion7',199)"))await defaults.ExecuteNonQueryAsync();
// Mini App can resume a payment after returning from Tribute.
app.MapGet("/api/tribute/my-payments",async(HttpRequest req,MySqlDataSource db,CancellationToken ct)=>{
 if(Authorized(req) is not long userId)return Results.Unauthorized();
 var result=new List<object>();
 await using(var cmd=db.CreateCommand("""
 SELECT order_uuid,product,amount_kopecks,status,created_at,applied_at
 FROM reklio_tribute_payments WHERE user_id=@user ORDER BY created_at DESC LIMIT 20
 """)){
  ((MySqlConnector.MySqlParameterCollection)cmd.Parameters).AddWithValue("@user",userId);
  await using var reader=await cmd.ExecuteReaderAsync(ct);
  while(await reader.ReadAsync(ct))result.Add(new{orderUuid=reader.GetString(0),product=reader.GetString(1),amountKopecks=reader.GetInt64(2),status=reader.GetString(3),createdAt=reader.GetDateTime(4),appliedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5)});
 }
 await using(var cmd=db.CreateCommand("""
 SELECT order_uuid,channel_id,amount_kopecks,status,created_at,applied_at
 FROM reklio_tribute_promotions WHERE user_id=@user ORDER BY created_at DESC LIMIT 20
 """)){
  ((MySqlConnector.MySqlParameterCollection)cmd.Parameters).AddWithValue("@user",userId);
  await using var reader=await cmd.ExecuteReaderAsync(ct);
  while(await reader.ReadAsync(ct))result.Add(new{orderUuid=reader.GetString(0),product="promotion7",channelId=reader.GetInt64(1),amountKopecks=reader.GetInt64(2),status=reader.GetString(3),createdAt=reader.GetDateTime(4),appliedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5)});
 }
 return Results.Ok(result);
});
app.MapGet("/api/service-prices",async(MySqlDataSource db,CancellationToken ct)=>{
 await using var cmd=db.CreateCommand("SELECT service_key,price_rub FROM reklio_service_prices");
 var prices=new Dictionary<string,int>();
 await using var reader=await cmd.ExecuteReaderAsync(ct);
 while(await reader.ReadAsync(ct))prices[reader.GetString(0)]=reader.GetInt32(1);
 return Results.Ok(prices);
});
app.MapPut("/api/admin/service-prices/{serviceKey}",async(string serviceKey,UpdateServicePriceInput input,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>{
 if(Authorized(req) is not long uid)return Results.Unauthorized();
 if(await StaffRole(db,uid,ct)!="owner")return Results.Forbid();
 if(serviceKey is not ("pro30" or "promotion7")||input.PriceRub<1||input.PriceRub>100000)return Results.BadRequest();
 await using var cmd=db.CreateCommand("UPDATE reklio_service_prices SET price_rub=@price WHERE service_key=@key");
 ((MySqlConnector.MySqlParameterCollection)cmd.Parameters).AddWithValue("@price",input.PriceRub);((MySqlConnector.MySqlParameterCollection)cmd.Parameters).AddWithValue("@key",serviceKey);
 await cmd.ExecuteNonQueryAsync(ct);
 return Results.Ok(new{serviceKey,priceRub=input.PriceRub});
});
// Paid channel promotion. Price is configured server-side, never supplied by the client.
await using(var promoSchema=app.Services.GetRequiredService<MySqlDataSource>().CreateCommand("""
CREATE TABLE IF NOT EXISTS reklio_tribute_promotions (
 order_uuid CHAR(36) PRIMARY KEY,
 user_id BIGINT NOT NULL,
 channel_id BIGINT NOT NULL,
 days INT NOT NULL,
 amount_kopecks BIGINT NOT NULL,
 status VARCHAR(16) NOT NULL DEFAULT 'pending',
 created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
 applied_at DATETIME NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
"""))await promoSchema.ExecuteNonQueryAsync();

app.MapPost("/api/tribute/promotions/{channelId:long}/checkout",async(long channelId,HttpRequest req,MySqlDataSource db,IHttpClientFactory clients,CancellationToken ct)=>
{
 if(Authorized(req) is not long owner)return Results.Unauthorized();
 var key=Environment.GetEnvironmentVariable("TRIBUTE_API_KEY");
 if(string.IsNullOrWhiteSpace(key))return Results.StatusCode(503);
 int price;
 await using(var priceCmd=db.CreateCommand("SELECT price_rub FROM reklio_service_prices WHERE service_key='promotion7'"))price=Convert.ToInt32(await priceCmd.ExecuteScalarAsync(ct));
 await using(var check=db.CreateCommand("SELECT COUNT(*) FROM channels WHERE telegram_id=@id AND connected_by=@owner")){
  ((MySqlConnector.MySqlParameterCollection)check.Parameters).AddWithValue("@id",channelId);((MySqlConnector.MySqlParameterCollection)check.Parameters).AddWithValue("@owner",owner);
  if(Convert.ToInt64(await check.ExecuteScalarAsync(ct))==0)return Results.NotFound();
 }
 try{
  var tribute=new Reklio.Payments.TributeShopClient(clients.CreateClient(),key);
  using var order=await tribute.CreateOrderAsync(price*100,"Продвижение Reklio","Выделение канала в каталоге на 7 дней",owner.ToString(),
    "https://reklio.site/profile","https://reklio.site/profile",ct);
  var root=order.RootElement;
  var uuid=root.GetProperty("uuid").GetString();
  var url=root.TryGetProperty("webappPaymentUrl",out var web)&&web.ValueKind==JsonValueKind.String?web.GetString():null;
  if(string.IsNullOrWhiteSpace(url))url=root.TryGetProperty("paymentUrl",out var payment)&&payment.ValueKind==JsonValueKind.String?payment.GetString():null;
  if(!Guid.TryParse(uuid,out _)||!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https")
   return Results.Problem("Invalid Tribute checkout response",statusCode:502);
  await using var save=db.CreateCommand("""
    INSERT INTO reklio_tribute_promotions(order_uuid,user_id,channel_id,days,amount_kopecks)
    VALUES(@uuid,@owner,@channel,7,@amount)
    """);
  ((MySqlConnector.MySqlParameterCollection)save.Parameters).AddWithValue("@uuid",uuid);((MySqlConnector.MySqlParameterCollection)save.Parameters).AddWithValue("@owner",owner);
  ((MySqlConnector.MySqlParameterCollection)save.Parameters).AddWithValue("@channel",channelId);((MySqlConnector.MySqlParameterCollection)save.Parameters).AddWithValue("@amount",price*100);
  await save.ExecuteNonQueryAsync(ct);
  return Results.Ok(new{orderUuid=uuid,paymentUrl=url});
 }catch(HttpRequestException e){return Results.Problem($"Tribute order creation failed (provider HTTP {(e.StatusCode is null ? "network" : ((int)e.StatusCode.Value).ToString())})",statusCode:502);}
});

app.MapPost("/api/tribute/promotions/confirm/{orderUuid:guid}",async(Guid orderUuid,HttpRequest req,MySqlDataSource db,IHttpClientFactory clients,CancellationToken ct)=>
{
 if(Authorized(req) is not long owner)return Results.Unauthorized();
 var key=Environment.GetEnvironmentVariable("TRIBUTE_API_KEY");
 if(string.IsNullOrWhiteSpace(key))return Results.StatusCode(503);
 await using var conn=await db.OpenConnectionAsync(ct);
 await using var tx=await conn.BeginTransactionAsync(ct);
 await using var check=new MySqlCommand("""
   SELECT channel_id,days,status FROM reklio_tribute_promotions
   WHERE order_uuid=@uuid AND user_id=@owner FOR UPDATE
   """,conn,tx);
 ((MySqlConnector.MySqlParameterCollection)check.Parameters).AddWithValue("@uuid",orderUuid.ToString());((MySqlConnector.MySqlParameterCollection)check.Parameters).AddWithValue("@owner",owner);
 long channel;int days;string state;
 await using(var reader=await check.ExecuteReaderAsync(ct)){
  if(!await reader.ReadAsync(ct))return Results.NotFound();
  channel=reader.GetInt64(0);days=reader.GetInt32(1);state=reader.GetString(2);
 }
 if(state=="paid")return Results.Ok(new{status="paid"});
 try{
  var tribute=new Reklio.Payments.TributeShopClient(clients.CreateClient(),key);
  using var result=await tribute.GetOrderStatusAsync(orderUuid,ct);
  var status=result.RootElement.GetProperty("status").GetString();
  if(status!="paid")return Results.Ok(new{status});
  await using var apply=new MySqlCommand("""
   INSERT INTO channel_promotions(channel_id,active_until)
   VALUES(@channel,DATE_ADD(UTC_TIMESTAMP(),INTERVAL @days DAY))
   ON DUPLICATE KEY UPDATE active_until=DATE_ADD(GREATEST(active_until,UTC_TIMESTAMP()),INTERVAL @days DAY)
   """,conn,tx);
  ((MySqlConnector.MySqlParameterCollection)apply.Parameters).AddWithValue("@channel",channel);((MySqlConnector.MySqlParameterCollection)apply.Parameters).AddWithValue("@days",days);
  await apply.ExecuteNonQueryAsync(ct);
  await using var mark=new MySqlCommand("UPDATE reklio_tribute_promotions SET status='paid',applied_at=UTC_TIMESTAMP() WHERE order_uuid=@uuid",conn,tx);
  ((MySqlConnector.MySqlParameterCollection)mark.Parameters).AddWithValue("@uuid",orderUuid.ToString());await mark.ExecuteNonQueryAsync(ct);
  await tx.CommitAsync(ct);
  return Results.Ok(new{status="paid"});
 }catch(HttpRequestException){return Results.Problem("Tribute verification failed",statusCode:502);}
});

// PayAnyWay sells Reklio's own services. Only the signed Pay URL notification proves a payment:
// the buyer can edit the form amount, so the amount, currency and mode are checked against the order too.
await using(var payAnyWaySchema=app.Services.GetRequiredService<MySqlDataSource>().CreateCommand("""
CREATE TABLE IF NOT EXISTS reklio_payanyway_orders (
 transaction_id CHAR(36) NOT NULL PRIMARY KEY,
 user_id BIGINT NOT NULL,
 product VARCHAR(32) NOT NULL,
 channel_id BIGINT NULL,
 days INT NOT NULL,
 amount_rub DECIMAL(10,2) NOT NULL,
 test_mode BOOLEAN NOT NULL,
 status VARCHAR(16) NOT NULL DEFAULT 'pending',
 operation_id VARCHAR(64) NULL,
 created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
 paid_at DATETIME NULL,
 INDEX idx_payanyway_user(user_id,created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
"""))await payAnyWaySchema.ExecuteNonQueryAsync();

static (string Account,string Code)? PayAnyWayConfig()
{
 var account=Environment.GetEnvironmentVariable("PAYANYWAY_ACCOUNT_ID");
 var code=Environment.GetEnvironmentVariable("PAYANYWAY_INTEGRITY_CODE");
 return string.IsNullOrWhiteSpace(account)||string.IsNullOrWhiteSpace(code)?null:(account,code);
}

// The price is read on the server and never supplied by the client.
static async Task<IResult> PayAnyWayCheckout(MySqlDataSource db,long userId,string product,long? channelId,int days,string description,CancellationToken ct)
{
 if(PayAnyWayConfig() is not {} config)return Results.Problem("Оплата временно недоступна",statusCode:503);
 decimal amount;
 await using(var priceCmd=db.CreateCommand("SELECT price_rub FROM reklio_service_prices WHERE service_key=@key"))
 {
  priceCmd.Parameters.Add(new MySqlParameter("@key",product));
  amount=Convert.ToDecimal(await priceCmd.ExecuteScalarAsync(ct));
 }
 var transactionId=Guid.NewGuid().ToString("D");
 var testMode=Environment.GetEnvironmentVariable("PAYANYWAY_TEST_MODE")=="1";
 await using var save=db.CreateCommand("""
  INSERT INTO reklio_payanyway_orders(transaction_id,user_id,product,channel_id,days,amount_rub,test_mode)
  VALUES(@id,@user,@product,@channel,@days,@amount,@test)
  """);
 save.Parameters.Add(new MySqlParameter("@id",transactionId));
 save.Parameters.Add(new MySqlParameter("@user",userId));
 save.Parameters.Add(new MySqlParameter("@product",product));
 save.Parameters.Add(new MySqlParameter("@channel",(object?)channelId??DBNull.Value));
 save.Parameters.Add(new MySqlParameter("@days",days));
 save.Parameters.Add(new MySqlParameter("@amount",amount));
 save.Parameters.Add(new MySqlParameter("@test",testMode));
 await save.ExecuteNonQueryAsync(ct);
 return Results.Ok(new{orderUuid=transactionId,paymentUrl=Reklio.Payments.PayAnyWay.PaymentUrl(
   config.Account,config.Code,transactionId,amount,userId,testMode,description,"https://reklio.site/profile")});
}

app.MapPost("/api/payments/checkout/pro",async(HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
 if(Authorized(req) is not long userId)return Results.Unauthorized();
 return await PayAnyWayCheckout(db,userId,"pro30",null,30,"Reklio Pro, доступ на 30 дней",ct);
});
app.MapPost("/api/payments/checkout/promotion/{channelId:long}",async(long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
 if(Authorized(req) is not long owner)return Results.Unauthorized();
 await using(var check=db.CreateCommand("SELECT COUNT(*) FROM channels WHERE telegram_id=@id AND connected_by=@owner"))
 {
  check.Parameters.Add(new MySqlParameter("@id",channelId));
  check.Parameters.Add(new MySqlParameter("@owner",owner));
  if(Convert.ToInt64(await check.ExecuteScalarAsync(ct))==0)return Results.NotFound();
 }
 return await PayAnyWayCheckout(db,owner,"promotion7",channelId,7,"Продвижение канала в каталоге Reklio на 7 дней",ct);
});
// Abandoned checkouts stay payable, but they are hidden from the history after a day.
app.MapGet("/api/payments/my",async(HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
 if(Authorized(req) is not long userId)return Results.Unauthorized();
 await using var cmd=db.CreateCommand("""
  SELECT transaction_id,product,channel_id,amount_rub,status,created_at,paid_at
  FROM reklio_payanyway_orders
  WHERE user_id=@user AND (status<>'pending' OR created_at>UTC_TIMESTAMP()-INTERVAL 1 DAY)
  ORDER BY created_at DESC LIMIT 20
  """);
 cmd.Parameters.Add(new MySqlParameter("@user",userId));
 var result=new List<object>();
 await using var reader=await cmd.ExecuteReaderAsync(ct);
 while(await reader.ReadAsync(ct))result.Add(new{orderUuid=reader.GetString(0),product=reader.GetString(1),
   channelId=reader.IsDBNull(2)?(long?)null:reader.GetInt64(2),amountKopecks=decimal.ToInt64(reader.GetDecimal(3)*100),
   status=reader.GetString(4),createdAt=reader.GetDateTime(5),appliedAt=reader.IsDBNull(6)?(DateTime?)null:reader.GetDateTime(6)});
 return Results.Ok(result);
});
// Replying FAIL makes the provider retry for 24 hours, so it is used only when a retry can help.
app.MapMethods("/api/payments/payanyway/notification",new[]{"GET","POST"},async(HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
 if(req.ContentLength>65536)return Results.StatusCode(413);
 if(PayAnyWayConfig() is not {} config)return Results.Text("FAIL",statusCode:503);
 var form=req.HasFormContentType?await req.ReadFormAsync(ct):null;
 string Param(string name)=>(form is null?req.Query[name]:form[name]).ToString();
 var transactionId=Param("MNT_TRANSACTION_ID");
 var operationId=Param("MNT_OPERATION_ID");
 var currency=Param("MNT_CURRENCY_CODE");
 var subscriber=Param("MNT_SUBSCRIBER_ID");
 var testFlag=Param("MNT_TEST_MODE")=="1"?"1":"0";
 if(Param("MNT_ID")!=config.Account||transactionId.Length is 0 or >64||operationId.Length>64||
    !decimal.TryParse(Param("MNT_AMOUNT"),System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out var paid)||
    !Reklio.Payments.PayAnyWay.IsValidNotification(config.Account,config.Code,transactionId,operationId,
      Reklio.Payments.PayAnyWay.Amount(paid),currency,subscriber,testFlag,Param("MNT_SIGNATURE")))
  return Results.Text("FAIL",statusCode:400);
 await using var conn=await db.OpenConnectionAsync(ct);
 await using var tx=await conn.BeginTransactionAsync(ct);
 long buyer=0;long? channel=null;string? product=null;int days=0;decimal amount=0m;bool orderTest=false;string? state=null;
 await using(var find=new MySqlCommand("""
  SELECT user_id,product,channel_id,days,amount_rub,test_mode,status
  FROM reklio_payanyway_orders WHERE transaction_id=@id FOR UPDATE
  """,conn,tx))
 {
  find.Parameters.AddWithValue("@id",transactionId);
  await using var reader=await find.ExecuteReaderAsync(ct);
  if(await reader.ReadAsync(ct))
  {
   buyer=reader.GetInt64(0);product=reader.GetString(1);channel=reader.IsDBNull(2)?null:reader.GetInt64(2);
   days=reader.GetInt32(3);amount=reader.GetDecimal(4);orderTest=reader.GetBoolean(5);state=reader.GetString(6);
  }
 }
 if(state is null)app.Logger.LogError("PayAnyWay paid unknown order {TransactionId}, operation {OperationId}",transactionId,operationId);
 if(state!="pending")
 {
  await tx.RollbackAsync(ct);
  return Results.Text("SUCCESS");
 }
 var matches=paid==amount&&currency=="RUB"&&subscriber==buyer.ToString()&&(testFlag=="1")==orderTest;
 if(matches&&product=="pro30")
 {
  // A buyer may pay before the bot has registered this account.
  await using var register=new MySqlCommand("INSERT IGNORE INTO users(telegram_id,display_name) VALUES(@user,@name)",conn,tx);
  register.Parameters.AddWithValue("@user",buyer);
  register.Parameters.AddWithValue("@name",buyer.ToString());
  await register.ExecuteNonQueryAsync(ct);
  await using var extend=new MySqlCommand("""
   UPDATE users SET subscription_until=DATE_ADD(
     GREATEST(COALESCE(subscription_until,UTC_TIMESTAMP()),UTC_TIMESTAMP()),INTERVAL @days DAY)
   WHERE telegram_id=@user
   """,conn,tx);
  extend.Parameters.AddWithValue("@user",buyer);
  extend.Parameters.AddWithValue("@days",days);
  await extend.ExecuteNonQueryAsync(ct);
 }
 else if(matches&&product=="promotion7"&&channel is long promotedChannel)
 {
  await using var promote=new MySqlCommand("""
   INSERT INTO channel_promotions(channel_id,active_until)
   VALUES(@channel,DATE_ADD(UTC_TIMESTAMP(),INTERVAL @days DAY))
   ON DUPLICATE KEY UPDATE active_until=DATE_ADD(GREATEST(active_until,UTC_TIMESTAMP()),INTERVAL @days DAY)
   """,conn,tx);
  promote.Parameters.AddWithValue("@channel",promotedChannel);
  promote.Parameters.AddWithValue("@days",days);
  await promote.ExecuteNonQueryAsync(ct);
 }
 else matches=false;
 await using var mark=new MySqlCommand("""
  UPDATE reklio_payanyway_orders SET status=@status,operation_id=@operation,paid_at=UTC_TIMESTAMP()
  WHERE transaction_id=@id
  """,conn,tx);
 mark.Parameters.AddWithValue("@status",matches?"paid":"review");
 mark.Parameters.AddWithValue("@operation",operationId);
 mark.Parameters.AddWithValue("@id",transactionId);
 await mark.ExecuteNonQueryAsync(ct);
 await tx.CommitAsync(ct);
 if(matches)app.Logger.LogInformation("Applied PayAnyWay order {TransactionId}",transactionId);
 else app.Logger.LogError("PayAnyWay order {TransactionId} needs manual review: paid {Paid} {Currency}, expected {Amount} RUB",transactionId,paid,currency,amount);
 return Results.Text("SUCCESS");
});

app.MapGet("/api/subscription", async (HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if(Authorized(req) is not long userId)return Results.Unauthorized();
    await using var cmd=db.CreateCommand("SELECT COALESCE(GREATEST(subscription_until,pro_granted_until),subscription_until,pro_granted_until) FROM users WHERE telegram_id=@id");
    cmd.Parameters.Add(new MySqlParameter("@id",userId));
    var value=await cmd.ExecuteScalarAsync(ct);
    var expiry=value is DateTime date ? date : (DateTime?)null;
    var isPro=expiry>DateTime.UtcNow;
    decimal price=399m;
    await using(var priceCmd=db.CreateCommand("SELECT price_rub FROM reklio_service_prices WHERE service_key='pro30'"))price=Convert.ToDecimal(await priceCmd.ExecuteScalarAsync(ct));
    return Results.Ok(new {plan=isPro?"PRO":"FREE",isPro,expiresAt=isPro?expiry:null,channelLimit=isPro?10:1,fullAnalytics=isPro,
        proPriceRub=price,checkoutAvailable=PayAnyWayConfig() is not null,checkoutMessage="Оплата временно недоступна"});
});
// Custom, unique channel links are stored separately from Telegram channel IDs.
await using (var aliasDb=app.Services.GetRequiredService<MySqlDataSource>().CreateCommand("""
CREATE TABLE IF NOT EXISTS reklio_channel_aliases (
 channel_id BIGINT NOT NULL PRIMARY KEY,
 alias VARCHAR(32) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
 updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
 UNIQUE KEY uq_reklio_channel_alias (alias)
) ENGINE=InnoDB
""")) await aliasDb.ExecuteNonQueryAsync();

app.MapGet("/api/public/channel-alias/{alias}",async (string alias,MySqlDataSource db,CancellationToken ct)=>{
 if(!System.Text.RegularExpressions.Regex.IsMatch(alias,@"^[a-zA-Z0-9_]{4,32}$"))return Results.NotFound();
 await using var cmd=db.CreateCommand("""
 SELECT c.telegram_id FROM reklio_channel_aliases a JOIN channels c ON c.telegram_id=a.channel_id
 WHERE a.alias=@alias AND c.is_listed=TRUE LIMIT 1
 """);
 cmd.Parameters.Add(new MySqlParameter("@alias",alias));
 var id=await cmd.ExecuteScalarAsync(ct);
 return id is null?Results.NotFound():Results.Ok(new {channelId=Convert.ToInt64(id)});
});
app.MapPost("/api/my-channels/{channelId:long}/alias",async (long channelId,ChannelAliasInput input,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>{
 if(Authorized(req) is not long owner)return Results.Unauthorized();
 var alias=(input.Alias??"").Trim().ToLowerInvariant();
 if(!System.Text.RegularExpressions.Regex.IsMatch(alias,@"^[a-z0-9_]{4,32}$"))
  return Results.BadRequest(new {error="Используйте 4–32 латинских символа, цифры или _"});
 if(alias is "admin" or "api" or "app" or "help" or "catalog" or "login" or "support" or "reklio")
  return Results.BadRequest(new {error="Этот адрес зарезервирован"});
 await using var conn=await db.OpenConnectionAsync(ct);
 await using var tx=await conn.BeginTransactionAsync(ct);
 await using(var verify=new MySqlCommand("SELECT telegram_id FROM channels WHERE telegram_id=@id AND connected_by=@owner FOR UPDATE",conn,tx)){
  verify.Parameters.AddWithValue("@id",channelId);verify.Parameters.AddWithValue("@owner",owner);
  if(await verify.ExecuteScalarAsync(ct) is null){await tx.RollbackAsync(ct);return Results.NotFound();}
 }
 try{
  await using(var remove=new MySqlCommand("DELETE FROM reklio_channel_aliases WHERE channel_id=@id",conn,tx)){
   remove.Parameters.AddWithValue("@id",channelId);
   await remove.ExecuteNonQueryAsync(ct);
  }
  await using var cmd=new MySqlCommand("""
  INSERT INTO reklio_channel_aliases(channel_id,alias) VALUES(@id,@alias)
  """,conn,tx);
  cmd.Parameters.AddWithValue("@id",channelId);cmd.Parameters.AddWithValue("@alias",alias);
  await cmd.ExecuteNonQueryAsync(ct);
  await tx.CommitAsync(ct);
  return Results.Ok(new {alias,url="https://reklio.site/ad/"+alias});
 }catch(MySqlException ex) when(ex.Number==1062){
  await tx.RollbackAsync(ct);
  return Results.Conflict(new {error="Этот адрес уже занят. Выберите другой."});
 }
});
app.MapGet("/api/my-channels", async (HttpRequest req, MySqlDataSource db, CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    await using var cmd = db.CreateCommand("""
        SELECT c.telegram_id,c.title,c.is_listed,c.ad_price_rub,c.category,a.alias,
          EXISTS(SELECT 1 FROM reklio_channel_blocks b WHERE b.channel_id=c.telegram_id AND (b.blocked_until IS NULL OR b.blocked_until>UTC_TIMESTAMP())),
          (SELECT b.blocked_until FROM reklio_channel_blocks b WHERE b.channel_id=c.telegram_id)
        FROM channels c
        LEFT JOIN reklio_channel_aliases a ON a.channel_id=c.telegram_id
        WHERE c.connected_by=@user ORDER BY c.connected_at DESC LIMIT 50
        """);
    cmd.Parameters.Add(new MySqlParameter("@user", userId));
    var channels = new List<object>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
        channels.Add(new { id = reader.GetInt64(0), title = reader.GetString(1),
            listed = reader.GetBoolean(2), price = reader.GetDecimal(3), category = reader.IsDBNull(4) ? "" : reader.GetString(4), alias=reader.IsDBNull(5)?null:reader.GetString(5),
            blocked=reader.GetInt64(6)!=0, blockedUntil=reader.IsDBNull(7)?(DateTime?)null:reader.GetDateTime(7) });
    return Results.Ok(channels);
});

app.MapPost("/api/my-channels/{channelId:long}/category", async (long channelId,UpdateCategoryInput input,HttpRequest req,MySqlDataSource db,CancellationToken ct) =>
{
    if (Authorized(req) is not long userId) return Results.Unauthorized();
    var category=input.Category?.Trim()??"";
    var allowed=new HashSet<string>(StringComparer.Ordinal){"Технологии","Игры","Новости","Бизнес","Финансы","Образование","Развлечения","Юмор","Спорт","Путешествия","Авто","Красота и стиль","Здоровье","Кулинария","Музыка","Кино","Другое"};
    if(!allowed.Contains(category))return Results.BadRequest(new {error="Выберите категорию из списка"});
    await using var cmd=db.CreateCommand("UPDATE channels SET category=@category WHERE telegram_id=@channel AND connected_by=@owner");
    cmd.Parameters.Add(new MySqlParameter("@category",category));
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",userId));
    return await cmd.ExecuteNonQueryAsync(ct)>0?Results.Ok(new {category}):Results.NotFound();
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
    if(input.Hours<0 || input.Hours>720 || input.Price<1 || input.Price>1000000)
        return Results.BadRequest(new {error="Укажите длительность 0 (навсегда) или 1–720 ч и цену 1–1000000 ₽"});
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
    if(input.Listed&&await ChannelIsBlocked(db,channelId,ct))return ChannelBlockedConflict();
    if(input.Listed&&await ChannelListingProblem(db,channelId,ct) is {} listingProblem)return Results.Conflict(new {error=listingProblem});
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
          AND status IN ('Pending','PendingPaid','AwaitingPayment','Scheduled','Publishing','Published')
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
          AND NOT EXISTS(SELECT 1 FROM reklio_channel_blocks b WHERE b.channel_id=c.telegram_id AND (b.blocked_until IS NULL OR b.blocked_until>UTC_TIMESTAMP()))
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
          AND @at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR)
          AND @at<DATE_ADD(UTC_TIMESTAMP(),INTERVAL 90 DAY)
          AND NOT EXISTS (
            SELECT 1 FROM (SELECT channel_id,scheduled_at,status FROM ad_requests) other
            WHERE other.channel_id=a.channel_id AND other.scheduled_at=@at
              AND other.status IN ('Pending','PendingPaid','AwaitingPayment','Scheduled','Publishing','Published'))
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
               (a.status IN ('Pending','AwaitingPayment') AND a.scheduled_at<=DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR)))
          AND @at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR)
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
    if (await OrderNeedsPaymentDetails(db, orderId, ownerId, ct)) return PaymentDetailsRequired();

    await using var conn = await db.OpenConnectionAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    await using var update = new MySqlCommand("""
        UPDATE ad_requests
        SET status=IF(status='PendingPaid','Scheduled','AwaitingPayment')
        WHERE id=@order AND owner_id=@owner AND status IN ('Pending','PendingPaid')
          AND ( (status='Pending' AND scheduled_at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 1 HOUR)) OR (status='PendingPaid' AND scheduled_at>UTC_TIMESTAMP()) )
        """, conn, tx);
    update.Parameters.AddWithValue("@order", orderId);
    update.Parameters.AddWithValue("@owner", ownerId);
    if (await update.ExecuteNonQueryAsync(ct) != 1)
    {
        await tx.RollbackAsync(ct);
        await using var reasonCmd=new MySqlCommand("SELECT status,scheduled_at FROM ad_requests WHERE id=@order AND owner_id=@owner LIMIT 1",conn);
        reasonCmd.Parameters.AddWithValue("@order",orderId);
        reasonCmd.Parameters.AddWithValue("@owner",ownerId);
        await using var reasonReader=await reasonCmd.ExecuteReaderAsync(ct);
        if(!await reasonReader.ReadAsync(ct)) return Results.NotFound(new {error="Заявка не найдена"});
        var actualStatus=reasonReader.GetString(0);
        var at=reasonReader.IsDBNull(1)?(DateTime?)null:reasonReader.GetDateTime(1);
        if(actualStatus is not ("Pending" or "PendingPaid")) return Results.Conflict(new {error="Эта заявка уже обработана. Обновите список заказов."});
        if(at is null) return Results.Conflict(new {error="В заявке не выбрано время размещения. Попросите покупателя выбрать дату и время."});
        if(at.Value<=DateTime.UtcNow) return Results.Conflict(new {error="Время размещения уже прошло. Попросите покупателя выбрать новое время."});
        if(actualStatus=="Pending" && at.Value<=DateTime.UtcNow.AddHours(1)) return Results.Conflict(new {error="До публикации осталось меньше 1 часа — времени на оплату недостаточно. Попросите покупателя перенести размещение."});
        return Results.Conflict(new {error="Не удалось одобрить заявку. Обновите список и попробуйте ещё раз."});
    }

    await using var notify = new MySqlCommand("""
        INSERT INTO notification_outbox (recipient_id,order_id,body)
        SELECT advertiser_id,id,CONCAT('✅ Заявка #',id,' одобрена',CHAR(10),IF(status='Scheduled','📅 Размещение запланировано.','💳 Следующий шаг — оплата.'),CHAR(10),'Откройте заказ в Reklio.')
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
        SELECT advertiser_id,id,CONCAT('❌ Заявка #',id,' отклонена',CHAR(10),'Владелец канала не подтвердил размещение.',CHAR(10),'🔎 Вы можете выбрать другую площадку.')
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
        SELECT a.price_rub
        FROM ad_requests a
        WHERE a.id=@id AND a.advertiser_id=@buyer AND status='AwaitingPayment'
          AND scheduled_at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
        FOR UPDATE
        """, conn, tx);
    lookup.Parameters.AddWithValue("@id", orderId);
    lookup.Parameters.AddWithValue("@buyer", buyer);
    decimal price;
    await using(var reader=await lookup.ExecuteReaderAsync(ct))
    {
        if(!await reader.ReadAsync(ct)){await tx.RollbackAsync(ct);return Results.Conflict(new {error="Заказ недоступен для оплаты"});}
        price=reader.GetDecimal(0);
    }
    var buyerTotal = price;
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
    // Commit the persisted idempotency key before contacting the provider.
    // Concurrent requests reuse the same key; YooKassa deduplicates them.
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

    await tx.CommitAsync(ct);

    // No database transaction or order row lock is held across the HTTP call.
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
    using var providerTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
    providerTimeout.CancelAfter(TimeSpan.FromSeconds(12));
    using var response = await client.SendAsync(request, providerTimeout.Token);
    if (!response.IsSuccessStatusCode)
        return Results.Problem("ЮKassa отклонила создание тестового платежа", statusCode: 502);
    using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    var root = document.RootElement;
    var providerId = root.GetProperty("id").GetString();
    var url = root.GetProperty("confirmation").GetProperty("confirmation_url").GetString();
    if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(url))
        return Results.Problem("Некорректный ответ ЮKassa", statusCode: 502);
    await using var saveTx = await conn.BeginTransactionAsync(ct);
    await using var save = new MySqlCommand("""
        UPDATE reklio_payment_attempts
        SET provider_payment_id=@provider,confirmation_url=@url,
            status=IF(status='Succeeded','Succeeded','Pending')
        WHERE id=@attempt AND mode='test' AND status IN ('Created','Pending','Succeeded')
          AND (provider_payment_id IS NULL OR provider_payment_id=@provider)
        """, conn, saveTx);
    save.Parameters.AddWithValue("@provider", providerId);
    save.Parameters.AddWithValue("@url", url);
    save.Parameters.AddWithValue("@attempt", attemptId);
    if (await save.ExecuteNonQueryAsync(ct) != 1)
        throw new InvalidOperationException("Payment attempt was not saved");
    await saveTx.CommitAsync(ct);
    return Results.Ok(new { confirmationUrl = url });
});

app.MapPost("/api/payments/yookassa/notification", async (HttpRequest req, MySqlDataSource db, IHttpClientFactory clients, CancellationToken ct) =>
{
    if(req.ContentLength>65536)return Results.StatusCode(413);
    var maxBody=req.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
    if(maxBody is {IsReadOnly:false})maxBody.MaxRequestBodySize=65536;
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
                SELECT advertiser_id,id,CONCAT('🧪 Тестовая оплата подтверждена · заказ #',id,CHAR(10),'✅ Размещение запланировано.',CHAR(10),'Реальные деньги не списаны.')
                  FROM ad_requests WHERE id=@order
                UNION ALL
                SELECT owner_id,id,CONCAT('🧪 Тестовый заказ #',id,CHAR(10),'✅ Оплата имитирована, деньги не списаны.',CHAR(10),'📅 Ожидается публикация.')
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
                SELECT advertiser_id,id,CONCAT('💳 Оплата подтверждена · заказ #',id,CHAR(10),'✅ Реклама запланирована.',CHAR(10),'📅 Ожидайте публикации в назначенное время.')
                FROM ad_requests WHERE id=@id
                UNION ALL
                SELECT owner_id,id,CONCAT('💳 Заказ #',id,' оплачен',CHAR(10),'✅ Оплата подтверждена.',CHAR(10),'📅 Публикация ожидается по расписанию.')
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


// Owner earnings are bookkeeping entries, not stored-value accounts or customer deposits.
// Only externally verified real payments and completed, payout-reviewed orders qualify.
static async Task ReconcileOwnerEarnings(MySqlConnection conn,long ownerId,CancellationToken ct)
{
    await using var credit=new MySqlCommand("""
        INSERT IGNORE INTO reklio_owner_earnings(order_id,owner_id,amount_rub)
        SELECT a.id,a.owner_id,a.price_rub
        FROM ad_requests a
        WHERE a.owner_id=@owner AND a.payment_status='Paid' AND a.is_test_order=FALSE
          AND a.status='Deleted' AND a.payout_status='ReadyForManualReview'
          AND a.price_rub>0
        """,conn);
    credit.Parameters.AddWithValue("@owner",ownerId);
    await credit.ExecuteNonQueryAsync(ct);
}

static async Task<decimal> OwnerAvailable(MySqlConnection conn,long ownerId,CancellationToken ct)
{
    await using var sum=new MySqlCommand("""
        SELECT
          (SELECT COALESCE(SUM(e.amount_rub),0) FROM reklio_owner_earnings e
             JOIN ad_requests a ON a.id=e.order_id
           WHERE e.owner_id=@owner AND a.owner_id=@owner
             AND a.payment_status='Paid' AND a.is_test_order=FALSE
             AND a.status='Deleted' AND a.payout_status IN ('ReadyForManualReview','Paid'))
          -
          (SELECT COALESCE(SUM(r.amount_rub),0) FROM reklio_payout_requests r
           WHERE r.owner_id=@owner AND r.status IN ('Pending','Paid'))
        """,conn);
    sum.Parameters.AddWithValue("@owner",ownerId);
    return Convert.ToDecimal(await sum.ExecuteScalarAsync(ct));
}



app.MapGet("/api/web/admin/earnings-review",async(HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var uid)||uid<=0)
        return Results.Unauthorized();
    if(!IsRootAdmin(uid))return Results.Forbid();
    await using var cmd=db.CreateCommand("""
        SELECT a.id,a.owner_id,a.price_rub,a.published_at,p.deleted_at
        FROM ad_requests a
        JOIN reklio_publications p ON p.order_id=a.id
        WHERE a.payment_status='Paid' AND a.is_test_order=FALSE
          AND a.status='Deleted' AND a.payout_status='AwaitingVerification'
          AND p.state='Deleted' AND p.deleted_at IS NOT NULL
        ORDER BY p.deleted_at ASC LIMIT 100
        """);
    var items=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))items.Add(new{id=reader.GetInt64(0),ownerId=reader.GetInt64(1),
        amount=reader.GetDecimal(2),publishedAt=reader.IsDBNull(3)?(DateTime?)null:reader.GetDateTime(3),
        deletedAt=reader.GetDateTime(4)});
    return Results.Ok(items);
});
app.MapPost("/api/web/admin/earnings-review/{orderId:long}/approve",async(long orderId,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var uid)||uid<=0)
        return Results.Unauthorized();
    if(!IsRootAdmin(uid))return Results.Forbid();
    if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))
        return Results.StatusCode(403);
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("""
        UPDATE ad_requests a JOIN reklio_publications p ON p.order_id=a.id
        SET a.payout_status='ReadyForManualReview',a.payout_eligible_at=UTC_TIMESTAMP()
        WHERE a.id=@id AND a.status='Deleted' AND a.payment_status='Paid'
          AND a.is_test_order=FALSE AND a.payout_status='AwaitingVerification'
          AND a.disputed_at IS NULL AND a.publication_issue IS NULL
          AND p.state='Deleted'
          AND p.deleted_at<=DATE_SUB(UTC_TIMESTAMP(),INTERVAL 24 HOUR)
        """,conn,tx);
    cmd.Parameters.AddWithValue("@id",orderId);
    if(await cmd.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Заказ не соответствует условиям начисления либо не прошло 24 часа"});}
    await using var audit=new MySqlCommand("""
        INSERT INTO reklio_admin_audit(actor_id,action,target_id,details)
        VALUES(@actor,'OwnerEarningApproved',@order,'Verified payment and completed placement')
        """,conn,tx);
    audit.Parameters.AddWithValue("@actor",uid);
    audit.Parameters.AddWithValue("@order",orderId);
    await audit.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new{orderId,approved=true});
});

app.MapGet("/api/web/admin/payouts",async(HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var uid)||uid<=0)
        return Results.Unauthorized();
    if(!IsRootAdmin(uid))return Results.Forbid();
    await using var cmd=db.CreateCommand("""
        SELECT id,owner_id,amount_rub,status,created_at,processed_at,transfer_reference
        FROM reklio_payout_requests ORDER BY id DESC LIMIT 100
        """);
    var rows=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))rows.Add(new{id=reader.GetInt64(0),ownerId=reader.GetInt64(1),
        amount=reader.GetDecimal(2),status=reader.GetString(3),createdAt=reader.GetDateTime(4),
        processedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5),
        reference=reader.IsDBNull(6)?null:reader.GetString(6)});
    return Results.Ok(rows);
});
app.MapPost("/api/web/admin/payouts/{id:long}/decision",async(long id,PayoutDecisionInput input,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var uid)||uid<=0)
        return Results.Unauthorized();
    if(!IsRootAdmin(uid))return Results.Forbid();
    if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))
        return Results.StatusCode(403);
    if(input.Decision is not ("Paid" or "Rejected"))return Results.BadRequest(new{error="Неверное действие"});
    var reference=(input.Reference??"").Trim();
    if(input.Decision=="Paid"&&(reference.Length<4||reference.Length>120))
        return Results.BadRequest(new{error="Требуется идентификатор подтверждённого банковского перевода"});
    if(reference.Length>120)return Results.BadRequest();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("""
        UPDATE reklio_payout_requests
        SET status=@status,processed_at=UTC_TIMESTAMP(),processed_by=@admin,transfer_reference=@reference
        WHERE id=@id AND status='Pending'
        """,conn,tx);
    cmd.Parameters.AddWithValue("@id",id);
    cmd.Parameters.AddWithValue("@status",input.Decision);
    cmd.Parameters.AddWithValue("@admin",uid);
    cmd.Parameters.AddWithValue("@reference",reference);
    if(await cmd.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Заявка уже обработана"});}
    await using var audit=new MySqlCommand("""
        INSERT INTO reklio_admin_audit(actor_id,action,target_id,details)
        VALUES(@admin,'OwnerPayout',@id,@details)
        """,conn,tx);
    audit.Parameters.AddWithValue("@admin",uid);
    audit.Parameters.AddWithValue("@id",id);
    audit.Parameters.AddWithValue("@details",input.Decision+" "+reference);
    await audit.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new{id,status=input.Decision});
});


// Seller receives ad payments independently. Reklio never owns the funds or creates payout balances.
// Card numbers are stored only to show them to the buyer of an approved order. Never accept a CVV or expiry date here.
static string? NormalizeCardNumber(string value)
{
    var digits=value.Replace(" ","").Replace("-","");
    if(digits.Length is <16 or >19||!digits.All(char.IsAsciiDigit))return null;
    var sum=0;
    for(var i=0;i<digits.Length;i++)
    {
        var digit=digits[digits.Length-1-i]-'0';
        if(i%2==1){digit*=2;if(digit>9)digit-=9;}
        sum+=digit;
    }
    return sum%10==0?digits:null;
}

static string? NormalizePhone(string value)
{
    var digits=new string(value.Where(char.IsAsciiDigit).ToArray());
    if(digits.Length==10&&digits[0]=='9')digits="7"+digits;
    if(digits.Length!=11||digits[0] is not ('7' or '8'))return null;
    return "+7"+digits[1..];
}

static async Task<IResult> ChannelPaymentDetails(MySqlDataSource db,long channelId,long owner,CancellationToken ct)
{
    await using var cmd=db.CreateCommand("""
      SELECT d.card_number,d.phone,d.bank FROM channels c
      LEFT JOIN reklio_channel_payment_details d ON d.channel_id=c.telegram_id AND d.owner_id=c.connected_by
      WHERE c.telegram_id=@channel AND c.connected_by=@owner
      """);
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",owner));
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    if(!await reader.ReadAsync(ct))return Results.NotFound();
    return Results.Ok(new{cardNumber=reader.IsDBNull(0)?null:reader.GetString(0),phone=reader.IsDBNull(1)?null:reader.GetString(1),
        bank=reader.IsDBNull(2)?null:reader.GetString(2)});
}

// Saving all fields empty removes the details, so an owner can always withdraw a stored card number.
static async Task<IResult> SaveChannelPaymentDetails(MySqlDataSource db,long channelId,long owner,PaymentDetailsInput input,CancellationToken ct)
{
    var cardText=(input.CardNumber??"").Trim();
    var phoneText=(input.Phone??"").Trim();
    var bank=(input.Bank??"").Trim();
    if(cardText.Length==0&&phoneText.Length==0&&bank.Length==0)
    {
        await using var remove=db.CreateCommand("DELETE FROM reklio_channel_payment_details WHERE channel_id=@channel AND owner_id=@owner");
        remove.Parameters.Add(new MySqlParameter("@channel",channelId));
        remove.Parameters.Add(new MySqlParameter("@owner",owner));
        await remove.ExecuteNonQueryAsync(ct);
        return Results.Ok(new{cardNumber=(string?)null,phone=(string?)null,bank=(string?)null});
    }
    string? card=null,phone=null;
    if(cardText.Length>0&&(card=NormalizeCardNumber(cardText)) is null)
        return Results.BadRequest(new{error="Проверьте номер карты"});
    if(phoneText.Length>0&&(phone=NormalizePhone(phoneText)) is null)
        return Results.BadRequest(new{error="Укажите российский номер телефона, например +79001234567"});
    if(card is null&&phone is null)
        return Results.BadRequest(new{error="Укажите номер карты или телефона"});
    if(bank.Length is <2 or >64||bank.Any(char.IsControl))
        return Results.BadRequest(new{error="Укажите название банка"});
    await using var cmd=db.CreateCommand("""
      INSERT INTO reklio_channel_payment_details(channel_id,owner_id,card_number,phone,bank)
      SELECT telegram_id,connected_by,@card,@phone,@bank FROM channels WHERE telegram_id=@channel AND connected_by=@owner
      ON DUPLICATE KEY UPDATE owner_id=VALUES(owner_id),card_number=VALUES(card_number),phone=VALUES(phone),
        bank=VALUES(bank),updated_at=UTC_TIMESTAMP()
      """);
    cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
    cmd.Parameters.Add(new MySqlParameter("@owner",owner));
    cmd.Parameters.Add(new MySqlParameter("@card",(object?)card??DBNull.Value));
    cmd.Parameters.Add(new MySqlParameter("@phone",(object?)phone??DBNull.Value));
    cmd.Parameters.Add(new MySqlParameter("@bank",bank));
    return await cmd.ExecuteNonQueryAsync(ct)>0?Results.Ok(new{cardNumber=card,phone,bank}):Results.NotFound();
}

// A buyer sees the details only while the order awaits payment and the channel is not blocked.
static async Task<IResult> OrderDirectPayment(MySqlDataSource db,long orderId,long uid,CancellationToken ct)
{
    await using var cmd=db.CreateCommand("""
      SELECT a.price_rub,a.status,a.payment_status,a.owner_id,d.card_number,d.phone,d.bank,p.status,
        EXISTS(SELECT 1 FROM reklio_channel_blocks b WHERE b.channel_id=a.channel_id AND (b.blocked_until IS NULL OR b.blocked_until>UTC_TIMESTAMP())),
        (a.advertiser_id=@uid AND a.status='AwaitingPayment' AND
          (p.status='Rejected' OR (p.status='BuyerClaimed' AND p.claimed_at<UTC_TIMESTAMP()-INTERVAL 24 HOUR)))
      FROM ad_requests a
      LEFT JOIN reklio_channel_payment_details d ON d.channel_id=a.channel_id AND d.owner_id=a.owner_id
      LEFT JOIN reklio_direct_payments p ON p.order_id=a.id
      WHERE a.id=@id AND (a.advertiser_id=@uid OR a.owner_id=@uid)
      """);
    cmd.Parameters.Add(new MySqlParameter("@id",orderId));
    cmd.Parameters.Add(new MySqlParameter("@uid",uid));
    await using var rd=await cmd.ExecuteReaderAsync(ct);
    if(!await rd.ReadAsync(ct))return Results.NotFound();
    var orderStatus=rd.GetString(1);
    var isOwner=rd.GetInt64(3)==uid;
    var blocked=rd.GetInt64(8)!=0;
    var showDetails=isOwner||(orderStatus=="AwaitingPayment"&&!blocked);
    return Results.Ok(new{amount=rd.GetDecimal(0),orderStatus,paymentStatus=rd.GetString(2),
        cardNumber=!showDetails||rd.IsDBNull(4)?null:rd.GetString(4),
        phone=!showDetails||rd.IsDBNull(5)?null:rd.GetString(5),
        bank=!showDetails||rd.IsDBNull(6)?null:rd.GetString(6),
        claimStatus=rd.IsDBNull(7)?null:rd.GetString(7),channelBlocked=blocked,
        canDispute=!rd.IsDBNull(9)&&rd.GetInt64(9)!=0,
        notice="Деньги перечисляются напрямую продавцу. Reklio не получает их и не проверяет перевод."});
}

app.MapGet("/api/web/channels/{channelId:long}/direct-payment",async(long channelId,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var uid)||uid<=0)return Results.Unauthorized();
    return await ChannelPaymentDetails(db,channelId,uid,ct);
});
app.MapPost("/api/web/channels/{channelId:long}/direct-payment",async(long channelId,PaymentDetailsInput input,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var uid)||uid<=0)return Results.Unauthorized();
    if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))return Results.StatusCode(403);
    return await SaveChannelPaymentDetails(db,channelId,uid,input,ct);
});
app.MapGet("/api/web/orders/{orderId:long}/direct-payment",async(long orderId,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true||!long.TryParse(ctx.User.FindFirst("id")?.Value,out var uid)||uid<=0)return Results.Unauthorized();
    return await OrderDirectPayment(db,orderId,uid,ct);
});

app.MapGet("/api/my-channels/{channelId:long}/direct-payment",async(long channelId,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    return await ChannelPaymentDetails(db,channelId,uid,ct);
});
app.MapPost("/api/my-channels/{channelId:long}/direct-payment",async(long channelId,PaymentDetailsInput input,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    return await SaveChannelPaymentDetails(db,channelId,uid,input,ct);
});
app.MapGet("/api/orders/{orderId:long}/direct-payment",async(long orderId,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    return await OrderDirectPayment(db,orderId,uid,ct);
});
// The owner cannot sell a slot without a way to get paid, or the buyer would be stuck after approval.
static async Task<bool> OrderNeedsPaymentDetails(MySqlDataSource db,long orderId,long owner,CancellationToken ct)
{
    await using var cmd=db.CreateCommand("""
        SELECT COUNT(*) FROM ad_requests a
        WHERE a.id=@order AND a.owner_id=@owner AND a.status='Pending'
          AND NOT EXISTS(SELECT 1 FROM reklio_channel_payment_details d WHERE d.channel_id=a.channel_id AND d.owner_id=a.owner_id)
        """);
    cmd.Parameters.Add(new MySqlParameter("@order",orderId));
    cmd.Parameters.Add(new MySqlParameter("@owner",owner));
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct))>0;
}
static IResult PaymentDetailsRequired()=>Results.Conflict(new {error="Сначала укажите реквизиты для оплаты в настройках канала, иначе покупатель не сможет заплатить."});

// A channel in the catalog must be able to take an order all the way through payment.
static async Task<string?> ChannelListingProblem(MySqlDataSource db,long channelId,CancellationToken ct)
{
    await using var cmd=db.CreateCommand("""
        SELECT
          EXISTS(SELECT 1 FROM channel_duration_rates r WHERE r.channel_id=@id AND r.is_active=TRUE),
          EXISTS(SELECT 1 FROM reklio_channel_payment_details d WHERE d.channel_id=@id)
        """);
    cmd.Parameters.Add(new MySqlParameter("@id",channelId));
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    if(reader.GetInt64(0)==0)return "Сначала добавьте рекламный тариф.";
    if(reader.GetInt64(1)==0)return "Сначала укажите реквизиты для оплаты.";
    return null;
}

// Cancelling stops an order before money moves. After a confirmed payment only the owner can cancel,
// because only the owner can return the transfer; the buyer keeps the right to a dispute.
app.MapPost("/api/orders/{orderId:long}/cancel",async(long orderId,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cancel=new MySqlCommand("""
        UPDATE ad_requests a SET a.status='Cancelled'
        WHERE a.id=@id AND (
          (a.advertiser_id=@user OR a.owner_id=@user) AND (a.status='Pending' OR (a.status='AwaitingPayment'
             AND NOT EXISTS(SELECT 1 FROM reklio_direct_payments p WHERE p.order_id=a.id AND p.status='BuyerClaimed')))
          OR (a.owner_id=@user AND a.status IN ('Scheduled','PendingPaid','PaidReschedule') AND a.payment_status='DirectPaid'))
        """,conn,tx);
    cancel.Parameters.AddWithValue("@id",orderId);
    cancel.Parameters.AddWithValue("@user",uid);
    if(await cancel.ExecuteNonQueryAsync(ct)!=1)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new {error="Этот заказ уже нельзя отменить. Обновите список заказов."});
    }
    await using var notice=new MySqlCommand("""
        INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
        SELECT IF(owner_id=@user,advertiser_id,owner_id),id,CONCAT('❌ Заказ #',id,' отменён',CHAR(10),
          IF(payment_status='DirectPaid','Владелец отменил оплаченное размещение и должен вернуть деньги. Если деньги не вернулись, откройте спор в Reklio.',
             IF(owner_id=@user,'Владелец канала отменил заявку.','Покупатель отменил заявку.')))
        FROM ad_requests WHERE id=@id
        """,conn,tx);
    notice.Parameters.AddWithValue("@id",orderId);
    notice.Parameters.AddWithValue("@user",uid);
    await notice.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {status="Cancelled"});
});

// Returns a paid post that vanished or was refused by Telegram to the publishing queue.
app.MapPost("/api/orders/{orderId:long}/republish",async(long orderId,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var update=new MySqlCommand("""
        UPDATE ad_requests SET status='Scheduled',scheduled_at=UTC_TIMESTAMP(),last_error=NULL,
          published_message_id=NULL,published_at=NULL
        WHERE id=@id AND owner_id=@owner AND status IN ('EarlyDeletionPending','PublishFailed')
          AND payment_status IN ('Paid','TestPaid','DirectPaid')
        """,conn,tx);
    update.Parameters.AddWithValue("@id",orderId);
    update.Parameters.AddWithValue("@owner",uid);
    if(await update.ExecuteNonQueryAsync(ct)!=1)
    {
        await tx.RollbackAsync(ct);
        return Results.Conflict(new {error="Повторная публикация недоступна. Обновите список заказов."});
    }
    // The publication worker claims an order only once, so the old attempt has to go.
    await using var reset=new MySqlCommand("DELETE FROM reklio_publications WHERE order_id=@id",conn,tx);
    reset.Parameters.AddWithValue("@id",orderId);
    await reset.ExecuteNonQueryAsync(ct);
    try
    {
        await using var monitor=new MySqlCommand("DELETE FROM reklio_post_monitor WHERE order_id=@id",conn,tx);
        monitor.Parameters.AddWithValue("@id",orderId);
        await monitor.ExecuteNonQueryAsync(ct);
    }
    catch(MySqlException ex) when(ex.Number==1146){/* The optional post monitor is not installed. */}
    await using var audit=new MySqlCommand("""
        INSERT INTO ad_order_events(order_id,actor_id,event_type,details)
        VALUES(@id,@owner,'EarlyDeletionRepublishRequested','Владелец выбрал повторную публикацию')
        """,conn,tx);
    audit.Parameters.AddWithValue("@id",orderId);
    audit.Parameters.AddWithValue("@owner",uid);
    await audit.ExecuteNonQueryAsync(ct);
    await using var notice=new MySqlCommand("""
        INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
        SELECT advertiser_id,id,CONCAT('🔄 Заказ #',id,CHAR(10),'Владелец публикует рекламу заново. Срок размещения начнётся с новой публикации.')
        FROM ad_requests WHERE id=@id
        """,conn,tx);
    notice.Parameters.AddWithValue("@id",orderId);
    await notice.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {status="Scheduled"});
});

app.MapPost("/api/orders/{orderId:long}/direct-payment/claim",async(long orderId,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var check=new MySqlCommand("""
      SELECT a.owner_id FROM ad_requests a
      JOIN reklio_channel_payment_details l ON l.channel_id=a.channel_id AND l.owner_id=a.owner_id
      WHERE a.id=@id AND a.advertiser_id=@buyer AND a.status='AwaitingPayment'
        AND a.payment_status='Unpaid' AND a.scheduled_at>DATE_ADD(UTC_TIMESTAMP(),INTERVAL 10 MINUTE)
        AND NOT EXISTS(SELECT 1 FROM reklio_channel_blocks b WHERE b.channel_id=a.channel_id AND (b.blocked_until IS NULL OR b.blocked_until>UTC_TIMESTAMP()))
      FOR UPDATE
      """,conn,tx);
    check.Parameters.AddWithValue("@id",orderId);
    check.Parameters.AddWithValue("@buyer",uid);
    var owner=await check.ExecuteScalarAsync(ct);
    if(owner is null){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Заказ недоступен или время размещения слишком близко"});}
    await using var cmd=new MySqlCommand("""
      INSERT INTO reklio_direct_payments(order_id,owner_id,buyer_id,status)
      VALUES(@id,@owner,@buyer,'BuyerClaimed')
      ON DUPLICATE KEY UPDATE status=IF(status='OwnerConfirmed',status,'BuyerClaimed'),claimed_at=UTC_TIMESTAMP()
      """,conn,tx);
    cmd.Parameters.AddWithValue("@id",orderId);cmd.Parameters.AddWithValue("@owner",Convert.ToInt64(owner));cmd.Parameters.AddWithValue("@buyer",uid);
    await cmd.ExecuteNonQueryAsync(ct);
    await using var notice=new MySqlCommand("""
      INSERT INTO notification_outbox(recipient_id,order_id,body)
      SELECT owner_id,id,CONCAT('💳 Покупатель сообщил об оплате напрямую · заказ #',id,CHAR(10),
         'Проверьте поступление в своём банке. Подтверждение в Reklio запускает публикацию.')
      FROM ad_requests WHERE id=@id
      """,conn,tx);
    notice.Parameters.AddWithValue("@id",orderId);await notice.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);return Results.Ok(new{status="BuyerClaimed"});
});
app.MapPost("/api/orders/{orderId:long}/direct-payment/{decision}",async(long orderId,string decision,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    if(decision is not ("confirm" or "reject"))return Results.BadRequest();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var check=new MySqlCommand("""
       SELECT a.status,a.scheduled_at,d.status FROM ad_requests a
       JOIN reklio_direct_payments d ON d.order_id=a.id
       WHERE a.id=@id AND a.owner_id=@owner AND d.owner_id=@owner AND d.status='BuyerClaimed'
       FOR UPDATE
       """,conn,tx);
    check.Parameters.AddWithValue("@id",orderId);check.Parameters.AddWithValue("@owner",uid);
    string? status=null;DateTime? schedule=null;
    await using(var rd=await check.ExecuteReaderAsync(ct))
       if(await rd.ReadAsync(ct)){status=rd.GetString(0);schedule=rd.IsDBNull(1)?null:rd.GetDateTime(1);}
    if(status!="AwaitingPayment"){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Заявка недоступна для подтверждения"});}
    var late=decision=="confirm"&&(schedule is null||schedule.Value<=DateTime.UtcNow.AddMinutes(10));
    await using var claim=new MySqlCommand("""
        UPDATE reklio_direct_payments SET status=@status,
          confirmed_at=IF(@status='OwnerConfirmed',UTC_TIMESTAMP(),NULL)
        WHERE order_id=@id AND status='BuyerClaimed'
        """,conn,tx);
    claim.Parameters.AddWithValue("@id",orderId);
    claim.Parameters.AddWithValue("@status",decision=="confirm"?"OwnerConfirmed":"Rejected");
    if(await claim.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Results.Conflict();}
    if(decision=="confirm")
    {
        await using var update=new MySqlCommand("""
          UPDATE ad_requests SET status=IF(@late,'PaidReschedule','Scheduled'),paid_confirmed=TRUE,
             payment_status='DirectPaid',is_test_order=FALSE,payout_status='NotEligible'
          WHERE id=@id AND owner_id=@owner AND status='AwaitingPayment' AND payment_status='Unpaid'
          """,conn,tx);
        update.Parameters.AddWithValue("@id",orderId);update.Parameters.AddWithValue("@owner",uid);update.Parameters.AddWithValue("@late",late);
        if(await update.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Results.Conflict();}
    }
    await using var notice=new MySqlCommand("""
      INSERT INTO notification_outbox(recipient_id,order_id,body)
      SELECT advertiser_id,id,CONCAT(IF(@approved=1,'✅ Продавец подтвердил получение оплаты','❌ Продавец не подтвердил оплату'),
           ' · заказ #',id,CHAR(10),IF(@approved=1,IF(@late,'Время публикации уже прошло. Выберите новое время в разделе «Заказы».','Размещение запланировано.'),'Если вы перевели деньги, откройте спор в разделе «Заказы».'))
      FROM ad_requests WHERE id=@id
      """,conn,tx);
    notice.Parameters.AddWithValue("@id",orderId);notice.Parameters.AddWithValue("@approved",decision=="confirm");notice.Parameters.AddWithValue("@late",late);
    await notice.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new{status=decision!="confirm"?"AwaitingPayment":late?"PaidReschedule":"Scheduled"});
});

app.MapGet("/api/web/payouts",async(HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true || !long.TryParse(ctx.User.FindFirst("id")?.Value,out var ownerId)||ownerId<=0)
        return Results.Unauthorized();
    await using var conn=await db.OpenConnectionAsync(ct);
    await ReconcileOwnerEarnings(conn,ownerId,ct);
    var available=await OwnerAvailable(conn,ownerId,ct);
    await using var cmd=new MySqlCommand("""
        SELECT id,amount_rub,status,created_at,processed_at
        FROM reklio_payout_requests WHERE owner_id=@owner
        ORDER BY id DESC LIMIT 100
        """,conn);
    cmd.Parameters.AddWithValue("@owner",ownerId);
    var history=new List<object>();
    await using(var reader=await cmd.ExecuteReaderAsync(ct))
        while(await reader.ReadAsync(ct))
            history.Add(new {id=reader.GetInt64(0),amount=reader.GetDecimal(1),status=reader.GetString(2),
                createdAt=reader.GetDateTime(3),processedAt=reader.IsDBNull(4)?(DateTime?)null:reader.GetDateTime(4)});
    return Results.Ok(new {available=Math.Max(0m,available),requests=history,
        note="Учёт начислений за выполненные заказы. Это не электронный кошелёк; заявки обрабатываются вручную."});
});
app.MapPost("/api/web/payouts",async(PayoutRequestInput input,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(ctx.User.Identity?.IsAuthenticated!=true || !long.TryParse(ctx.User.FindFirst("id")?.Value,out var ownerId)||ownerId<=0)
        return Results.Unauthorized();
    if(ctx.Request.Headers.Origin.ToString() is not ("https://reklio.site" or "https://www.reklio.site"))
        return Results.StatusCode(403);
    if(input.Amount<100m || input.Amount>1000000m || decimal.Round(input.Amount,2)!=input.Amount)
        return Results.BadRequest(new {error="Сумма выплаты: от 100 до 1 000 000 ₽, не более двух знаков после запятой"});
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    // Serialize requests for the same owner across all API instances.
    await using(var owner=new MySqlCommand("SELECT telegram_id FROM users WHERE telegram_id=@id FOR UPDATE",conn,tx))
    {
        owner.Parameters.AddWithValue("@id",ownerId);
        if(await owner.ExecuteScalarAsync(ct) is null){await tx.RollbackAsync(ct);return Results.NotFound();}
    }
    await using(var credit=new MySqlCommand("""
        INSERT IGNORE INTO reklio_owner_earnings(order_id,owner_id,amount_rub)
        SELECT a.id,a.owner_id,a.price_rub FROM ad_requests a
        WHERE a.owner_id=@owner AND a.payment_status='Paid' AND a.is_test_order=FALSE
          AND a.status='Deleted' AND a.payout_status='ReadyForManualReview' AND a.price_rub>0
        """,conn,tx))
    {
        credit.Parameters.AddWithValue("@owner",ownerId);
        await credit.ExecuteNonQueryAsync(ct);
    }
    decimal available;
    await using(var balance=new MySqlCommand("""
        SELECT
           (SELECT COALESCE(SUM(e.amount_rub),0) FROM reklio_owner_earnings e
              JOIN ad_requests a ON a.id=e.order_id WHERE e.owner_id=@owner
              AND a.owner_id=@owner AND a.payment_status='Paid' AND a.is_test_order=FALSE
              AND a.status='Deleted' AND a.payout_status IN ('ReadyForManualReview','Paid'))
           -
           (SELECT COALESCE(SUM(amount_rub),0) FROM reklio_payout_requests
              WHERE owner_id=@owner AND status IN ('Pending','Paid'))
        """,conn,tx))
    {
        balance.Parameters.AddWithValue("@owner",ownerId);
        available=Convert.ToDecimal(await balance.ExecuteScalarAsync(ct));
    }
    if(available<input.Amount){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Недостаточно доступных начислений"});}
    await using var insert=new MySqlCommand("""
        INSERT INTO reklio_payout_requests(owner_id,amount_rub,status)
        VALUES(@owner,@amount,'Pending')
        """,conn,tx);
    insert.Parameters.AddWithValue("@owner",ownerId);
    insert.Parameters.AddWithValue("@amount",input.Amount);
    await insert.ExecuteNonQueryAsync(ct);
    var id=insert.LastInsertedId;
    await tx.CommitAsync(ct);
    return Results.Ok(new{id,status="Pending",amount=input.Amount});
});
app.MapGet("/api/payouts",async(HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(ctx.Request) is not long ownerId)
        return Results.Unauthorized();
    await using var conn=await db.OpenConnectionAsync(ct);
    await ReconcileOwnerEarnings(conn,ownerId,ct);
    var available=await OwnerAvailable(conn,ownerId,ct);
    await using var cmd=new MySqlCommand("""
        SELECT id,amount_rub,status,created_at,processed_at
        FROM reklio_payout_requests WHERE owner_id=@owner
        ORDER BY id DESC LIMIT 100
        """,conn);
    cmd.Parameters.AddWithValue("@owner",ownerId);
    var history=new List<object>();
    await using(var reader=await cmd.ExecuteReaderAsync(ct))
        while(await reader.ReadAsync(ct))
            history.Add(new {id=reader.GetInt64(0),amount=reader.GetDecimal(1),status=reader.GetString(2),
                createdAt=reader.GetDateTime(3),processedAt=reader.IsDBNull(4)?(DateTime?)null:reader.GetDateTime(4)});
    return Results.Ok(new {available=Math.Max(0m,available),requests=history,
        note="Учёт начислений за выполненные заказы. Это не электронный кошелёк; заявки обрабатываются вручную."});
});
app.MapPost("/api/payouts",async(PayoutRequestInput input,HttpContext ctx,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(ctx.Request) is not long ownerId)
        return Results.Unauthorized();
    if(input.Amount<100m || input.Amount>1000000m || decimal.Round(input.Amount,2)!=input.Amount)
        return Results.BadRequest(new {error="Сумма выплаты: от 100 до 1 000 000 ₽, не более двух знаков после запятой"});
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    // Serialize requests for the same owner across all API instances.
    await using(var owner=new MySqlCommand("SELECT telegram_id FROM users WHERE telegram_id=@id FOR UPDATE",conn,tx))
    {
        owner.Parameters.AddWithValue("@id",ownerId);
        if(await owner.ExecuteScalarAsync(ct) is null){await tx.RollbackAsync(ct);return Results.NotFound();}
    }
    await using(var credit=new MySqlCommand("""
        INSERT IGNORE INTO reklio_owner_earnings(order_id,owner_id,amount_rub)
        SELECT a.id,a.owner_id,a.price_rub FROM ad_requests a
        WHERE a.owner_id=@owner AND a.payment_status='Paid' AND a.is_test_order=FALSE
          AND a.status='Deleted' AND a.payout_status='ReadyForManualReview' AND a.price_rub>0
        """,conn,tx))
    {
        credit.Parameters.AddWithValue("@owner",ownerId);
        await credit.ExecuteNonQueryAsync(ct);
    }
    decimal available;
    await using(var balance=new MySqlCommand("""
        SELECT
           (SELECT COALESCE(SUM(e.amount_rub),0) FROM reklio_owner_earnings e
              JOIN ad_requests a ON a.id=e.order_id WHERE e.owner_id=@owner
              AND a.owner_id=@owner AND a.payment_status='Paid' AND a.is_test_order=FALSE
              AND a.status='Deleted' AND a.payout_status IN ('ReadyForManualReview','Paid'))
           -
           (SELECT COALESCE(SUM(amount_rub),0) FROM reklio_payout_requests
              WHERE owner_id=@owner AND status IN ('Pending','Paid'))
        """,conn,tx))
    {
        balance.Parameters.AddWithValue("@owner",ownerId);
        available=Convert.ToDecimal(await balance.ExecuteScalarAsync(ct));
    }
    if(available<input.Amount){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Недостаточно доступных начислений"});}
    await using var insert=new MySqlCommand("""
        INSERT INTO reklio_payout_requests(owner_id,amount_rub,status)
        VALUES(@owner,@amount,'Pending')
        """,conn,tx);
    insert.Parameters.AddWithValue("@owner",ownerId);
    insert.Parameters.AddWithValue("@amount",input.Amount);
    await insert.ExecuteNonQueryAsync(ct);
    var id=insert.LastInsertedId;
    await tx.CommitAsync(ct);
    return Results.Ok(new{id,status="Pending",amount=input.Amount});
});

app.MapGet("/api/admin/payouts",async(HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    if(!IsRootAdmin(uid))return Results.Forbid();
    await using var cmd=db.CreateCommand("""
        SELECT id,owner_id,amount_rub,status,created_at,processed_at
        FROM reklio_payout_requests ORDER BY id DESC LIMIT 100
        """);
    var history=new List<object>();
    await using var reader=await cmd.ExecuteReaderAsync(ct);
    while(await reader.ReadAsync(ct))history.Add(new {
        id=reader.GetInt64(0),ownerId=reader.GetInt64(1),amount=reader.GetDecimal(2),
        status=reader.GetString(3),createdAt=reader.GetDateTime(4),
        processedAt=reader.IsDBNull(5)?(DateTime?)null:reader.GetDateTime(5)});
    return Results.Ok(history);
});
app.MapPost("/api/admin/payouts/{id:long}/decision",async(long id,HttpRequest req,MySqlDataSource db,CancellationToken ct)=>
{
    if(Authorized(req) is not long uid)return Results.Unauthorized();
    if(!IsRootAdmin(uid))return Results.Forbid();
    var input=await req.ReadFromJsonAsync<PayoutDecisionInput>(cancellationToken:ct);
    if(input is null || input.Decision is not ("Paid" or "Rejected"))
        return Results.BadRequest(new{error="Неизвестное действие"});
    var reference=(input.Reference??"").Trim();
    if(input.Decision=="Paid"&&(reference.Length<4||reference.Length>120))
        return Results.BadRequest(new{error="Укажите номер или идентификатор подтверждённого банковского перевода"});
    if(reference.Length>120)return Results.BadRequest();
    await using var conn=await db.OpenConnectionAsync(ct);
    await using var tx=await conn.BeginTransactionAsync(ct);
    await using var cmd=new MySqlCommand("""
        UPDATE reklio_payout_requests
        SET status=@decision,processed_at=UTC_TIMESTAMP(),processed_by=@admin,transfer_reference=@ref
        WHERE id=@id AND status='Pending'
        """,conn,tx);
    cmd.Parameters.AddWithValue("@id",id);
    cmd.Parameters.AddWithValue("@decision",input.Decision);
    cmd.Parameters.AddWithValue("@admin",uid);
    cmd.Parameters.AddWithValue("@ref",reference);
    if(await cmd.ExecuteNonQueryAsync(ct)!=1){await tx.RollbackAsync(ct);return Results.Conflict(new{error="Заявка уже обработана или не найдена"});}
    await using var audit=new MySqlCommand("""
        INSERT INTO reklio_admin_audit(actor_id,action,target_id,details)
        VALUES(@admin,'OwnerPayout',@id,@details)
        """,conn,tx);
    audit.Parameters.AddWithValue("@admin",uid);
    audit.Parameters.AddWithValue("@id",id);
    audit.Parameters.AddWithValue("@details",input.Decision+" "+reference);
    await audit.ExecuteNonQueryAsync(ct);
    await tx.CommitAsync(ct);
    return Results.Ok(new {id,status=input.Decision});
});

app.Run();

record UpdateServicePriceInput(int PriceRub);
record CreateOrderInput(long ChannelId, string Text);
record PublicationReviewInput(string Decision,string Note);
record StaffInput(long TelegramId,string Role);
record ResolveDisputeInput(string Decision,string? Sanction,int? Days);
record OpenDisputeInput(string Reason);
record RescheduleInput(DateTimeOffset ScheduledAt);
record ScheduleOrderInput(long RateId, DateTimeOffset ScheduledAt);

record UpdateRateInput(int Hours, decimal Price);
record UpdateListingInput(bool Listed);
record PaymentDetailsInput(string? CardNumber,string? Phone,string? Bank);
record UpdateCategoryInput(string Category);
record ChannelAliasInput(string Alias);

record ConnectChannelRequest(string Username);

sealed class CatalogChannel(long id,string title,string category,decimal price,string description,string? username)
{
    public long Id {get;}=id;
    public string Title {get;}=title;
    public string Category {get;}=category;
    public decimal Price {get;set;}=price;
    public string Description {get;}=description;
    public string? Username {get;}=username;
    public int? Subscribers {get;set;}
    public long Posts30d {get;set;}
    public long? AvgViews {get;set;}
    public long ViewsSamples {get;set;}
    public long Likes {get;set;}
    public DateTime? PromotedUntil {get;set;}
}

sealed class ShortOidcStateFormat(IMemoryCache cache) : ISecureDataFormat<AuthenticationProperties>
{
    public string Protect(AuthenticationProperties data) => Protect(data, null);
    public string Protect(AuthenticationProperties data, string? purpose)
    {
        var key=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        cache.Set(key, (Data:data, Purpose:purpose),
            new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(10)).SetSize(1));
        return key;
    }

    public AuthenticationProperties? Unprotect(string? protectedText) => Unprotect(protectedText, null);
    public AuthenticationProperties? Unprotect(string? protectedText, string? purpose)
    {
        if(string.IsNullOrEmpty(protectedText) || protectedText.Length!=48) return null;
        if(!cache.TryGetValue(protectedText, out (AuthenticationProperties Data,string? Purpose) entry)) return null;
        cache.Remove(protectedText); // One-time token; replayed callbacks are rejected.
        return entry.Purpose==purpose ? entry.Data : null;
    }
}

record WebOrderDecision(string Action);
record PayoutRequestInput(decimal Amount);
record PayoutDecisionInput(string Decision,string? Reference);
