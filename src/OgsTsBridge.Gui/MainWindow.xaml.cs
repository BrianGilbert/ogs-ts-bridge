#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OgsTsBridge.Sources;

namespace OgsTsBridge.Gui
{
/// <summary>
/// Desktop shell over the translator engine. Latency contract: the UI
/// thread only ever reads snapshot state (stats counters, latency ring)
/// at display cadence and issues commands through EngineHost/AutoSupervisor —
/// it never touches the forwarder's receive→send span.
/// </summary>
public partial class MainWindow : Window
{
    const string SponsorsUrl = "https://github.com/sponsors/BrianGilbert";
    const string DiscordUrl = "https://discord.gg/yyCp5D2Ccp";

    readonly EngineHost _host;
    readonly DispatcherTimer _ui;
    System.Windows.Forms.NotifyIcon? _tray;
    bool _hiddenOnce;
    string? _lastErrorShown;

    long _lastIn, _lastOut, _lastRejected;

    public ICommand OpenSponsors { get; }
    public ICommand OpenDiscord { get; }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        OpenSponsors = new RelayCommand(() => OpenUrl(SponsorsUrl));
        OpenDiscord = new RelayCommand(() => OpenUrl(DiscordUrl));

        _host = new EngineHost(new EngineOptions(
            new IPEndPoint(IPAddress.Parse("239.255.17.17"), 40123)));
        _host.StateChanged += () => Dispatcher.BeginInvoke(RefreshAll);
        _host.Start();

        foreach (var src in _host.Sources)
            foreach (var game in src.Games)
                GameList.Items.Add(new GameRow(game, src));
        if (GameList.Items.Count > 0) GameList.SelectedIndex = 0;

        _ui = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _ui.Tick += (_, _) => RefreshStats();
        _ui.Start();

        InitTray();

        RefreshAll();
        // closing hides to the tray: the engine keeps running with a
        // visible tray icon — "is it running?" always has an answer.
        // Exit for real from the tray menu.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
            if (!_hiddenOnce)
            {
                _hiddenOnce = true;
                _tray!.ShowBalloonTip(3000, "OGS-TS Bridge",
                    "Still running — right-click the tray icon to open or exit.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        };
    }

    void InitTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _tray!.Visible = false;
            _host.Dispose();
            Application.Current.Shutdown();
        });
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "OGS-TS Bridge",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    sealed class GameRow
    {
        public GameRow(LegacyGame game, ILegacySource source) { Game = game; Source = source; }
        public LegacyGame Game { get; }
        public ILegacySource Source { get; }
        public override string ToString() => Game.DisplayName;
    }

    void OnGameSelected(object sender, SelectionChangedEventArgs e) => RefreshAll();

    void RefreshAll()
    {
        var active = _host.ActiveSource;
        var label = _host.ActiveProcessLabel;
        if (active is not null)
        {
            StatusDot.Fill = Brushes.Green;
            var gameName = active.Games.FirstOrDefault(
                g => g.ProcessName.Equals(label, StringComparison.OrdinalIgnoreCase))?.DisplayName
                ?? (GameList.SelectedItem as GameRow)?.Game.DisplayName;
            StatusText.Text = $"Forwarding: {gameName ?? active.Title}";
            var input = Forwarders.DescribeInput(active);
            StatusDetail.Text = (_host.ManualActive
                ? $"manually selected · {input} → {_host.Options.Output}"
                : $"detected {gameName ?? label} · {input} → {_host.Options.Output}")
                + (_host.ActiveForwarder?.Stats.Waiting is { } waiting ? $" · {waiting}" : "");
        }
        else if (_host.LastError is { } error)
        {
            StatusDot.Fill = Brushes.OrangeRed;
            StatusText.Text = "Game detected — could not start forwarding";
            StatusDetail.Text = error;
            if (_lastErrorShown != error)
            {
                _lastErrorShown = error;
                _tray?.ShowBalloonTip(5000, "OGS-TS Bridge",
                    "Game detected but the input port is unavailable — open the window for details.",
                    System.Windows.Forms.ToolTipIcon.Error);
            }
        }
        else
        {
            StatusDot.Fill = Brushes.Gray;
            StatusText.Text = _host.AutoWatch ? "Watching for a known game…" : "Idle (auto-detect off)";
            StatusDetail.Text = _host.AutoWatch
                ? "start a game and telemetry will flow automatically, or pick one below"
                : "pick a game below to forward it";
            _lastErrorShown = null;
        }

        StartManual.IsEnabled = active is null && GameList.SelectedItem is GameRow;
        PatchGame.IsEnabled = GameList.SelectedItem is GameRow { Source: IGamePatcher };
        StopSession.IsEnabled = active is not null;

        RefreshStats(resetRates: active is null);
    }

    void RefreshStats(bool resetRates = false)
    {
        var f = _host.ActiveForwarder;
        if (f is null)
        {
            _lastIn = _lastOut = _lastRejected = 0;
            InRate.Text = OutRate.Text = Rejected.Text = "—";
            LP50.Text = LP95.Text = LP99.Text = LMax.Text = "—";
            return;
        }

        var s = f.Stats;
        double inRate = resetRates ? 0 : s.PacketsIn - _lastIn;
        double outRate = resetRates ? 0 : s.PacketsOut - _lastOut;
        long rej = resetRates ? 0 : s.Rejected - _lastRejected;
        _lastIn = s.PacketsIn; _lastOut = s.PacketsOut; _lastRejected = s.Rejected;

        InRate.Text = inRate.ToString("F0");
        OutRate.Text = outRate.ToString("F0");
        Rejected.Text = rej.ToString();

        var l = f.Latency.Take();
        if (double.IsNaN(l.P50))
        {
            LP50.Text = LP95.Text = LP99.Text = LMax.Text = "—";
        }
        else
        {
            LP50.Text = l.P50.ToString("F0");
            LP95.Text = l.P95.ToString("F0");
            LP99.Text = l.P99.ToString("F0");
            LMax.Text = l.Max.ToString("F0");
        }
    }

    void OnPatchGame(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameRow { Source: IGamePatcher patcher } row) return;
        foreach (var name in row.Source.ProcessNames)
        {
            var running = Process.GetProcessesByName(name);
            if (running.Length > 0)
            {
                foreach (var proc in running) proc.Dispose();
                MessageBox.Show(this,
                    $"{name} is running. Close the game first — it rewrites its config on exit — then try again.",
                    "Game is running", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        var files = patcher.PatchGameTelemetry(row.Source.DefaultPort);
        MessageBox.Show(this,
            files.Count == 0
                ? "No config files found. Start the game at least once so they exist, then try again."
                : "Patched:" + Environment.NewLine + string.Join(Environment.NewLine, files) +
                  Environment.NewLine + Environment.NewLine +
                  "A .bak-ogs-ts-bridge backup was saved alongside each file." + Environment.NewLine +
                  $"Start the game — it now sends telemetry to 127.0.0.1:{row.Source.DefaultPort}.",
            "Game telemetry enabled",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OnStartManual(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameRow row) return;
        if (!_host.StartManual(row.Source))
        {
            if (_host.LastError is { } error)
                MessageBox.Show(this, error, "Could not start forwarding",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(this,
                    "A session is already active — stop it first.",
                    "Already forwarding", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        RefreshAll();
    }

    void OnStopSession(object sender, RoutedEventArgs e)
    {
        _host.Stop();
        RefreshAll();
    }

    void OnAutoWatch(object sender, RoutedEventArgs e)
    {
        if (_host is null) return; // XAML IsChecked=True fires during InitializeComponent
        _host.SetAutoWatch(AutoWatchBox.IsChecked == true);
        RefreshAll();
    }

    void OnApplyOutput(object sender, RoutedEventArgs e)
    {
        var text = EndpointBox.Text.Trim();
        var parts = text.Split(':');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) ||
            !int.TryParse(parts[1], out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show(this, "Endpoint must look like 239.255.17.17:40123",
                "Bad endpoint", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(TtlBox.Text.Trim(), out var ttl) || ttl < 0 || ttl > 255) ttl = 1;
        _host.ApplyOutput(new IPEndPoint(ip, port), ttl);
        RefreshAll();
    }

    static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    sealed class RelayCommand : ICommand
    {
        readonly Action _run;
        public RelayCommand(Action run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _run();
    }
}
}
