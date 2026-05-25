using FlashlightApp.Core;
using Microsoft.Data.Sqlite;

namespace FlashlightApp.Core.Tests;

public class SqliteLogStoreQueryTests
{
    private static FlashAttemptRecord Sample(string batchId = "B-1",
        FlashResult result = FlashResult.Pass, string? err = null, int durationMs = 800)
        => new(
            TsUtc:           DateTime.UtcNow,
            Operator:        "Iryna",
            StationId:       "BENCH-1",
            BatchId:         batchId,
            ProductId:       "pocket-light",
            FirmwareVersion: "1.0.0",
            FirmwareSha256:  "0000000000000000000000000000000000000000000000000000000000000000",
            TargetBmpMatch:  "PY32Fxxx",
            TargetDetected:  "PY32Fxxx M0+",
            TargetFlashKb:   32,
            ComPort:         "COM30",
            ProbeSerial:     null,
            Power:           PowerMode.External,
            ConnectRst:      false,
            BmpFrequencyHz:  1_000_000,
            Result:          result,
            ErrorCode:       err,
            ErrorMessage:    err is null ? null : "details",
            DurationMs:      durationMs,
            GdbTail:         null);

    [Fact]
    public void QueryRecent_returns_newest_first_and_respects_limit()
    {
        using var store = new SqliteLogStore(":memory:");
        for (int i = 0; i < 5; i++)
            store.Append(Sample(durationMs: 100 + i));

        var rows = store.QueryRecent(limit: 3);
        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].Id > rows[2].Id, "newest first");
    }

    [Fact]
    public void QueryRecent_carries_required_columns()
    {
        using var store = new SqliteLogStore(":memory:");
        store.Append(Sample(result: FlashResult.Fail, err: "E_TIMEOUT"));
        var row = store.QueryRecent().Single();
        Assert.Equal("FAIL",       row.Result);
        Assert.Equal("E_TIMEOUT",  row.ErrorCode);
        Assert.Equal("pocket-light", row.ProductId);
        Assert.Equal("PY32Fxxx M0+", row.TargetDetected);
    }

    [Fact]
    public void CountsForBatch_separates_pass_and_fail()
    {
        using var store = new SqliteLogStore(":memory:");
        for (int i = 0; i < 7; i++) store.Append(Sample(batchId: "A"));
        for (int i = 0; i < 3; i++) store.Append(Sample(batchId: "A", result: FlashResult.Fail, err: "E_VERIFY_MISMATCH"));
        store.Append(Sample(batchId: "B"));

        var a = store.CountsForBatch("A");
        Assert.Equal((10, 7, 3), a);

        var b = store.CountsForBatch("B");
        Assert.Equal((1, 1, 0), b);

        var missing = store.CountsForBatch("Z");
        Assert.Equal((0, 0, 0), missing);
    }

    [Fact]
    public void Roundtrip_pass_and_fail_appear_correctly_in_history()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"hist-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SqliteLogStore(tmp))
            {
                store.Append(Sample());
                store.Append(Sample(result: FlashResult.Fail, err: "E_LOAD_FAILED"));
            }
            using (var reopen = new SqliteLogStore(tmp))
            {
                var rows = reopen.QueryRecent();
                Assert.Equal(2, rows.Count);
                Assert.Equal("FAIL", rows[0].Result);
                Assert.Equal("PASS", rows[1].Result);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
