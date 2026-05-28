using System.Text;
using System.Text.Json;

namespace Iskra.Core;

/// <summary>
/// Sprint 5 cloud mirror wire format. One flash attempt = one JSON object on
/// one line. The shape is fixed and explicit (every column always present,
/// nulls written as <c>null</c> rather than omitted) so the downstream
/// <c>rebuild-logs-db</c> Action can ingest a moving stream of rows into
/// SQLite without per-row schema sniffing.
///
/// <para>
/// Field names match the SQLite column names exactly (snake_case). The wire
/// format is versioned via <see cref="SchemaVersion"/>; bump the constant and
/// the ingest script's expected version together when columns change.
/// </para>
/// </summary>
public static class FlashAttemptJsonl
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Serialize a single unsynced row to one JSONL line. No trailing newline —
    /// callers (typically <see cref="SerializeBatch"/>) decide line terminators.
    /// </summary>
    public static string Serialize(UnsyncedFlashAttempt row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = false }))
        {
            WriteRow(w, row);
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    /// <summary>
    /// Serialize a sequence of rows to a JSONL chunk: one object per line,
    /// each line terminated by <c>\n</c> (including the final one). Appending
    /// this directly after an existing JSONL file is safe — the previous file
    /// already ended in <c>\n</c> by this same contract.
    /// </summary>
    public static string SerializeBatch(IEnumerable<UnsyncedFlashAttempt> rows)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(Serialize(r));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void WriteRow(Utf8JsonWriter w, UnsyncedFlashAttempt row)
    {
        var r = row.Record;
        w.WriteStartObject();
        w.WriteNumber("schema_version",   SchemaVersion);
        w.WriteNumber("local_id",         row.Id);
        w.WriteString("ts_utc",           r.TsUtc.ToUniversalTime().ToString("o"));
        w.WriteString("operator",         r.Operator);
        w.WriteString("station_id",       r.StationId);
        w.WriteString("batch_id",         r.BatchId);
        w.WriteString("product_id",       r.ProductId);
        w.WriteString("firmware_version", r.FirmwareVersion);
        w.WriteString("firmware_sha256",  r.FirmwareSha256);
        w.WriteString("target_bmp_match", r.TargetBmpMatch);
        WriteStringOrNull(w, "target_detected", r.TargetDetected);
        w.WriteNumber("target_flash_kb",  r.TargetFlashKb);
        w.WriteString("com_port",         r.ComPort);
        WriteStringOrNull(w, "probe_serial", r.ProbeSerial);
        w.WriteString("power_mode",       r.Power.ToString().ToLowerInvariant());
        w.WriteBoolean("connect_rst",     r.ConnectRst);
        w.WriteNumber("bmp_frequency_hz", r.BmpFrequencyHz);
        w.WriteString("result",           r.Result == FlashResult.Pass ? "PASS" : "FAIL");
        WriteStringOrNull(w, "error_code",    r.ErrorCode);
        WriteStringOrNull(w, "error_message", r.ErrorMessage);
        w.WriteNumber("duration_ms",      r.DurationMs);
        WriteStringOrNull(w, "gdb_tail",      r.GdbTail);
        w.WriteEndObject();
    }

    private static void WriteStringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else               w.WriteString(name, value);
    }
}
