using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using RaceMind.Models;
using RaceMind.Services;

namespace RaceMind;

public partial class MainWindow : Window
{
    private readonly TelemetryService _telemetry = new();
    private readonly SessionStore _store = new();
    private readonly UpdateService _updates = new();
    private readonly RaceEngineerEngine _raceEngineer = new();
    private readonly CancellationTokenSource _cts = new();
    private string? _sessionId;
    private string? _activeDriverKey;
    private bool _recording;
    private long _samples;
    private int _firstLap;
    private int _lastLap;
    private double _speedSum;
    private double _maxSpeed;
    private double _throttleSum;
    private double _brakeSum;
    private double _fuelStart;
    private double _fuelLast;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = GetDisplayVersion();
        _telemetry.ConnectionChanged += (_, connected) => Dispatcher.Invoke(() => SetConnected(connected));
        _telemetry.SnapshotReceived += OnSnapshot;
        Loaded += (_, _) => { _ = _telemetry.StartAsync(_cts.Token); _ = _updates.CheckAndApplyAsync(); };
        Closed += (_, _) => _cts.Cancel();
    }

    private void OnSnapshot(object? sender, TelemetrySnapshot s)
    {
        if (!s.InPitLane)
        {
            var driverKey = GetDriverKey(s);
            if (!_recording || !string.Equals(_activeDriverKey, driverKey, StringComparison.Ordinal))
                StartStint(s, driverKey);

            _samples++;
            _lastLap = Math.Max(_lastLap, s.Lap);
            _speedSum += Math.Max(0, s.SpeedKph);
            _maxSpeed = Math.Max(_maxSpeed, Math.Max(0, s.SpeedKph));
            _throttleSum += Math.Clamp(s.Throttle, 0, 1);
            _brakeSum += Math.Clamp(s.Brake, 0, 1);
            _fuelLast = s.FuelLitres;
            _raceEngineer.Process(s);

            if (_sessionId is not null)
                _ = _store.AppendAsync(_sessionId, s);

            Dispatcher.Invoke(() =>
            {
                TrackText.Text = BlankIfEmpty(s.Track);
                DriverText.Text = BlankIfEmpty(s.Driver);
                CarText.Text = BlankIfEmpty(s.Vehicle);
                CaptureTitle.Text = "Registrazione attiva";
                CaptureDetail.Text = $"Giro {s.Lap} · {_samples:N0} campioni acquisiti";
                StatusText.Text = "IN PISTA";
                Subtitle.Text = "RaceMind sta registrando e analizzando lo stint in background.";
                DynamicsSummaryText.Text = "Race Engineer attivo · curve, fasi, gomme, degrado e profilo pilota";
                if (IsVisible) Hide();
            });
            return;
        }

        if (_recording)
        {
            _recording = false;
            if (_sessionId is not null)
                _ = _store.AppendAsync(_sessionId, s);

            var sampleCount = Math.Max(1, _samples);
            var avgSpeed = _speedSum / sampleCount;
            var avgThrottle = (_throttleSum / sampleCount) * 100.0;
            var avgBrake = (_brakeSum / sampleCount) * 100.0;
            var fuelUsed = Math.Max(0, _fuelStart - _fuelLast);
            var report = _raceEngineer.Finish();

            Dispatcher.Invoke(() =>
            {
                TrackText.Text = BlankIfEmpty(s.Track);
                DriverText.Text = BlankIfEmpty(s.Driver);
                CarText.Text = BlankIfEmpty(s.Vehicle);
                CaptureTitle.Text = "Stint analizzato";
                var laps = Math.Max(1, _lastLap - _firstLap + 1);
                CaptureDetail.Text = $"{laps} giri · {_samples:N0} campioni reali elaborati";
                MaxSpeedText.Text = $"{_maxSpeed:0} km/h";
                AvgSpeedText.Text = $"{avgSpeed:0} km/h";
                FuelUsedText.Text = $"{fuelUsed:0.00} L";
                PedalUsageText.Text = $"Gas {avgThrottle:0}% · Freno {avgBrake:0}%";
                DynamicsSummaryText.Text = FormatRaceEngineerReport(report);
                StatusText.Text = s.InGarage ? "GARAGE" : "BOX";
                Subtitle.Text = report.Headline;
                ConnectionText.Text = "Telemetria LMU collegata";
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });

            _sessionId = null;
            _activeDriverKey = null;
        }
        else
        {
            Dispatcher.Invoke(() =>
            {
                TrackText.Text = BlankIfEmpty(s.Track);
                DriverText.Text = BlankIfEmpty(s.Driver);
                CarText.Text = BlankIfEmpty(s.Vehicle);
                StatusText.Text = s.InGarage ? "GARAGE" : "BOX";
                Subtitle.Text = "RaceMind è pronto per il prossimo stint.";
                if (!IsVisible) Show();
            });
        }
    }

    private void StartStint(TelemetrySnapshot s, string driverKey)
    {
        _recording = true;
        _activeDriverKey = driverKey;
        _sessionId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{SafeFilePart(s.Driver)}";
        _samples = 0;
        _firstLap = s.Lap;
        _lastLap = s.Lap;
        _speedSum = 0;
        _maxSpeed = 0;
        _throttleSum = 0;
        _brakeSum = 0;
        _fuelStart = s.FuelLitres;
        _fuelLast = s.FuelLitres;
        _raceEngineer.Reset(s);
    }

    private static string FormatRaceEngineerReport(RaceEngineerReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Headline.ToUpperInvariant());
        sb.AppendLine($"Ingresso: {PhaseText(r.Entry)} · Centro: {PhaseText(r.Mid)} · Uscita: {PhaseText(r.Exit)}");
        sb.AppendLine($"Curve analizzate {r.CornerEvents} · consistenza {r.Laps.ConsistencyPercent:0}% · best {(r.Laps.BestLapSeconds > 0 ? FormatLap(r.Laps.BestLapSeconds) : "—")}");
        sb.AppendLine($"Gomme kPa FL {r.Tires.FrontLeftPressureKpa:0} FR {r.Tires.FrontRightPressureKpa:0} · RL {r.Tires.RearLeftPressureKpa:0} RR {r.Tires.RearRightPressureKpa:0}");
        sb.AppendLine($"Picchi °C FL {r.Tires.FrontLeftPeakTempC:0} FR {r.Tires.FrontRightPeakTempC:0} · RL {r.Tires.RearLeftPeakTempC:0} RR {r.Tires.RearRightPeakTempC:0}");
        sb.AppendLine($"Degrado: {r.Degradation.HandlingTrend} · passo {r.Degradation.PaceChangeSeconds:+0.00;-0.00;0.00}s");
        sb.AppendLine($"Pilota: {r.DriverComparison.PaceComparison} · {r.DriverComparison.BalanceComparison}");
        sb.AppendLine(r.EvidenceSummary);

        if (r.Recommendations.Count > 0)
        {
            var top = r.Recommendations[0];
            sb.Append($"SETUP → {top.Change} · confidenza {top.ConfidencePercent}%");
        }
        else
        {
            sb.Append("SETUP → nessuna modifica proposta: evidenza insufficiente o bilanciamento non dominante.");
        }

        return sb.ToString();
    }

    private static string PhaseText(PhaseHandlingSummary p)
    {
        var label = p.State switch
        {
            HandlingState.Understeer => "sottosterzo",
            HandlingState.Oversteer => "sovrasterzo",
            HandlingState.Neutral => "neutro",
            HandlingState.Mixed => "misto",
            _ => "insufficiente"
        };
        return p.ConfidencePercent > 0 ? $"{label} {p.ConfidencePercent}%" : label;
    }

    private static string FormatLap(double seconds)
    {
        var minutes = (int)(seconds / 60);
        var remainder = seconds - minutes * 60;
        return $"{minutes}:{remainder:00.000}";
    }

    private static string GetDriverKey(TelemetrySnapshot s)
    {
        if (s.DriverSteamId != 0)
            return $"steam:{s.DriverSteamId}";
        return "name:" + (s.Driver ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string SafeFilePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "driver";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Replace(' ', '_');
    }

    private static string GetDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        var version = assembly.GetName().Version;
        if (version is null) return "—";
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static string BlankIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void SetConnected(bool connected)
    {
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#2DD67B" : "#65757D"));
        if (!connected)
        {
            StatusText.Text = "IN ATTESA";
            ConnectionText.Text = "LMU non rilevato";
            Subtitle.Text = "In attesa di Le Mans Ultimate.";
            CaptureTitle.Text = "Non attiva";
            CaptureDetail.Text = "Si attiverà automaticamente durante lo stint.";
            DynamicsSummaryText.Text = "In attesa della telemetria dinamica LMU.";
            DriverText.Text = "—";
            if (!IsVisible) Show();
        }
        else if (!_recording)
        {
            StatusText.Text = "LMU COLLEGATO";
            ConnectionText.Text = "Telemetria LMU disponibile";
            Subtitle.Text = "Connessione stabilita. In attesa dello stint.";
            DynamicsSummaryText.Text = "Race Engineer pronto · analisi live al prossimo stint";
        }
    }

    private void OpenSessions_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", _store.Root) { UseShellExecute = true });

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Controllo aggiornamenti…";
        var result = await _updates.CheckManualAsync();
        switch (result)
        {
            case UpdateCheckResult.UpToDate:
                UpdateStatusText.Text = $"RaceMind è aggiornato · {DateTime.Now:HH:mm}";
                MessageBox.Show("Stai utilizzando l'ultima versione di RaceMind.", "RaceMind", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case UpdateCheckResult.UpdateAvailable:
                UpdateStatusText.Text = "Aggiornamento scaricato. Riavvio…";
                break;
            default:
                UpdateStatusText.Text = "Errore aggiornamento · dettagli registrati";
                var detail = string.IsNullOrWhiteSpace(_updates.LastError) ? "Errore non specificato." : _updates.LastError;
                MessageBox.Show(
                    $"RaceMind non è riuscito a controllare gli aggiornamenti.\n\nDettaglio: {detail}\n\nLog: {_updates.LogPath}",
                    "RaceMind · Aggiornamenti",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                break;
        }
        CheckUpdatesButton.IsEnabled = true;
    }
}
