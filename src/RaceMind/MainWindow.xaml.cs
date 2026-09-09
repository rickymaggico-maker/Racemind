using System.Diagnostics;
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
    private readonly CancellationTokenSource _cts = new();
    private string? _sessionId;
    private bool _recording;
    private long _samples;
    private int _firstLap;
    private int _lastLap;

    public MainWindow()
    {
        InitializeComponent();
        _telemetry.ConnectionChanged += (_, connected) => Dispatcher.Invoke(() => SetConnected(connected));
        _telemetry.SnapshotReceived += OnSnapshot;
        Loaded += (_, _) => { _ = _telemetry.StartAsync(_cts.Token); _ = _updates.CheckAndApplyAsync(); };
        Closed += (_, _) => _cts.Cancel();
    }

    private void OnSnapshot(object? sender, TelemetrySnapshot s)
    {
        if (!s.InPitLane)
        {
            if (!_recording) { _recording = true; _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss"); _samples = 0; _firstLap = s.Lap; _lastLap = s.Lap; }
            _samples++; _lastLap = Math.Max(_lastLap, s.Lap);
            if (_sessionId is not null) _ = _store.AppendAsync(_sessionId, s);
            Dispatcher.Invoke(() => { TrackText.Text = BlankIfEmpty(s.Track); DriverText.Text = BlankIfEmpty(s.Driver); CarText.Text = BlankIfEmpty(s.Vehicle); CaptureTitle.Text = "Registrazione attiva"; CaptureDetail.Text = $"Giro {s.Lap} · {_samples:N0} campioni acquisiti"; StatusText.Text = "IN PISTA"; Subtitle.Text = "RaceMind sta registrando lo stint in background."; if (IsVisible) Hide(); });
            return;
        }
        if (_recording)
        {
            _recording = false; if (_sessionId is not null) _ = _store.AppendAsync(_sessionId, s);
            Dispatcher.Invoke(() => { TrackText.Text = BlankIfEmpty(s.Track); DriverText.Text = BlankIfEmpty(s.Driver); CarText.Text = BlankIfEmpty(s.Vehicle); CaptureTitle.Text = "Stint salvato"; var laps = Math.Max(1, _lastLap - _firstLap + 1); CaptureDetail.Text = $"{laps} giri · {_samples:N0} campioni reali salvati"; StatusText.Text = s.InGarage ? "GARAGE" : "BOX"; Subtitle.Text = "Stint acquisito. I dati sono pronti per l'analisi."; ConnectionText.Text = "Telemetria LMU collegata"; Show(); WindowState = WindowState.Normal; Activate(); });
        }
        else Dispatcher.Invoke(() => { TrackText.Text = BlankIfEmpty(s.Track); DriverText.Text = BlankIfEmpty(s.Driver); CarText.Text = BlankIfEmpty(s.Vehicle); StatusText.Text = s.InGarage ? "GARAGE" : "BOX"; Subtitle.Text = "RaceMind è pronto per il prossimo stint."; if (!IsVisible) Show(); });
    }

    private static string BlankIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void SetConnected(bool connected)
    {
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#2DD67B" : "#65757D"));
        if (!connected) { StatusText.Text = "IN ATTESA"; ConnectionText.Text = "LMU non rilevato"; Subtitle.Text = "In attesa di Le Mans Ultimate."; CaptureTitle.Text = "Non attiva"; CaptureDetail.Text = "Si attiverà automaticamente durante lo stint."; DriverText.Text = "—"; if (!IsVisible) Show(); }
        else if (!_recording) { StatusText.Text = "LMU COLLEGATO"; ConnectionText.Text = "Telemetria LMU disponibile"; Subtitle.Text = "Connessione stabilita. In attesa dello stint."; }
    }

    private void OpenSessions_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", _store.Root) { UseShellExecute = true });

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
                UpdateStatusText.Text = "Impossibile verificare gli aggiornamenti";
                MessageBox.Show("Non è stato possibile controllare gli aggiornamenti. Verifica la connessione Internet e riprova.", "RaceMind", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
        CheckUpdatesButton.IsEnabled = true;
    }
}
