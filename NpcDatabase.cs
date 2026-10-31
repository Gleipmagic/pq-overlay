using Microsoft.Data.Sqlite;
using System.Net.Http;
using System.IO;

namespace EQMobOverlay;

public sealed class NpcDatabase
{
    // Public Project Quarm database release maintained by PQ Companion.
    private const string DatabaseUrl = "https://github.com/jasonsoprovich/pq-companion/releases/download/data-latest/quarm.db";

    private readonly string _dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQMobOverlay");
    private string DbPath => Path.Combine(_dataDir, "quarm.db");

    private TableShape? _shape;
    private readonly Dictionary<string, NpcInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string Status { get; private set; } = "Database not loaded";

    public async Task EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDir);
        if (!File.Exists(DbPath) || new FileInfo(DbPath).Length < 1_000_000)
        {
            Status = "Downloading Quarm NPC database…";
            progress?.Report(Status);
            var temp = DbPath + ".download";
            if (File.Exists(temp)) File.Delete(temp);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(DatabaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output, cancellationToken);

            File.Move(temp, DbPath, true);
        }

        _shape = DiscoverTableShape();
        Status = _shape is null ? "NPC table not recognized" : "NPC database ready";
        progress?.Report(Status);
    }

    public async Task<NpcInfo?> FindAsync(string targetName, uint zoneId, CancellationToken cancellationToken = default)
    {
        if (_shape is null) return null;
        var normalized = EqMemoryReader.NormalizeNpcName(targetName);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        var key = $"{zoneId}:{normalized}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        await using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Cache=Shared");
        await connection.OpenAsync(cancellationToken);

        var s = _shape;
        var idExpr = s.Id is null ? "0" : Q(s.Id);
        var order = s.Id is null
            ? $"ORDER BY {Q(s.Hp)} DESC"
            : $"ORDER BY CASE WHEN {Q(s.Id)} BETWEEN @zoneBase AND @zoneEnd THEN 0 ELSE 1 END, {Q(s.Hp)} DESC";

        var sql = $@"
SELECT {idExpr}, {Q(s.Name)}, {Q(s.Hp)}, {Q(s.MR)}, {Q(s.CR)}, {Q(s.DR)}, {Q(s.FR)}, {Q(s.PR)}
FROM {Q(s.Table)}
WHERE lower(trim(replace(replace(replace({Q(s.Name)}, '#', ''), '_', ' '), '  ', ' '))) = @name
{order}
LIMIT 1;";

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", normalized);
        command.Parameters.AddWithValue("@zoneBase", (long)zoneId * 1000L);
        command.Parameters.AddWithValue("@zoneEnd", (long)zoneId * 1000L + 999L);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            _cache[key] = null;
            return null;
        }

        var info = new NpcInfo(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? targetName : reader.GetString(1).TrimStart('#').Replace('_', ' '),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            GetInt(reader, 3), GetInt(reader, 4), GetInt(reader, 5), GetInt(reader, 6), GetInt(reader, 7));
        _cache[key] = info;
        return info;
    }

    private TableShape? DiscoverTableShape()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        connection.Open();

        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        foreach (var table in tables.OrderByDescending(t => t.Contains("npc", StringComparison.OrdinalIgnoreCase)))
        {
            var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({Q(table)})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var col = reader.GetString(1);
                cols[col] = col;
            }

            string? Find(params string[] names) => names.Select(n => cols.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => v is not null);
            var name = Find("name");
            var hp = Find("hp", "hitpoints");
            var mr = Find("MR", "mr");
            var cr = Find("CR", "cr");
            var dr = Find("DR", "dr");
            var fr = Find("FR", "fr");
            var pr = Find("PR", "pr");
            if (name is null || hp is null || mr is null || cr is null || dr is null || fr is null || pr is null) continue;

            return new TableShape(table, Find("id", "npc_id", "npcid"), name, hp, mr, cr, dr, fr, pr);
        }

        return null;
    }

    private static int GetInt(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
    private static string Q(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private sealed record TableShape(string Table, string? Id, string Name, string Hp, string MR, string CR, string DR, string FR, string PR);
}
