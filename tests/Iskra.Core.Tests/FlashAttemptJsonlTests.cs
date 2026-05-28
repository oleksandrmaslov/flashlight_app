using System.Text.Json;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class FlashAttemptJsonlTests
{
    private static UnsyncedFlashAttempt Make(long id, string? probe = null, string? gdbTail = "Section .text ... matched.",
                                              FlashResult result = FlashResult.Pass, string? err = null,
                                              PowerMode power = PowerMode.External, bool connectRst = false)
        => new(id, new FlashAttemptRecord(
            TsUtc:           new DateTime(2026, 5, 25, 14, 30, 0, DateTimeKind.Utc),
            Operator:        "Iryna",
            StationId:       "BENCH-1",
            BatchId:         "B-2026-001",
            ProductId:       "ci-clop",
            FirmwareVersion: "1.0.0",
            FirmwareSha256:  "abcdef",
            TargetBmpMatch:  "PY32Fxxx",
            TargetDetected:  "PY32Fxxx M0+",
            TargetFlashKb:   32,
            ComPort:         "COM30",
            ProbeSerial:     probe,
            Power:           power,
            ConnectRst:      connectRst,
            BmpFrequencyHz:  1_000_000,
            Result:          result,
            ErrorCode:       err,
            ErrorMessage:    err is null ? null : "details",
            DurationMs:      820,
            GdbTail:         gdbTail));

    [Fact]
    public void Serialize_emits_one_line_with_schema_version()
    {
        var s = FlashAttemptJsonl.Serialize(Make(42));
        Assert.DoesNotContain('\n', s);
        using var doc = JsonDocument.Parse(s);
        Assert.Equal(1, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(42, doc.RootElement.GetProperty("local_id").GetInt64());
    }

    [Fact]
    public void Serialize_writes_every_column_with_snake_case_names()
    {
        var s = FlashAttemptJsonl.Serialize(Make(1));
        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        var expected = new[]
        {
            "schema_version", "local_id", "ts_utc", "operator", "station_id",
            "batch_id", "product_id", "firmware_version", "firmware_sha256",
            "target_bmp_match", "target_detected", "target_flash_kb",
            "com_port", "probe_serial", "power_mode", "connect_rst",
            "bmp_frequency_hz", "result", "error_code", "error_message",
            "duration_ms", "gdb_tail"
        };
        foreach (var name in expected)
            Assert.True(root.TryGetProperty(name, out _), $"missing field: {name}");
    }

    [Fact]
    public void Null_fields_are_serialized_as_null_not_omitted()
    {
        // Stable shape matters for the SQLite ingest action; missing keys break
        // strict ingest scripts. Belt-and-braces: serialize nulls explicitly.
        var s = FlashAttemptJsonl.Serialize(Make(1, probe: null, gdbTail: null));
        using var doc = JsonDocument.Parse(s);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("probe_serial").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("gdb_tail").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("error_code").ValueKind);
    }

    [Fact]
    public void Power_mode_is_lowercase_to_match_sqlite_column()
    {
        var ext = FlashAttemptJsonl.Serialize(Make(1, power: PowerMode.External));
        var pro = FlashAttemptJsonl.Serialize(Make(1, power: PowerMode.Probe));
        Assert.Contains("\"power_mode\":\"external\"", ext);
        Assert.Contains("\"power_mode\":\"probe\"",    pro);
    }

    [Fact]
    public void Result_is_uppercase_PASS_or_FAIL()
    {
        Assert.Contains("\"result\":\"PASS\"", FlashAttemptJsonl.Serialize(Make(1, result: FlashResult.Pass)));
        Assert.Contains("\"result\":\"FAIL\"", FlashAttemptJsonl.Serialize(Make(1, result: FlashResult.Fail, err: "E_TIMEOUT")));
    }

    [Fact]
    public void Connect_rst_is_real_boolean_not_integer()
    {
        var s = FlashAttemptJsonl.Serialize(Make(1, connectRst: true));
        using var doc = JsonDocument.Parse(s);
        Assert.Equal(JsonValueKind.True, doc.RootElement.GetProperty("connect_rst").ValueKind);
    }

    [Fact]
    public void Ts_utc_serialized_as_iso8601_with_trailing_Z()
    {
        var s = FlashAttemptJsonl.Serialize(Make(1));
        using var doc = JsonDocument.Parse(s);
        var ts = doc.RootElement.GetProperty("ts_utc").GetString();
        Assert.NotNull(ts);
        Assert.EndsWith("Z", ts);
        Assert.Equal(new DateTime(2026, 5, 25, 14, 30, 0, DateTimeKind.Utc), DateTime.Parse(ts!).ToUniversalTime());
    }

    [Fact]
    public void SerializeBatch_produces_one_line_per_row_with_trailing_newlines()
    {
        var chunk = FlashAttemptJsonl.SerializeBatch(new[] { Make(1), Make(2), Make(3) });
        // 3 newlines, all terminating
        Assert.EndsWith("\n", chunk);
        var lines = chunk.Split('\n');
        // Split on the trailing \n leaves an empty final element — that's expected.
        Assert.Equal(4, lines.Length);
        Assert.Equal("", lines[3]);
        for (int i = 0; i < 3; i++)
        {
            using var doc = JsonDocument.Parse(lines[i]);
            Assert.Equal(i + 1, doc.RootElement.GetProperty("local_id").GetInt64());
        }
    }

    [Fact]
    public void SerializeBatch_empty_yields_empty_string()
    {
        Assert.Equal(string.Empty, FlashAttemptJsonl.SerializeBatch(Array.Empty<UnsyncedFlashAttempt>()));
    }
}
