using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlashlightApp.Core;

namespace FlashlightApp.Wpf;

public partial class MainWindow : Window
{
    private Catalog? _catalog;
    private string? _catalogPath;
    private string? _catalogDir;
    private string? _gdbExe;
    private string? _port;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        DiscoverGdb();
        DiscoverProbe();
        LoadCatalog();
    }

    private void DiscoverGdb()
    {
        _gdbExe = GdbDiscovery.Find(null);
        StatusGdb.Text = _gdbExe is null
            ? "gdb: НЕ ЗНАЙДЕНО"
            : $"gdb: {Path.GetFileName(_gdbExe)}";
    }

    private void DiscoverProbe()
    {
        var probes = ProbeDiscovery.FindGdbPorts();
        if (probes.Count == 1)
        {
            _port = probes[0].PortName;
            StatusPort.Text = $"Порт: {_port}";
        }
        else if (probes.Count == 0)
        {
            _port = null;
            StatusPort.Text = "Порт: BMP не знайдено";
        }
        else
        {
            _port = null;
            StatusPort.Text = $"Порт: знайдено {probes.Count} BMP (потрібно один)";
        }
    }

    private void LoadCatalog()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "examples", "catalog.json"),
            Path.Combine(AppContext.BaseDirectory, "catalog.json"),
            "examples/catalog.json",
            "catalog.json",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) { _catalogPath = Path.GetFullPath(c); break; }
        }
        if (_catalogPath is null)
        {
            StatusCatalog.Text = "Каталог: не знайдено (потрібен examples/catalog.json)";
            return;
        }
        try
        {
            _catalog = CatalogJson.ParseFile(_catalogPath);
            _catalogDir = Path.GetDirectoryName(_catalogPath);
            var trust = CatalogTrust.VerifyCatalogFile(_catalogPath, requireSigned: false);
            var trustText = trust switch
            {
                CatalogTrustResult.Verified         => "✓ Ed25519",
                CatalogTrustResult.UnsignedAllowed  => "без підпису",
                CatalogTrustResult.BadSignature     => "✗ невірний підпис",
                _                                   => trust.ToString(),
            };
            StatusCatalog.Text = $"Каталог: {_catalog.Products.Count} продукт(ів) · {trustText} · {Path.GetFileName(_catalogPath)}";
            ProductCombo.Items.Clear();
            foreach (var p in _catalog.Products)
                ProductCombo.Items.Add(p.ProductId);
            if (ProductCombo.Items.Count > 0)
                ProductCombo.SelectedIndex = 0;
        }
        catch (CatalogParseException ex)
        {
            StatusCatalog.Text = $"Каталог: помилка — {ex.Message}";
        }
    }

    private void ProductCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_catalog is null || ProductCombo.SelectedItem is not string id)
        {
            VersionLabel.Text = "—";
            return;
        }
        var product = _catalog.FindProduct(id);
        VersionLabel.Text = product?.Default()?.Version is { } v ? v : "—";
    }

    private async void FlashButton_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog is null) { Beep(); return; }
        if (ProductCombo.SelectedItem is not string productId) { Beep(); return; }

        var op = OperatorBox.Text?.Trim() ?? "";
        var batch = BatchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(batch))
        {
            SetBannerNeutral("Заповніть Оператор і Партія", warning: true);
            return;
        }
        if (_port is null || _gdbExe is null)
        {
            SetBannerNeutral("Немає програматора або gdb", warning: true);
            return;
        }

        var product = _catalog.FindProduct(productId);
        var release = product?.Default();
        if (product is null || release is null) { Beep(); return; }

        var elfPath = Path.IsPathRooted(release.ElfFilename)
            ? release.ElfFilename
            : Path.Combine(_catalogDir!, release.ElfFilename);

        if (!File.Exists(elfPath))
        {
            ShowFail("E_ELF_NOT_FOUND", $"ELF не знайдено: {elfPath}");
            LogAttempt(op, batch, product, release, elfPath,
                FlashResult.Fail, "E_ELF_NOT_FOUND", elfPath, 0, null, null);
            return;
        }

        FlashButton.IsEnabled = false;
        GdbOutput.Clear();
        SetBannerNeutral("Виконується…", warning: false);

        try
        {
            var sha = FirmwareIntegrity.ComputeSha256Hex(elfPath);
            if (!FirmwareIntegrity.HashesMatch(sha, release.ElfSha256))
            {
                var msg = $"computed {sha}, expected {release.ElfSha256.ToLowerInvariant()}";
                ShowFail("E_FW_HASH_MISMATCH", msg);
                LogAttempt(op, batch, product, release, elfPath,
                    FlashResult.Fail, "E_FW_HASH_MISMATCH", msg, 0, null, null);
                return;
            }

            var opts = new FlashOptions(
                ElfPath:            elfPath,
                Port:               _port,
                Power:              PowerMode.External,
                BmpFrequencyHz:     1_000_000,
                ConnectUnderReset:  false,
                Product:            product.ProductId,
                Operator:           op,
                Batch:              batch,
                StationId:          Environment.MachineName,
                TargetBmpMatch:     product.Target.BmpMatch,
                TargetFlashKb:      product.Target.FlashKb,
                FirmwareVersion:    release.Version,
                FirmwareSha256:     release.ElfSha256,
                GdbPath:            _gdbExe,
                DbPath:             null);

            var gdb = new GdbProcess(_gdbExe);
            var outcome = await FlashStateMachine.RunAsync(
                gdb,
                opts,
                timeout: TimeSpan.FromSeconds(15),
                onLine: line => Dispatcher.Invoke(() => GdbOutput.AppendText(line.Text + "\n")));

            if (outcome.IsPass)
                ShowPass(outcome.Duration);
            else
                ShowFail(outcome.ErrorCode!, outcome.ErrorMessage ?? "");

            LogAttempt(op, batch, product, release, elfPath,
                outcome.Result, outcome.ErrorCode, outcome.ErrorMessage,
                (long)outcome.Duration.TotalMilliseconds, outcome.GdbTail, outcome.DetectedTarget);
        }
        catch (Exception ex)
        {
            ShowFail("E_INTERNAL", ex.Message);
        }
        finally
        {
            FlashButton.IsEnabled = true;
        }
    }

    private void ShowPass(TimeSpan duration)
    {
        ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
        ResultText.Foreground = Brushes.White;
        ResultDetail.Foreground = Brushes.White;
        ResultText.Text = "✓ ПРОШИВКА УСПІШНА";
        ResultDetail.Text = $"{duration.TotalMilliseconds:F0} мс";
    }

    private void ShowFail(string code, string detail)
    {
        ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        ResultText.Foreground = Brushes.White;
        ResultDetail.Foreground = Brushes.White;
        ResultText.Text = $"✗ {code}";
        ResultDetail.Text = ErrorHints.For(code);
        if (!string.IsNullOrEmpty(detail))
            GdbOutput.AppendText($"\n[Деталі помилки]\n{detail}\n");
    }

    private void SetBannerNeutral(string msg, bool warning)
    {
        ResultBanner.Background = warning
            ? new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E))
            : new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        ResultDetail.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        ResultText.Text = msg;
        ResultDetail.Text = "";
    }

    private void LogAttempt(string op, string batch, Product product, FirmwareRelease release,
        string elfPath, FlashResult result, string? errCode, string? errMsg, long durMs,
        string? gdbTail, string? detected)
    {
        try
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlashlightApp", "flash_log.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var log = new SqliteLogStore(dbPath);
            log.Append(new FlashAttemptRecord(
                TsUtc:            DateTime.UtcNow,
                Operator:         op,
                StationId:        Environment.MachineName,
                BatchId:          batch,
                ProductId:        product.ProductId,
                FirmwareVersion:  release.Version,
                FirmwareSha256:   release.ElfSha256,
                TargetBmpMatch:   product.Target.BmpMatch,
                TargetDetected:   detected,
                TargetFlashKb:    product.Target.FlashKb,
                ComPort:          _port ?? "",
                ProbeSerial:      null,
                Power:            PowerMode.External,
                ConnectRst:       false,
                BmpFrequencyHz:   1_000_000,
                Result:           result,
                ErrorCode:        errCode,
                ErrorMessage:     errMsg,
                DurationMs:       durMs,
                GdbTail:          gdbTail));
        }
        catch
        {
            // logging failures must not crash the UI
        }
    }

    private static void Beep() => System.Media.SystemSounds.Beep.Play();
}
