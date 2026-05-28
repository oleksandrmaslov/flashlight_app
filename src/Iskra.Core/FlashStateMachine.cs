namespace Iskra.Core;

/// <summary>
/// Two-phase factory-safe driver:
/// <list type="number">
///   <item><description><b>Scan</b> — gdb connects, runs <c>swdp_scan</c>, quits.
///     No <c>attach</c>, no <c>load</c>. If the detected target family doesn't
///     match <c>TargetBmpMatch</c>, we bail out with <c>E_TARGET_MISMATCH</c>
///     before any flash write is attempted.</description></item>
///   <item><description><b>Flash</b> — only reached when scan classified clean.
///     Runs the canonical attach/load/compare-sections sequence.</description></item>
/// </list>
/// Each phase produces one <see cref="GdbRunResult"/>; the per-phase classifier
/// is pure (no IO), making the test suite deterministic.
/// </summary>
public static class FlashStateMachine
{
    public static async Task<FlashOutcome> RunAsync(
        GdbProcess gdb,
        FlashOptions options,
        TimeSpan timeout,
        Action<GdbLine>? onLine = null,
        CancellationToken ct = default)
    {
        // Phase 1: scan only — bail safely before touching flash on a wrong board.
        var scanTimeout = timeout < TimeSpan.FromSeconds(8) ? timeout : TimeSpan.FromSeconds(8);
        var scanRun = await gdb.RunScanAsync(
            options.Port,
            options.Power,
            options.BmpFrequencyHz,
            options.ConnectUnderReset,
            scanTimeout,
            onLine,
            ct).ConfigureAwait(false);

        var scanOutcome = ClassifyScan(scanRun, options.TargetBmpMatch);
        if (scanOutcome is not null)
            return scanOutcome;

        // Phase 2: flash — only reached when scan passed.
        var flashRun = await gdb.RunFlashAsync(
            options.Port,
            options.Power,
            options.BmpFrequencyHz,
            options.ConnectUnderReset,
            options.ElfPath,
            timeout,
            onLine,
            ct).ConfigureAwait(false);

        var outcome = Classify(flashRun, options.TargetBmpMatch);
        // Roll the scan duration into the reported wall-clock so logs reflect
        // true end-to-end time. Tail stays from the flash phase (operators want
        // verify lines), the scan run is captured live via onLine if the caller
        // is logging.
        return outcome with { Duration = scanRun.Duration + outcome.Duration };
    }

    /// <summary>
    /// Pure scan-phase classifier. Returns a non-null FAIL outcome if the scan
    /// detected a fatal condition (timeout, probe error, no targets, family
    /// mismatch); returns <c>null</c> when the scan is clean and the caller
    /// should proceed to flash.
    /// </summary>
    public static FlashOutcome? ClassifyScan(GdbRunResult run, string expectedBmpMatch)
    {
        var tail = run.Tail();

        if (run.TimedOut)
            return Fail("E_TIMEOUT", "gdb scan-phase wall-clock timeout exceeded", null, run.Duration, tail);

        var events = GdbOutputParser.Parse(run.Output);

        var probeFail = ClassifyProbeError(events, run.Duration, tail);
        if (probeFail is not null) return probeFail;

        var targets = events
            .Where(e => e.Kind == GdbEventKind.TargetDetected)
            .Select(e => e.Detail)
            .ToList();
        if (targets.Count == 0)
            return Fail("E_SCAN_NO_TARGET", "swdp_scan returned no targets", null, run.Duration, tail);

        var detected = targets[0];
        if (!string.IsNullOrEmpty(expectedBmpMatch) &&
            !targets.Any(t => t.Contains(expectedBmpMatch, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("E_TARGET_MISMATCH",
                $"expected '{expectedBmpMatch}', detected '{detected}'",
                detected, run.Duration, tail);
        }

        return null;
    }

    /// <summary>
    /// Pure flash-phase classifier: given a captured gdb run and the expected BMP target match string,
    /// return PASS or FAIL with the right E_* code. Order of checks matters — earlier checks
    /// take precedence so we report the *cause*, not a downstream symptom.
    /// </summary>
    public static FlashOutcome Classify(GdbRunResult run, string expectedBmpMatch)
    {
        var tail = run.Tail();

        if (run.TimedOut)
            return Fail("E_TIMEOUT", "gdb wall-clock timeout exceeded", null, run.Duration, tail);

        var events = GdbOutputParser.Parse(run.Output);

        var probeFail = ClassifyProbeError(events, run.Duration, tail);
        if (probeFail is not null) return probeFail;

        var targets = events
            .Where(e => e.Kind == GdbEventKind.TargetDetected)
            .Select(e => e.Detail)
            .ToList();
        string? detected = targets.FirstOrDefault();

        if (targets.Count == 0)
            return Fail("E_SCAN_NO_TARGET", "swdp_scan returned no targets", null, run.Duration, tail);

        if (!string.IsNullOrEmpty(expectedBmpMatch) &&
            !targets.Any(t => t.Contains(expectedBmpMatch, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail("E_TARGET_MISMATCH",
                $"expected '{expectedBmpMatch}', detected '{detected}'",
                detected, run.Duration, tail);
        }

        var attachFail = events.FirstOrDefault(e => e.Kind == GdbEventKind.AttachFailed);
        if (attachFail is not null)
            return Fail("E_ATTACH_FAILED", attachFail.Detail, detected, run.Duration, tail);

        var mismatch = events.FirstOrDefault(e => e.Kind == GdbEventKind.SectionMismatched);
        if (mismatch is not null)
            return Fail("E_VERIFY_MISMATCH",
                $"section {mismatch.Detail} verify failed",
                detected, run.Duration, tail);

        bool loaded  = events.Any(e => e.Kind == GdbEventKind.LoadingSection);
        bool matched = events.Any(e => e.Kind == GdbEventKind.SectionMatched);
        if (!loaded || !matched)
        {
            var why = run.ExitCode != 0
                ? $"gdb exit {run.ExitCode}; load/verify signal absent"
                : "load/verify signal absent in gdb output";
            return Fail("E_LOAD_FAILED", why, detected, run.Duration, tail);
        }

        if (run.ExitCode != 0)
            return Fail("E_GDB_CRASHED", $"gdb exit code {run.ExitCode}", detected, run.Duration, tail);

        return new FlashOutcome(FlashResult.Pass, null, null, detected, run.Duration, tail);
    }

    private static FlashOutcome? ClassifyProbeError(
        IReadOnlyList<GdbEvent> events, TimeSpan duration, string tail)
    {
        var usb = events.FirstOrDefault(e => e.Kind == GdbEventKind.UsbError);
        if (usb is not null)
            return Fail("E_PROBE_NOT_FOUND", usb.Detail, null, duration, tail);

        var busy = events.FirstOrDefault(e => e.Kind == GdbEventKind.ProbeBusy);
        if (busy is not null)
            return Fail("E_PROBE_BUSY", busy.Detail, null, duration, tail);

        var remote = events.FirstOrDefault(e => e.Kind == GdbEventKind.RemoteError);
        if (remote is not null)
            return Fail("E_PROBE_NOT_FOUND", remote.Detail, null, duration, tail);

        return null;
    }

    private static FlashOutcome Fail(string code, string msg, string? detected, TimeSpan dur, string tail)
        => new(FlashResult.Fail, code, msg, detected, dur, tail);
}
