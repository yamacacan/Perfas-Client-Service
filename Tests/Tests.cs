using Xunit;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerService1.Services;

public class AesEncryptionServiceTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public void Rejects_keys_that_are_not_32_bytes(string key) =>
        Assert.Throws<ArgumentException>(() => new AesEncryptionService(key));

    [Fact]
    public void Roundtrips_and_uses_random_iv()
    {
        var svc = new AesEncryptionService(Key);
        var a = svc.Encrypt("merhaba dünya");
        var b = svc.Encrypt("merhaba dünya");
        Assert.NotEqual(a, b);

        var raw = Convert.FromBase64String(a);
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(Key);
        aes.IV = raw[..16];
        var plain = aes.DecryptCbc(raw[16..], raw[..16]);
        Assert.Equal("merhaba dünya", Encoding.UTF8.GetString(plain));
    }
}

public class JsonlFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pa_tests_" + Guid.NewGuid().ToString("N"));
    private JsonlFileStore<BrowserData> NewStore() => new(NullLogger.Instance, _dir, "browser");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Rows_appended_after_read_survive_mark_synced()
    {
        var store = NewStore();
        await store.AppendAsync(new BrowserData { Url = "a" }, "browser_u_1.jsonl");
        var batch = await store.GetUnsyncedAsync(10);
        await store.AppendAsync(new BrowserData { Url = "b" }, "browser_u_1.jsonl"); // arrives mid-sync

        await store.MarkSyncedAsync(batch.Select(x => x.SourceFile!));

        var rest = await store.GetUnsyncedAsync(10);
        Assert.Single(rest);
        Assert.Equal("b", rest[0].Url);
    }

    [Fact]
    public async Task Fully_synced_file_is_deleted()
    {
        var store = NewStore();
        await store.AppendAsync(new BrowserData { Url = "a" }, "browser_u_1.jsonl");
        var batch = await store.GetUnsyncedAsync(10);
        await store.MarkSyncedAsync(batch.Select(x => x.SourceFile!));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Never_deletes_files_outside_store_directory()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pa_outside_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "x");
        try
        {
            await NewStore().MarkSyncedAsync(new[] { outside });
            Assert.True(File.Exists(outside));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Limit_is_applied_per_file_so_no_partial_file_is_returned()
    {
        var store = NewStore();
        for (var i = 0; i < 5; i++) await store.AppendAsync(new BrowserData { Url = "u" + i }, "browser_u_1.jsonl");
        Assert.Equal(5, (await store.GetUnsyncedAsync(2)).Count);
    }
}

public class HardwareValueSanitizerTests
{
    [Fact]
    public void Skips_placeholder_manufacturer()
    {
        var r = HardwareValueSanitizer.SelectHardwareCandidate(new string?[] { "To Be Filled By O.E.M.", null, "Dell Inc." }, "PC-1");
        Assert.Equal("Dell Inc.", r);
    }

    [Fact]
    public void Skips_placeholder_and_uniform_serials()
    {
        var r = HardwareValueSanitizer.SelectSerialCandidate(new string?[] { "Default string", "0000000000", "5CD1234XYZ" }, "PC-1");
        Assert.Equal("5CD1234XYZ", r);
    }
}

public class BrowserHistoryReaderTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "pa_hist_" + Guid.NewGuid().ToString("N") + ".db");
    public void Dispose() { try { File.Delete(_db); } catch { } }

    [Fact]
    public async Task Reads_recent_chromium_visits_from_a_copy_and_leaves_no_temp_files()
    {
        SQLitePCL.Batteries_V2.Init();
        var visit = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var micro = (long)((visit - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds * 1000);

        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $@"CREATE TABLE urls(id INTEGER PRIMARY KEY, url TEXT, title TEXT);
                CREATE TABLE visits(id INTEGER PRIMARY KEY, url INTEGER, visit_time INTEGER, visit_duration INTEGER, originator_cache_guid TEXT);
                INSERT INTO urls VALUES (1,'https://example.com/a','A');
                INSERT INTO visits VALUES (1,1,{micro},0,NULL);";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var reader = new BrowserHistoryReader(NullLogger<BrowserHistoryReader>.Instance);
        var rows = await reader.GetChromeRecentUrlsAsync(_db, visit.AddHours(-1), "Chrome");

        Assert.Single(rows);
        Assert.Equal("https://example.com/a", rows[0].Url);
        Assert.Equal(visit, rows[0].LastVisitTime);
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "chrome-temp_history_*.db"));
    }
}
