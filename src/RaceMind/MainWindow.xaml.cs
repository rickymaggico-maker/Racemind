using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using RaceMind.Services;

namespace RaceMind;

public partial class MainWindow : Window
{
    private readonly TelemetryService _telemetry = new();
    private readonly SessionStore _store = new();
    private readonly CancellationTokenSource _cts = new();

    public MainWindow()
    {
        InitializeComponent();
        _telemetry.ConnectionChanged += (_, connected) => Dispatcher.Invoke(() => SetConnected(connected));
        Loaded += async (_, _) =>
        {
            _ = _telemetry.StartAsync(_cts.Token);
            _ = new UpdateService().CheckAndApplyAsync();
        };
        Closed += (_, _) => _cts.Cancel();
    }

    private void SetConnected(bool connected)
    {
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#2DD67B" : "#65757D"));
        StatusText.Text = connected ? "LMU COLLEGATO" : "IN ATTESA";
        ConnectionText.Text = connected ? "Telemetria LMU disponibile" : "LMU non rilevato";
        Subtitle.Text = connected ? "Connessione stabilita. In attesa dello stint." : "In attesa di Le Mans Ultimate.";
    }

    private void OpenSessions_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", _store.Root) { UseShellExecute = true });
    }
}
