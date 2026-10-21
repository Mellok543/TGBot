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
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_price_rub DECIMAL(10,2) NOT NULL DEFAULT 0",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_description TEXT NULL",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_formats VARCHAR(255) NOT NULL DEFAULT 'Пост'",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS is_listed BOOLEAN NOT NULL DEFAULT FALSE",
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
        await using var cmd = _source.CreateCommand("""
            INSERT INTO channels (telegram_id, title, username, connected_by)
            VALUES (@channelId, @title, @username, @userId)
            ON DUPLICATE KEY UPDATE
                title=VALUES(title), username=VALUES(username),
                connected_by=VALUES(connected_by), connected_at=CURRENT_TIMESTAMP
            """);
        cmd.Parameters.Add(new MySqlParameter("@channelId", channelId));
        cmd.Parameters.Add(new MySqlParameter("@title", title.Length > 255 ? title[..255] : title));
        cmd.Parameters.Add(new MySqlParameter("@username", (object?)username ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@userId", userId));
        await cmd.ExecuteNonQueryAsync(ct);
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
        await using var cmd = _source.CreateCommand("UPDATE channels SET is_listed=@listed WHERE telegram_id=@id AND connected_by=@owner");
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
            SELECT telegram_id, title, ad_price_rub, COALESCE(ad_description,''), ad_formats
            FROM channels WHERE is_listed=TRUE ORDER BY connected_at DESC LIMIT 50
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var items = new List<(long, string, decimal, string, string)>();
        while (await reader.ReadAsync(ct))
            items.Add((reader.GetInt64(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3), reader.GetString(4)));
        return items;
    }

    public async Task<long?> CreateAdRequestAsync(long channelId, long advertiserId, string type, string? text, string? fileId, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var cmd = new MySqlCommand("""
            INSERT INTO ad_requests (channel_id, advertiser_id, owner_id, content_type, content_text, media_file_id, price_rub)
            SELECT telegram_id, @advertiser, connected_by, @type, @text, @file, ad_price_rub
            FROM channels WHERE telegram_id=@channel AND is_listed=TRUE AND connected_by<>@advertiser
            """, connection);
        cmd.Parameters.Add(new MySqlParameter("@advertiser", advertiserId));
        cmd.Parameters.Add(new MySqlParameter("@channel", channelId));
        cmd.Parameters.Add(new MySqlParameter("@type", type));
        cmd.Parameters.Add(new MySqlParameter("@text", (object?)text ?? DBNull.Value));
        cmd.Parameters.Add(new MySqlParameter("@file", (object?)fileId ?? DBNull.Value));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return null;
        return cmd.LastInsertedId;
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

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
