using MySqlConnector;

namespace TelegramManager.Bot;

public sealed class Database(string connectionString) : IAsyncDisposable
{
    private readonly MySqlDataSource _source = new MySqlDataSourceBuilder(connectionString).Build();

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        foreach (var sql in new[]
        {
            """
            CREATE TABLE IF NOT EXISTS users (
                telegram_id BIGINT PRIMARY KEY,
                username VARCHAR(255) NULL,
                display_name VARCHAR(255) NOT NULL,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS channels (
                telegram_id BIGINT PRIMARY KEY,
                title VARCHAR(255) NOT NULL,
                username VARCHAR(255) NULL,
                connected_by BIGINT NOT NULL,
                connected_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                INDEX idx_channels_connected_by (connected_by),
                CONSTRAINT fk_channels_connected_by FOREIGN KEY (connected_by) REFERENCES users(telegram_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS subscription_until DATETIME NULL",
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS first_channel_at DATETIME NULL",
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS pro_granted_until DATETIME NULL",
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS is_test_account BOOLEAN NOT NULL DEFAULT FALSE",
            """
            CREATE TABLE IF NOT EXISTS star_payments (
                telegram_payment_charge_id VARCHAR(255) PRIMARY KEY,
                user_id BIGINT NOT NULL,
                purpose VARCHAR(32) NOT NULL,
                amount_stars INT NOT NULL,
                order_id BIGINT NULL,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                INDEX idx_star_payments_user (user_id),
                INDEX idx_star_payments_order (order_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS owner_earnings (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                order_id BIGINT NOT NULL,
                owner_id BIGINT NOT NULL,
                amount_stars INT NOT NULL,
                platform_fee_stars INT NOT NULL,
                state VARCHAR(24) NOT NULL DEFAULT 'pending',
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE KEY uq_owner_earnings_order (order_id),
                INDEX idx_owner_earnings_owner (owner_id, state)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            UPDATE users u SET first_channel_at=(SELECT MIN(c.connected_at) FROM channels c WHERE c.connected_by=u.telegram_id)
            WHERE first_channel_at IS NULL AND EXISTS (SELECT 1 FROM channels c WHERE c.connected_by=u.telegram_id)
            """,
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS selected_role VARCHAR(16) NULL",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_price_rub DECIMAL(10,2) NOT NULL DEFAULT 0",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_description TEXT NULL",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_formats VARCHAR(255) NOT NULL DEFAULT 'Пост'",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS is_listed BOOLEAN NOT NULL DEFAULT FALSE",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS category VARCHAR(32) NOT NULL DEFAULT 'other'",
            """
            CREATE TABLE IF NOT EXISTS ad_requests (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                channel_id BIGINT NOT NULL,
                advertiser_id BIGINT NOT NULL,
                owner_id BIGINT NOT NULL,
                content_type VARCHAR(20) NOT NULL,
                content_text TEXT NULL,
                media_file_id VARCHAR(512) NULL,
                price_rub DECIMAL(10,2) NOT NULL,
                status VARCHAR(20) NOT NULL DEFAULT 'Pending',
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                INDEX idx_ad_requests_owner (owner_id, status),
                INDEX idx_ad_requests_advertiser (advertiser_id, status),
                FOREIGN KEY (channel_id) REFERENCES channels(telegram_id),
                FOREIGN KEY (advertiser_id) REFERENCES users(telegram_id),
                FOREIGN KEY (owner_id) REFERENCES users(telegram_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS scheduled_at DATETIME NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS paid_confirmed BOOLEAN NOT NULL DEFAULT FALSE",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS published_message_id INT NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS published_at DATETIME NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS delete_after_hours INT NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS last_error VARCHAR(512) NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS payment_status VARCHAR(24) NOT NULL DEFAULT 'Unpaid'",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS is_test_order BOOLEAN NOT NULL DEFAULT FALSE",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS payout_status VARCHAR(24) NOT NULL DEFAULT 'NotEligible'",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS dispute_reason VARCHAR(512) NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS disputed_at DATETIME NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS placement_verified_at DATETIME NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS payout_eligible_at DATETIME NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS dispute_resolution VARCHAR(32) NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS dispute_resolved_at DATETIME NULL",
            "ALTER TABLE ad_requests ADD COLUMN IF NOT EXISTS dispute_resolved_by BIGINT NULL",
            "CREATE INDEX IF NOT EXISTS idx_ad_requests_payout ON ad_requests (payout_status, status)",
            """
            CREATE TABLE IF NOT EXISTS channel_favorites (
                user_id BIGINT NOT NULL,
                channel_id BIGINT NOT NULL,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (user_id,channel_id),
                INDEX idx_favorites_channel (channel_id),
                FOREIGN KEY (user_id) REFERENCES users(telegram_id),
                FOREIGN KEY (channel_id) REFERENCES channels(telegram_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS channel_reviews (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                order_id BIGINT NOT NULL,
                channel_id BIGINT NOT NULL,
                owner_id BIGINT NOT NULL,
                advertiser_id BIGINT NOT NULL,
                stars TINYINT NOT NULL,
                comment VARCHAR(500) NULL,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                UNIQUE KEY uq_channel_review_order (order_id),
                INDEX idx_reviews_channel (channel_id,stars),
                INDEX idx_reviews_owner (owner_id),
                FOREIGN KEY (order_id) REFERENCES ad_requests(id),
                FOREIGN KEY (channel_id) REFERENCES channels(telegram_id),
                FOREIGN KEY (advertiser_id) REFERENCES users(telegram_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS notification_outbox (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                order_id BIGINT NOT NULL,
                recipient_id BIGINT NOT NULL,
                body VARCHAR(1024) NOT NULL,
                state VARCHAR(16) NOT NULL DEFAULT 'Pending',
                attempts INT NOT NULL DEFAULT 0,
                next_attempt_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                last_error VARCHAR(512) NULL,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE KEY uq_notification_order_recipient_body (order_id,recipient_id,body(160)),
                INDEX idx_notification_due (state,next_attempt_at),
                FOREIGN KEY (order_id) REFERENCES ad_requests(id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """,
            """
            CREATE TABLE IF NOT EXISTS ad_order_events (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                order_id BIGINT NOT NULL,
                actor_id BIGINT NULL,
                event_type VARCHAR(40) NOT NULL,
                details VARCHAR(512) NULL,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                INDEX idx_order_events_order (order_id, id),
                FOREIGN KEY (order_id) REFERENCES ad_requests(id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """
        })
        {
            await using var cmd = new MySqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task RegisterAsync(long id, string? username, string name, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO users (telegram_id, username, display_name)
            VALUES (@id, @username, @name)
            ON DUPLICATE KEY UPDATE username=VALUES(username), display_name=VALUES(display_name)
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        cmd.Parameters.Add(new MySqlParameter("@username", (object?)username ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@name", name.Length > 255 ? name[..255] : name));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveChannelAsync(long channelId, string title, string? username, long userId, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var access = new MySqlCommand("""
            SELECT first_channel_at, COALESCE(GREATEST(subscription_until,pro_granted_until),subscription_until,pro_granted_until) FROM users WHERE telegram_id=@user FOR UPDATE
            """, connection, tx);
        access.Parameters.Add(new MySqlParameter("@user", userId));
        DateTime created;
        DateTime? subscribed;
        await using (var reader = await access.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Профиль пользователя не найден.");
            created = reader.IsDBNull(0) ? DateTime.UtcNow : reader.GetDateTime(0);
            subscribed = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }
        var pro = subscribed is { } expiry && expiry > DateTime.UtcNow;
        var trial = created.AddDays(30) > DateTime.UtcNow;
        if (!pro && !trial)
            throw new InvalidOperationException("Бесплатные 30 дней закончились. Для подключения каналов требуется PRO (299 Stars / 30 дней).");
        await using var count = new MySqlCommand("SELECT COUNT(*) FROM channels WHERE connected_by=@user", connection, tx);
        count.Parameters.Add(new MySqlParameter("@user", userId));
        var existingCount = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        await using var existing = new MySqlCommand("SELECT connected_by FROM channels WHERE telegram_id=@channel FOR UPDATE", connection, tx);
        existing.Parameters.Add(new MySqlParameter("@channel", channelId));
        var existingOwner = await existing.ExecuteScalarAsync(ct);
        if (existingOwner is not null && Convert.ToInt64(existingOwner) != userId)
            throw new InvalidOperationException("Канал уже подключён другим владельцем.");
        if (existingOwner is null && existingCount >= (pro ? 10 : 1))
            throw new InvalidOperationException(pro ? "Лимит PRO — 10 каналов." : "В бесплатной версии доступен только один канал. Для 10 каналов подключите PRO.");
        await using var cmd = new MySqlCommand("""
            INSERT INTO channels (telegram_id, title, username, connected_by)
            VALUES (@channelId, @title, @username, @userId)
            ON DUPLICATE KEY UPDATE title=VALUES(title), username=VALUES(username)
            """, connection, tx);
        cmd.Parameters.Add(new MySqlParameter("@channelId", channelId));
        cmd.Parameters.Add(new MySqlParameter("@title", title.Length > 255 ? title[..255] : title));
        cmd.Parameters.Add(new MySqlParameter("@username", (object?)username ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@userId", userId));
        await cmd.ExecuteNonQueryAsync(ct);
        await using var first = new MySqlCommand("""
            UPDATE users SET first_channel_at=UTC_TIMESTAMP()
            WHERE telegram_id=@user AND first_channel_at IS NULL
            """, connection, tx);
        first.Parameters.Add(new MySqlParameter("@user", userId));
        await first.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<(bool Pro, DateTime TrialEnds, DateTime? ProEnds, int Limit)> GetPlanAsync(long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT first_channel_at, COALESCE(GREATEST(subscription_until,pro_granted_until),subscription_until,pro_granted_until) FROM users WHERE telegram_id=@user");
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Профиль не найден.");
        var created = r.IsDBNull(0) ? (DateTime?)null : r.GetDateTime(0);
        DateTime? expires = r.IsDBNull(1) ? null : r.GetDateTime(1);
        var pro = expires is { } at && at > DateTime.UtcNow;
        return (pro, created?.AddDays(30) ?? DateTime.MaxValue, expires, pro ? 10 : 1);
    }

    public async Task<List<(long Id, string Title, string? Username)>> GetChannelsAsync(long ownerId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT telegram_id, title, username FROM channels
            WHERE connected_by=@ownerId ORDER BY connected_at DESC
            """);
        cmd.Parameters.Add(new MySqlParameter("@ownerId", ownerId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var channels = new List<(long, string, string?)>();
        while (await reader.ReadAsync(ct))
            channels.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return channels;
    }

    public async Task DeleteChannelAsync(long channelId, long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            DELETE FROM channels WHERE telegram_id=@channelId AND connected_by=@userId
            """);
        cmd.Parameters.Add(new MySqlParameter("@channelId", channelId));
        cmd.Parameters.Add(new MySqlParameter("@userId", userId));
        await cmd.ExecuteNonQueryAsync(ct);
    }


    public async Task<(long Id, string Title, decimal Price, string Description, string Formats)?> GetChannelSettingsAsync(long channelId, long ownerId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT telegram_id, title, ad_price_rub, COALESCE(ad_description,''), ad_formats
            FROM channels WHERE telegram_id=@id AND connected_by=@owner
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", channelId));
        cmd.Parameters.Add(new MySqlParameter("@owner", ownerId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt64(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3), reader.GetString(4));
    }

    public async Task<bool> UpdateChannelSettingAsync(long channelId, long ownerId, string field, object value, CancellationToken ct)
    {
        var column = field switch
        {
            "price" => "ad_price_rub",
            "description" => "ad_description",
            "formats" => "ad_formats",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        await using var cmd = _source.CreateCommand($"UPDATE channels SET {column}=@value WHERE telegram_id=@id AND connected_by=@owner");
        cmd.Parameters.Add(new MySqlParameter("@value", value));
        cmd.Parameters.Add(new MySqlParameter("@id", channelId));
        cmd.Parameters.Add(new MySqlParameter("@owner", ownerId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }


    public async Task<bool> SetChannelListedAsync(long id, long owner, bool listed, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("UPDATE channels c JOIN users u ON u.telegram_id=c.connected_by SET c.is_listed=@listed WHERE c.telegram_id=@id AND c.connected_by=@owner AND (@listed=FALSE OR (u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP()) OR (u.first_channel_at IS NULL OR u.first_channel_at>UTC_TIMESTAMP()-INTERVAL 30 DAY))");
        cmd.Parameters.Add(new MySqlParameter("@listed", listed));
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        cmd.Parameters.Add(new MySqlParameter("@owner", owner));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> IsChannelListedAsync(long id, long owner, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT is_listed FROM channels WHERE telegram_id=@id AND connected_by=@owner");
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        cmd.Parameters.Add(new MySqlParameter("@owner", owner));
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task<List<(long Id, string Title, decimal Price, string Description, string Formats)>> GetCatalogAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT c.telegram_id, c.title, c.ad_price_rub, COALESCE(c.ad_description,''), c.ad_formats
            FROM channels c JOIN users u ON u.telegram_id=c.connected_by WHERE c.is_listed=TRUE AND ((u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP()) OR u.created_at>UTC_TIMESTAMP()-INTERVAL 30 DAY) ORDER BY c.connected_at DESC LIMIT 50
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<(long, string, decimal, string, string)>();
        while (await reader.ReadAsync(ct))
            items.Add((reader.GetInt64(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3), reader.GetString(4)));
        return items;
    }

    public async Task<bool> SetChannelCategoryAsync(long channelId, long ownerId, string category, CancellationToken ct)
    {
        if (!new[] { "news", "tech", "games", "business", "education", "entertainment", "lifestyle", "sport", "other" }.Contains(category)) return false;
        await using var cmd = _source.CreateCommand("UPDATE channels SET category=@category WHERE telegram_id=@id AND connected_by=@owner");
        cmd.Parameters.Add(new MySqlParameter("@category", category));
        cmd.Parameters.Add(new MySqlParameter("@id", channelId));
        cmd.Parameters.Add(new MySqlParameter("@owner", ownerId));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<List<(long Id, string Title, decimal Price)>> GetCategoryChannelsAsync(string category, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT c.telegram_id, c.title, c.ad_price_rub FROM channels c JOIN users u ON u.telegram_id=c.connected_by
            WHERE c.is_listed=TRUE AND c.category=@category AND ((u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP()) OR u.created_at>UTC_TIMESTAMP()-INTERVAL 30 DAY) ORDER BY c.connected_at DESC LIMIT 50
            """);
        cmd.Parameters.Add(new MySqlParameter("@category", category));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<(long, string, decimal)>();
        while (await r.ReadAsync(ct))
            items.Add((r.GetInt64(0), r.GetString(1), r.GetDecimal(2)));
        return items;
    }

    public async Task<long?> CreateAdRequestAsync(long channelId, long advertiserId, string type, string? text, string? fileId, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand("""
            INSERT INTO ad_requests (channel_id, advertiser_id, owner_id, content_type, content_text, media_file_id, price_rub, status)
            SELECT c.telegram_id, @advertiser, c.connected_by, @type, @text, @file, c.ad_price_rub, 'Draft'
            FROM channels c JOIN users u ON u.telegram_id=c.connected_by WHERE c.telegram_id=@channel AND c.connected_by<>@advertiser AND c.is_listed=TRUE AND ((u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP()) OR u.created_at>UTC_TIMESTAMP()-INTERVAL 30 DAY)
            """, connection);
        cmd.Parameters.Add(new MySqlParameter("@advertiser", advertiserId));
        cmd.Parameters.Add(new MySqlParameter("@channel", channelId));
        cmd.Parameters.Add(new MySqlParameter("@type", type));
        cmd.Parameters.Add(new MySqlParameter("@text", (object?)text ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@file", (object?)fileId ?? DBNull.Value));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return null;
        var createdId = cmd.LastInsertedId;
        await RecordOrderEventAsync(createdId, advertiserId, "Created", "Заявка создана", ct);
        return createdId;
    }

    public async Task<(long Id, long Owner, long Advertiser, string Channel, string Status, decimal Price, string Type, string? Text, string? File)?> GetAdRequestAsync(long id, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT r.id, r.owner_id, r.advertiser_id, c.title, r.status, r.price_rub, r.content_type, r.content_text, r.media_file_id
            FROM ad_requests r JOIN channels c ON c.telegram_id=r.channel_id WHERE r.id=@id
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
            reader.GetDecimal(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    public async Task<bool> DecideAdRequestAsync(long id, long ownerId, string status, CancellationToken ct)
    {
        if (status is not ("Approved" or "Rejected")) return false;
        await using var cmd = _source.CreateCommand("""
            UPDATE ad_requests SET status=@status WHERE id=@id AND owner_id=@owner AND status='Pending'
            """);
        cmd.Parameters.Add(new MySqlParameter("@status", status));
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        cmd.Parameters.Add(new MySqlParameter("@owner", ownerId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<List<(long Id, string Channel, string Status)>> GetMyAdRequestsAsync(long userId, bool asOwner, CancellationToken ct)
    {
        var field = asOwner ? "owner_id" : "advertiser_id";
        await using var cmd = _source.CreateCommand($"""
            SELECT r.id, c.title, r.status FROM ad_requests r
            JOIN channels c ON c.telegram_id=r.channel_id
            WHERE r.{field}=@user ORDER BY r.created_at DESC LIMIT 30
            """);
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(long, string, string)>();
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }


    public async Task<bool> ProposeAsync(long id, long advertiser, DateTime at, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("UPDATE ad_requests SET status='Pending', scheduled_at=@at WHERE id=@id AND advertiser_id=@user AND status='Draft'");
        cmd.Parameters.Add(new MySqlParameter("@at", at));
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        cmd.Parameters.Add(new MySqlParameter("@user", advertiser));
        var updated = await cmd.ExecuteNonQueryAsync(ct) == 1;
        if (updated) await RecordOrderEventAsync(id, advertiser, "TimeProposed", at.ToString("yyyy-MM-dd HH:mm") + " UTC", ct);
        return updated;
    }

    public async Task<bool> ConfirmAsync(long id, long owner, int? hours, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        long channel;
        DateTime at;
        await using (var get = new MySqlCommand("SELECT channel_id, scheduled_at FROM ad_requests WHERE id=@id AND owner_id=@owner AND status='Pending' AND scheduled_at IS NOT NULL FOR UPDATE", connection, tx))
        {
            get.Parameters.Add(new MySqlParameter("@id", id));
            get.Parameters.Add(new MySqlParameter("@owner", owner));
            await using var r = await get.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return false;
            channel = r.GetInt64(0);
            at = r.GetDateTime(1);
        }
        await using (var locked = new MySqlCommand("SELECT telegram_id FROM channels WHERE telegram_id=@channel FOR UPDATE", connection, tx))
        {
            locked.Parameters.Add(new MySqlParameter("@channel", channel));
            await locked.ExecuteScalarAsync(ct);
        }
        await using (var check = new MySqlCommand("SELECT COUNT(*) FROM ad_requests WHERE channel_id=@channel AND id<>@id AND scheduled_at=@at AND status IN ('Pending','Scheduled','Publishing','Published')", connection, tx))
        {
            check.Parameters.Add(new MySqlParameter("@channel", channel));
            check.Parameters.Add(new MySqlParameter("@id", id));
            check.Parameters.Add(new MySqlParameter("@at", at));
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct)) > 0) return false;
        }
        await using (var cmd = new MySqlCommand("UPDATE ad_requests SET status='AwaitingPayment', paid_confirmed=FALSE, payment_status='Unpaid', delete_after_hours=COALESCE(@hours,24) WHERE id=@id AND owner_id=@owner AND status='Pending' AND scheduled_at>UTC_TIMESTAMP()", connection, tx))
        {
            cmd.Parameters.Add(new MySqlParameter("@id", id));
            cmd.Parameters.Add(new MySqlParameter("@owner", owner));
            cmd.Parameters.Add(new MySqlParameter("@hours", (object?)hours ?? DBNull.Value));
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<List<long>> DueAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT id FROM ad_requests WHERE status='Scheduled' AND paid_confirmed=TRUE AND payment_status IN ('Paid','TestPaid') AND scheduled_at<=UTC_TIMESTAMP() LIMIT 20");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<long>();
        while (await r.ReadAsync(ct)) ids.Add(r.GetInt64(0));
        return ids;
    }

    public async Task<(long Channel, string Type, string? Text, string? File)?> ClaimAsync(long id, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using (var cmd = new MySqlCommand("UPDATE ad_requests SET status='Publishing' WHERE id=@id AND status='Scheduled' AND paid_confirmed=TRUE AND payment_status IN ('Paid','TestPaid') AND scheduled_at<=UTC_TIMESTAMP()", connection))
        {
            cmd.Parameters.Add(new MySqlParameter("@id", id));
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return null;
        }
        await using var get = new MySqlCommand("SELECT channel_id, content_type, content_text, media_file_id FROM ad_requests WHERE id=@id", connection);
        get.Parameters.Add(new MySqlParameter("@id", id));
        await using var r = await get.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    public async Task FinishPublishAsync(long id, int? messageId, string? error, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("UPDATE ad_requests SET status=@status, published_message_id=@msg, published_at=IF(@msg IS NULL,NULL,UTC_TIMESTAMP()), payout_status=IF(@msg IS NULL OR is_test_order=TRUE,'NotEligible','OnHold'), last_error=@error WHERE id=@id AND status='Publishing'");
        cmd.Parameters.Add(new MySqlParameter("@status", messageId is null ? "PublishFailed" : "Published"));
        cmd.Parameters.Add(new MySqlParameter("@msg", (object?)messageId ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@error", (object?)error ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        if (await cmd.ExecuteNonQueryAsync(ct) == 1)
            await RecordOrderEventAsync(id, null, messageId is null ? "PublishFailed" : "Published", error, ct);
    }

    public async Task<List<(long Id, long Channel, int Message)>> DueDeletionsAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT id, channel_id, published_message_id FROM ad_requests WHERE status IN ('Published','Disputed','DisputeResolved') AND published_message_id IS NOT NULL AND last_error IS NULL AND delete_after_hours IS NOT NULL AND DATE_ADD(published_at, INTERVAL delete_after_hours HOUR)<=UTC_TIMESTAMP() LIMIT 20");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(long, long, int)>();
        while (await r.ReadAsync(ct)) list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt32(2)));
        return list;
    }

    public async Task FinishDeletionAsync(long id, string? error, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("UPDATE ad_requests SET status=IF(status='Published',@status,status), published_message_id=IF(@error IS NULL,NULL,published_message_id), payout_status=IF(status IN ('Disputed','DisputeResolved'),'Blocked',IF(is_test_order=TRUE,'NotEligible',IF(@error IS NULL,'AwaitingVerification','OnHold'))), last_error=@error WHERE id=@id AND status IN ('Published','Disputed','DisputeResolved') AND published_message_id IS NOT NULL AND last_error IS NULL");
        cmd.Parameters.Add(new MySqlParameter("@status", error is null ? "Deleted" : "DeleteFailed"));
        cmd.Parameters.Add(new MySqlParameter("@error", (object?)error ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        if (await cmd.ExecuteNonQueryAsync(ct) == 1)
            await RecordOrderEventAsync(id, null, error is null ? "Deleted" : "DeleteFailed", error, ct);
    }


    public async Task<(long Id, string Title, decimal Price, string Description, string Formats)?> GetPublicChannelAsync(long id, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT telegram_id, title, ad_price_rub, COALESCE(ad_description,''), ad_formats
            FROM channels WHERE telegram_id=@id
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.GetString(3), r.GetString(4));
    }

    public async Task<(string Status, DateTime? Scheduled, string? Error, int? MessageId)?> GetScheduleStatusAsync(long id, long user, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT status, scheduled_at, last_error, published_message_id FROM ad_requests
            WHERE id=@id AND (owner_id=@user OR advertiser_id=@user)
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        cmd.Parameters.Add(new MySqlParameter("@user", user));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetDateTime(1),
            r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3));
    }


    public async Task<string?> GetRoleAsync(long user, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT selected_role FROM users WHERE telegram_id=@user");
        cmd.Parameters.Add(new MySqlParameter("@user", user));
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    public async Task SetRoleAsync(long user, string role, CancellationToken ct)
    {
        if (role is not ("owner" or "buyer")) throw new ArgumentOutOfRangeException(nameof(role));
        await using var cmd = _source.CreateCommand("UPDATE users SET selected_role=@role WHERE telegram_id=@user");
        cmd.Parameters.Add(new MySqlParameter("@role", role));
        cmd.Parameters.Add(new MySqlParameter("@user", user));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(long Id, string Title, decimal Price)>> GetPreviouslyOrderedChannelsAsync(long user, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT c.telegram_id, c.title, c.ad_price_rub FROM channels c
            JOIN (SELECT channel_id, MAX(created_at) AS last_order FROM ad_requests
                  WHERE advertiser_id=@user GROUP BY channel_id) a ON a.channel_id=c.telegram_id
            ORDER BY a.last_order DESC LIMIT 50
            """);
        cmd.Parameters.Add(new MySqlParameter("@user", user));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<(long, string, decimal)>();
        while (await r.ReadAsync(ct)) result.Add((r.GetInt64(0), r.GetString(1), r.GetDecimal(2)));
        return result;
    }


    public async Task<DateTime> GetDatabaseUtcAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT UTC_TIMESTAMP()");
        return Convert.ToDateTime(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<DateTime?> FindNextFreeAsync(long requestId, DateTime startUtc, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT r.channel_id FROM ad_requests r WHERE r.id=@id AND r.status='Approved'
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", requestId));
        var channel = await cmd.ExecuteScalarAsync(ct);
        if (channel is null) return null;
        var start = new DateTime(((startUtc.Ticks + TimeSpan.TicksPerMinute * 30 - 1) / (TimeSpan.TicksPerMinute * 30)) * (TimeSpan.TicksPerMinute * 30), DateTimeKind.Utc);
        await using var occupied = _source.CreateCommand("""
            SELECT scheduled_at FROM ad_requests
            WHERE channel_id=@channel AND scheduled_at>=@start AND scheduled_at<@end
            AND status IN ('Pending','Scheduled','Publishing','Published')
            """);
        occupied.Parameters.Add(new MySqlParameter("@channel", channel));
        occupied.Parameters.Add(new MySqlParameter("@start", start));
        occupied.Parameters.Add(new MySqlParameter("@end", start.AddDays(30)));
        var reserved = new HashSet<long>();
        await using (var r = await occupied.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) reserved.Add(r.GetDateTime(0).Ticks);
        for (var slot = start; slot < start.AddDays(30); slot = slot.AddMinutes(30))
            if (!reserved.Contains(slot.Ticks)) return slot;
        return null;
    }

    public async Task<DateTime?> GetSubscriptionUntilAsync(long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT subscription_until FROM users WHERE telegram_id=@user");
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToDateTime(value);
    }

    // Records are idempotent by Telegram charge ID. Never credit funds from callbacks alone.
    public async Task<bool> RecordSubscriptionPaymentAsync(long userId, string chargeId, int stars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chargeId) || stars != 299) return false;
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var insert = new MySqlCommand("""
            INSERT IGNORE INTO star_payments (telegram_payment_charge_id, user_id, purpose, amount_stars)
            VALUES (@charge, @user, 'subscription', @stars)
            """, connection, tx);
        insert.Parameters.Add(new MySqlParameter("@charge", chargeId));
        insert.Parameters.Add(new MySqlParameter("@user", userId));
        insert.Parameters.Add(new MySqlParameter("@stars", stars));
        if (await insert.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return false; }
        await using var update = new MySqlCommand("""
            UPDATE users SET subscription_until=DATE_ADD(
                GREATEST(COALESCE(subscription_until, UTC_TIMESTAMP()), UTC_TIMESTAMP()),
                INTERVAL 30 DAY
            ) WHERE telegram_id=@user
            """, connection, tx);
        update.Parameters.Add(new MySqlParameter("@user", userId));
        if (await update.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return false; }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<(long Pending, long Available, long Paid)> GetOwnerEarningsAsync(long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT COALESCE(SUM(CASE WHEN state='pending' THEN amount_stars ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN state='available' THEN amount_stars ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN state='paid' THEN amount_stars ELSE 0 END),0)
            FROM owner_earnings WHERE owner_id=@user
            """);
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (0, 0, 0);
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }

    public async Task<(long Bought, long Sold, long Published, long Channels, decimal BookedRub, long EarnedStars)> GetProfileSummaryAsync(long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT
              (SELECT COUNT(*) FROM ad_requests WHERE advertiser_id=@user AND status<>'Draft'),
              (SELECT COUNT(*) FROM ad_requests WHERE owner_id=@user AND status IN ('Scheduled','Publishing','Published','Deleted')),
              (SELECT COUNT(*) FROM ad_requests WHERE owner_id=@user AND published_at IS NOT NULL),
              (SELECT COUNT(*) FROM channels WHERE connected_by=@user),
              (SELECT COALESCE(SUM(price_rub),0) FROM ad_requests WHERE owner_id=@user AND status IN ('Scheduled','Publishing','Published','Deleted')),
              (SELECT COALESCE(SUM(amount_stars),0) FROM owner_earnings WHERE owner_id=@user AND state IN ('available','paid'))
            """);
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (0,0,0,0,0,0);
        return (r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3),r.GetDecimal(4),r.GetInt64(5));
    }

    public async Task<List<(string Channel, long Orders, decimal BookedRub, long EarnedStars)>> GetChannelAnalyticsAsync(long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT c.title,
                (SELECT COUNT(*) FROM ad_requests a WHERE a.channel_id=c.telegram_id AND a.status IN ('Scheduled','Publishing','Published','Deleted')),
                (SELECT COALESCE(SUM(a.price_rub),0) FROM ad_requests a WHERE a.channel_id=c.telegram_id AND a.status IN ('Scheduled','Publishing','Published','Deleted')),
                (SELECT COALESCE(SUM(e.amount_stars),0) FROM owner_earnings e JOIN ad_requests a ON a.id=e.order_id
                 WHERE a.channel_id=c.telegram_id AND e.state IN ('available','paid'))
            FROM channels c WHERE c.connected_by=@user ORDER BY c.title LIMIT 10
            """);
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<(string, long, decimal, long)>();
        while (await r.ReadAsync(ct)) rows.Add((r.GetString(0),r.GetInt64(1),r.GetDecimal(2),r.GetInt64(3)));
        return rows;
    }

    public async Task<(long Bought, decimal BookedRub)> GetBuyerAnalyticsAsync(long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT COUNT(*),COALESCE(SUM(price_rub),0) FROM ad_requests
            WHERE advertiser_id=@user AND status IN ('Scheduled','Publishing','Published','Deleted')
            """);
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (0,0);
        return (r.GetInt64(0),r.GetDecimal(1));
    }

    public async Task<bool> GrantProAsync(long userId, int days, CancellationToken ct)
    {
        if (days < 1 || days > 3650) return false;
        await using var cmd = _source.CreateCommand("""
            UPDATE users SET pro_granted_until=DATE_ADD(
                GREATEST(UTC_TIMESTAMP(), COALESCE(pro_granted_until,UTC_TIMESTAMP())),
                INTERVAL @days DAY) WHERE telegram_id=@user
            """);
        cmd.Parameters.Add(new MySqlParameter("@days", days));
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RevokeGrantedProAsync(long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("UPDATE users SET pro_granted_until=NULL WHERE telegram_id=@user");
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> SetTestAccountAsync(long userId, bool enabled, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("UPDATE users SET is_test_account=@enabled WHERE telegram_id=@user");
        cmd.Parameters.Add(new MySqlParameter("@enabled", enabled));
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<List<(long Id, string Name, bool Test)>> GetAdminUsersAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT telegram_id, display_name, is_test_account FROM users
            ORDER BY created_at DESC LIMIT 15
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<(long, string, bool)>();
        while (await r.ReadAsync(ct))
            result.Add((r.GetInt64(0), r.GetString(1), r.GetBoolean(2)));
        return result;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
    // Protected orders: never automatically release money on the basis of a timer alone.
    public async Task<bool> OpenDisputeAsync(long orderId, long actorId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        await using var cmd = _source.CreateCommand("""
            UPDATE ad_requests SET status='Disputed', payout_status='Blocked',
                dispute_reason=@reason, disputed_at=UTC_TIMESTAMP()
            WHERE id=@id AND (advertiser_id=@actor OR owner_id=@actor)
                AND status IN ('Published','Deleted','DeleteFailed')
                AND payout_status NOT IN ('Paid','Refunded')
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", orderId));
        cmd.Parameters.Add(new MySqlParameter("@actor", actorId));
        cmd.Parameters.Add(new MySqlParameter("@reason", reason.Length > 512 ? reason[..512] : reason));
        var updated = await cmd.ExecuteNonQueryAsync(ct) == 1;
        if (updated) await RecordOrderEventAsync(orderId, actorId, "DisputeOpened", reason, ct);
        return updated;
    }

    public async Task<bool> MarkPlacementVerifiedAsync(long orderId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            UPDATE ad_requests SET placement_verified_at=UTC_TIMESTAMP(),
                payout_status='ReadyForManualReview', payout_eligible_at=UTC_TIMESTAMP()
            WHERE id=@id AND status='Deleted' AND payout_status='AwaitingVerification'
                AND payment_status='Paid'
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", orderId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<List<(long Id, string Channel, string Reason)>> GetOpenDisputesAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT a.id, c.title, COALESCE(a.dispute_reason, 'Не указана')
            FROM ad_requests a JOIN channels c ON c.telegram_id=a.channel_id
            WHERE a.status='Disputed' AND a.payout_status='Blocked'
            ORDER BY a.disputed_at DESC LIMIT 30
            """);
        var items = new List<(long, string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return items;
    }

    // Admin decision is only a recorded recommendation; no funds are moved.
    public async Task<bool> ResolveDisputeAsync(long orderId, long adminId, string decision, CancellationToken ct)
    {
        if (decision is not ("RefundRecommended" or "PayoutRecommended")) return false;
        await using var cmd = _source.CreateCommand("""
            UPDATE ad_requests
            SET status='DisputeResolved', payout_status='Blocked',
                dispute_resolution=@decision, dispute_resolved_at=UTC_TIMESTAMP(),
                dispute_resolved_by=@admin
            WHERE id=@id AND status='Disputed' AND payout_status='Blocked'
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", orderId));
        cmd.Parameters.Add(new MySqlParameter("@admin", adminId));
        cmd.Parameters.Add(new MySqlParameter("@decision", decision));
        var updated = await cmd.ExecuteNonQueryAsync(ct) == 1;
        if (updated) await RecordOrderEventAsync(orderId, adminId, "DisputeResolved", decision, ct);
        return updated;
    }

    // A test checkout is restricted to allowlisted accounts and never represents actual revenue.
    public async Task<bool> CompleteTestCheckoutAsync(long orderId, long buyerId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            UPDATE ad_requests a JOIN users u ON u.telegram_id=a.advertiser_id
            SET a.status='Scheduled', a.paid_confirmed=TRUE, a.payment_status='TestPaid',
                a.is_test_order=TRUE, a.payout_status='NotEligible'
            WHERE a.id=@id AND a.advertiser_id=@buyer AND a.status='AwaitingPayment'
                AND a.scheduled_at>UTC_TIMESTAMP()
                AND u.is_test_account=TRUE
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", orderId));
        cmd.Parameters.Add(new MySqlParameter("@buyer", buyerId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<(DateTime Created, DateTime? Scheduled, DateTime? Published, int? DurationHours,
        string Payment, string Payout, bool Test, string? Dispute, string? Resolution, int? MessageId)?>
        GetAdOrderDetailsAsync(long orderId, long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT created_at, scheduled_at, published_at, delete_after_hours,
                   payment_status, payout_status, is_test_order, dispute_reason,
                   dispute_resolution, published_message_id
            FROM ad_requests WHERE id=@id AND (advertiser_id=@user OR owner_id=@user)
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", orderId));
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetDateTime(0), reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetString(4), reader.GetString(5), reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9));
    }

    public async Task RecordOrderEventAsync(long orderId, long? actorId, string eventType, string? details, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO ad_order_events(order_id,actor_id,event_type,details)
            VALUES (@order,@actor,@type,@details)
            """);
        cmd.Parameters.Add(new MySqlParameter("@order", orderId));
        cmd.Parameters.Add(new MySqlParameter("@actor", (object?)actorId ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@type", eventType));
        cmd.Parameters.Add(new MySqlParameter("@details", (object?)details ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(DateTime At, string Event, string? Details)>> GetOrderHistoryAsync(long orderId, long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT e.created_at, e.event_type, e.details
            FROM ad_order_events e JOIN ad_requests a ON a.id=e.order_id
            WHERE e.order_id=@order AND (a.owner_id=@user OR a.advertiser_id=@user)
            ORDER BY e.id DESC LIMIT 25
            """);
        cmd.Parameters.Add(new MySqlParameter("@order", orderId));
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var events = new List<(DateTime, string, string?)>();
        while (await reader.ReadAsync(ct))
            events.Add((reader.GetDateTime(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return events;
    }

    public async Task<(long Owner, long Advertiser)?> GetOrderParticipantsAsync(long id, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT owner_id,advertiser_id FROM ad_requests WHERE id=@id");
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0),reader.GetInt64(1)) : null;
    }

    public async Task EnqueueOrderNotificationAsync(long orderId, string body, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT IGNORE INTO notification_outbox(order_id,recipient_id,body)
            SELECT a.id, u.telegram_id, @body FROM ad_requests a
            JOIN (SELECT a1.owner_id AS telegram_id FROM ad_requests a1 WHERE a1.id=@id
                  UNION SELECT a2.advertiser_id FROM ad_requests a2 WHERE a2.id=@id) u
            WHERE a.id=@id
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", orderId));
        cmd.Parameters.Add(new MySqlParameter("@body", body));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(long Id,long Recipient,string Body)>> ClaimNotificationsAsync(CancellationToken ct)
    {
        await using (var recover = _source.CreateCommand("""
            UPDATE notification_outbox
            SET state=IF(attempts>=8,'Failed','Pending'),
                next_attempt_at=UTC_TIMESTAMP()
            WHERE state='Sending' AND next_attempt_at<DATE_SUB(UTC_TIMESTAMP(), INTERVAL 5 MINUTE)
            """))
            await recover.ExecuteNonQueryAsync(ct);
        await using var cmd = _source.CreateCommand("""
            SELECT id,recipient_id,body FROM notification_outbox
            WHERE state='Pending' AND next_attempt_at<=UTC_TIMESTAMP()
            ORDER BY id LIMIT 20
            """);
        var items = new List<(long,long,string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) items.Add((reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2)));
        return items;
    }

    public async Task<bool> TryClaimNotificationAsync(long id, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            UPDATE notification_outbox SET state='Sending',attempts=attempts+1, next_attempt_at=UTC_TIMESTAMP()
            WHERE id=@id AND state='Pending' AND next_attempt_at<=UTC_TIMESTAMP()
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        return await cmd.ExecuteNonQueryAsync(ct)==1;
    }

    public async Task CompleteNotificationAsync(long id, string? error, bool permanent, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            UPDATE notification_outbox
            SET state=CASE WHEN @error IS NULL THEN 'Sent'
                           WHEN @permanent=TRUE OR attempts>=8 THEN 'Failed'
                           ELSE 'Pending' END,
                last_error=@error,
                next_attempt_at=DATE_ADD(UTC_TIMESTAMP(), INTERVAL LEAST(3600,POW(2,LEAST(attempts,10))*15) SECOND)
            WHERE id=@id AND state='Sending'
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", id));
        cmd.Parameters.Add(new MySqlParameter("@error", (object?)error ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@permanent", permanent));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnqueueNotificationToUserAsync(long orderId, long recipientId, string body, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT IGNORE INTO notification_outbox(order_id,recipient_id,body)
            SELECT id,@recipient,@body FROM ad_requests WHERE id=@id
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", orderId));
        cmd.Parameters.Add(new MySqlParameter("@recipient", recipientId));
        cmd.Parameters.Add(new MySqlParameter("@body", body));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(long Pending, long Sending, long Failed, long StuckPublishing, long DeleteFailures)> GetOperationsSummaryAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT
                (SELECT COUNT(*) FROM notification_outbox WHERE state='Pending'),
                (SELECT COUNT(*) FROM notification_outbox WHERE state='Sending'),
                (SELECT COUNT(*) FROM notification_outbox WHERE state='Failed'),
                (SELECT COUNT(*) FROM ad_requests WHERE status='Publishing' AND updated_at<DATE_SUB(UTC_TIMESTAMP(),INTERVAL 5 MINUTE)),
                (SELECT COUNT(*) FROM ad_requests WHERE status='DeleteFailed' OR (last_error IS NOT NULL AND status IN ('Disputed','DisputeResolved')))
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return default;
        return (reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4));
    }

    public async Task<List<(long Id, string Status, string? Error)>> GetOrdersNeedingAttentionAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT id,status,last_error FROM ad_requests
            WHERE status IN ('PublishFailed','DeleteFailed')
               OR (status='Publishing' AND updated_at<DATE_SUB(UTC_TIMESTAMP(),INTERVAL 5 MINUTE))
               OR (status IN ('Disputed','DisputeResolved') AND last_error IS NOT NULL)
            ORDER BY updated_at DESC LIMIT 15
            """);
        var result = new List<(long,string,string?)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetInt64(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2)));
        return result;
    }

    public async Task<List<(long Id, long OrderId, long Recipient, int Attempts, string? Error)>> GetFailedNotificationsAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT id,order_id,recipient_id,attempts,last_error FROM notification_outbox
            WHERE state='Failed' ORDER BY id DESC LIMIT 15
            """);
        var items = new List<(long,long,long,int,string?)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add((reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),
                reader.GetInt32(3),reader.IsDBNull(4)?null:reader.GetString(4)));
        return items;
    }

    // Only explicit admin action may retry a permanently failed notification.
    // An already sent or currently leased notification is never reset.
    public async Task<bool> RetryFailedNotificationAsync(long notificationId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            UPDATE notification_outbox
            SET state='Pending', attempts=0, next_attempt_at=UTC_TIMESTAMP(), last_error=NULL
            WHERE id=@id AND state='Failed'
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", notificationId));
        return await cmd.ExecuteNonQueryAsync(ct)==1;
    }

    // Ratings are based on unique advertisers with completed, non-test placements.
    // No fabricated reviews or unverified subscriber counts.
    public async Task<(int Completed, int RepeatClients, decimal Rating)?> GetOwnerReputationAsync(long ownerId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT COUNT(*), COUNT(DISTINCT advertiser_id)
            FROM ad_requests
            WHERE owner_id=@owner AND status IN ('Published','Deleted')
              AND payment_status='Paid' AND is_test_order=FALSE
              AND payout_status<>'Blocked'
            """);
        cmd.Parameters.Add(new MySqlParameter("@owner", ownerId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var completed = reader.GetInt32(0);
        var clients = reader.GetInt32(1);
        // This is a transparent activity score, not a subjective 5-star review.
        var score = Math.Min(100m, completed * 5m + clients * 10m);
        return (completed, clients, score);
    }

    public async Task<(long Owner, string Category, string? Username)?> GetChannelPublicInfoAsync(long channelId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT connected_by,category,username FROM channels WHERE telegram_id=@id
            """);
        cmd.Parameters.Add(new MySqlParameter("@id", channelId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetInt64(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2))
            : null;
    }

    public async Task<List<(long Id, string Title, decimal Price, int Completed, int Clients)>> BrowseChannelsAsync(
        string category, string sort, int page, CancellationToken ct)
    {
        page = Math.Clamp(page, 0, 100);
        var order = sort switch
        {
            "price" => "c.ad_price_rub ASC, c.telegram_id",
            "expensive" => "c.ad_price_rub DESC, c.telegram_id",
            "rating" => "completed DESC, clients DESC, c.telegram_id",
            _ => "c.connected_at DESC, c.telegram_id"
        };
        await using var cmd = _source.CreateCommand($"""
            SELECT c.telegram_id,c.title,c.ad_price_rub,
                COUNT(DISTINCT CASE WHEN a.status IN ('Published','Deleted') AND a.payment_status='Paid'
                     AND a.is_test_order=FALSE AND a.payout_status<>'Blocked' THEN a.id END) AS completed,
                COUNT(DISTINCT CASE WHEN a.status IN ('Published','Deleted') AND a.payment_status='Paid'
                     AND a.is_test_order=FALSE AND a.payout_status<>'Blocked' THEN a.advertiser_id END) AS clients
            FROM channels c
            JOIN users u ON u.telegram_id=c.connected_by
            LEFT JOIN ad_requests a ON a.owner_id=c.connected_by
            WHERE c.is_listed=TRUE AND (@category='all' OR c.category=@category)
              AND ((u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP())
                OR u.created_at>UTC_TIMESTAMP()-INTERVAL 30 DAY)
            GROUP BY c.telegram_id,c.title,c.ad_price_rub,c.connected_at
            ORDER BY {order} LIMIT 8 OFFSET @offset
            """);
        cmd.Parameters.Add(new MySqlParameter("@category", category));
        cmd.Parameters.Add(new MySqlParameter("@offset", page*8));
        var items = new List<(long,string,decimal,int,int)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add((reader.GetInt64(0),reader.GetString(1),reader.GetDecimal(2),reader.GetInt32(3),reader.GetInt32(4)));
        return items;
    }

    public async Task<int> CountBrowseChannelsAsync(string category, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT COUNT(*) FROM channels c JOIN users u ON u.telegram_id=c.connected_by
            WHERE c.is_listed=TRUE AND (@category='all' OR c.category=@category)
              AND ((u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP())
                OR u.created_at>UTC_TIMESTAMP()-INTERVAL 30 DAY)
            """);
        cmd.Parameters.Add(new MySqlParameter("@category", category));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<bool> ToggleFavoriteAsync(long userId, long channelId, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var locked = new MySqlCommand("SELECT telegram_id FROM channels WHERE telegram_id=@channel FOR UPDATE", connection, tx))
        {
            locked.Parameters.Add(new MySqlParameter("@channel", channelId));
            if (await locked.ExecuteScalarAsync(ct) is null) return false;
        }
        await using (var remove = new MySqlCommand("DELETE FROM channel_favorites WHERE user_id=@user AND channel_id=@channel", connection, tx))
        {
            remove.Parameters.Add(new MySqlParameter("@user", userId));
            remove.Parameters.Add(new MySqlParameter("@channel", channelId));
            if (await remove.ExecuteNonQueryAsync(ct)>0)
            {
                await tx.CommitAsync(ct);
                return false;
            }
        }
        await using (var insert = new MySqlCommand("INSERT IGNORE INTO channel_favorites(user_id,channel_id) VALUES (@user,@channel)", connection, tx))
        {
            insert.Parameters.Add(new MySqlParameter("@user", userId));
            insert.Parameters.Add(new MySqlParameter("@channel", channelId));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> IsFavoriteAsync(long userId, long channelId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("SELECT COUNT(*) FROM channel_favorites WHERE user_id=@user AND channel_id=@channel");
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        cmd.Parameters.Add(new MySqlParameter("@channel", channelId));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct))>0;
    }

    public async Task<List<(long Id,string Title,decimal Price)>> GetFavoriteChannelsAsync(long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT c.telegram_id,c.title,c.ad_price_rub FROM channel_favorites f
            JOIN channels c ON c.telegram_id=f.channel_id
            JOIN users u ON u.telegram_id=c.connected_by
            WHERE f.user_id=@user AND c.is_listed=TRUE
              AND ((u.subscription_until>UTC_TIMESTAMP() OR u.pro_granted_until>UTC_TIMESTAMP())
                OR u.created_at>UTC_TIMESTAMP()-INTERVAL 30 DAY)
            ORDER BY f.created_at DESC LIMIT 30
            """);
        cmd.Parameters.Add(new MySqlParameter("@user", userId));
        var items = new List<(long,string,decimal)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
            items.Add((reader.GetInt64(0),reader.GetString(1),reader.GetDecimal(2)));
        return items;
    }

    public async Task<bool> CanReviewOrderAsync(long orderId,long advertiserId,CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT COUNT(*) FROM ad_requests a WHERE a.id=@id AND a.advertiser_id=@user
              AND a.status IN ('Published','Deleted') AND a.payment_status='Paid'
              AND a.is_test_order=FALSE AND a.payout_status<>'Blocked'
            """);
        cmd.Parameters.Add(new MySqlParameter("@id",orderId));
        cmd.Parameters.Add(new MySqlParameter("@user",advertiserId));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct))>0;
    }

    public async Task<bool> SaveReviewAsync(long orderId,long advertiserId,int stars,CancellationToken ct)
    {
        if(stars<1||stars>5) return false;
        await using var cmd = _source.CreateCommand("""
            INSERT INTO channel_reviews(order_id,channel_id,owner_id,advertiser_id,stars)
            SELECT a.id,a.channel_id,a.owner_id,a.advertiser_id,@stars FROM ad_requests a
            WHERE a.id=@id AND a.advertiser_id=@user AND a.status IN ('Published','Deleted')
              AND a.payment_status='Paid' AND a.is_test_order=FALSE AND a.payout_status<>'Blocked'
            ON DUPLICATE KEY UPDATE stars=IF(advertiser_id=VALUES(advertiser_id),VALUES(stars),stars)
            """);
        cmd.Parameters.Add(new MySqlParameter("@id",orderId));
        cmd.Parameters.Add(new MySqlParameter("@user",advertiserId));
        cmd.Parameters.Add(new MySqlParameter("@stars",stars));
        return await cmd.ExecuteNonQueryAsync(ct)>0;
    }

    public async Task<bool> SaveReviewCommentAsync(long orderId,long advertiserId,string comment,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(comment)||comment.Length>500)return false;
        await using var cmd = _source.CreateCommand("""
            UPDATE channel_reviews SET comment=@comment
            WHERE order_id=@id AND advertiser_id=@user
            """);
        cmd.Parameters.Add(new MySqlParameter("@id",orderId));
        cmd.Parameters.Add(new MySqlParameter("@user",advertiserId));
        cmd.Parameters.Add(new MySqlParameter("@comment",comment));
        return await cmd.ExecuteNonQueryAsync(ct)>0;
    }

    public async Task<(double Average,int Count)> GetChannelReviewSummaryAsync(long channelId,CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT COALESCE(AVG(r.stars),0),COUNT(*) FROM channel_reviews r
            JOIN ad_requests a ON a.id=r.order_id
            WHERE r.channel_id=@channel AND a.status IN ('Published','Deleted')
              AND a.payment_status='Paid' AND a.is_test_order=FALSE AND a.payout_status<>'Blocked'
            """);
        cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetDouble(0),reader.GetInt32(1)) : (0,0);
    }

    public async Task<List<(int Stars,string? Comment,DateTime At)>> GetChannelReviewsAsync(long channelId,CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT r.stars,r.comment,r.created_at FROM channel_reviews r
            JOIN ad_requests a ON a.id=r.order_id
            WHERE r.channel_id=@channel AND a.status IN ('Published','Deleted')
              AND a.payment_status='Paid' AND a.is_test_order=FALSE AND a.payout_status<>'Blocked'
            ORDER BY r.updated_at DESC LIMIT 10
            """);
        cmd.Parameters.Add(new MySqlParameter("@channel",channelId));
        var items=new List<(int,string?,DateTime)>();
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
            items.Add((reader.GetInt32(0),reader.IsDBNull(1)?null:reader.GetString(1),reader.GetDateTime(2)));
        return items;
    }

}
