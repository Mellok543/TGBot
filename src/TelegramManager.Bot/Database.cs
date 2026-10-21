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
            """
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_price_rub DECIMAL(10,2) NOT NULL DEFAULT 0",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_description TEXT NULL",
            "ALTER TABLE channels ADD COLUMN IF NOT EXISTS ad_formats VARCHAR(255) NOT NULL DEFAULT 'Пост'"
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

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
