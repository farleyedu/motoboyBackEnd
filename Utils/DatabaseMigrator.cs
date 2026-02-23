using Npgsql;
using System.Text;

namespace motoboyBackEnd.Utils;

public class DatabaseMigrator
{
    private readonly string _connectionString;

    public DatabaseMigrator(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    public async Task<bool> ExecuteSqlFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (!File.Exists(filePath))
        {
            return false;
        }

        var sql = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        return await ExecuteSqlAsync(sql);
    }

    public async Task<bool> ExecuteSqlAsync(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<Dictionary<string, object>>> QueryAsync(string sql)
    {
        var results = new List<Dictionary<string, object>>();

        if (string.IsNullOrWhiteSpace(sql))
        {
            return results;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            }

            results.Add(row);
        }

        return results;
    }
}
