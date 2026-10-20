using Npgsql;

namespace TelegramManager.Bot;

public sealed class Database(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS users (
                telegram_id BIGINT PRIMARY KEY,
                username TEXT,
                display_name TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS channels (
                telegram_id BIGINT PRIMARY KEY,
                title TEXT NOT NULL,
                username TEXT,
                connected_by BIGINT NOT NULL REFERENCES users(telegram_id),
                connected_at TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_channels_connected_by ON channels(connected_by);
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RegisterAsync(long id, string? username, string name, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO users (telegram_id, username, display_name)
            VALUES ($1, $2, $3)
            ON CONFLICT (telegram_id) DO UPDATE SET username=EXCLUDED.username, display_name=EXCLUDED.display_name;
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue((object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue(name);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveChannelAsync(long channelId, string title, string? username, long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO channels (telegram_id,title,username,connected_by)
            VALUES ($1,$2,$3,$4)
            ON CONFLICT (telegram_id) DO UPDATE SET
              title=EXCLUDED.title,username=EXCLUDED.username,
              connected_by=EXCLUDED.connected_by,connected_at=now();
            """);
        cmd.Parameters.AddWithValue(channelId);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue((object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<(long Id, string Title, string? Username)>> GetChannelsAsync(long ownerId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT telegram_id,title,username FROM channels WHERE connected_by=$1 ORDER BY connected_at DESC");
        cmd.Parameters.AddWithValue(ownerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var channels = new List<(long, string, string?)>();
        while (await reader.ReadAsync(ct))
            channels.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return channels;
    }

    public async Task DeleteChannelAsync(long channelId, long userId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("DELETE FROM channels WHERE telegram_id=$1 AND connected_by=$2");
        cmd.Parameters.AddWithValue(channelId);
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
