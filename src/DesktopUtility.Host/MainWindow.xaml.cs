using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DesktopUtility.Host.Capture;
using DesktopUtility.Host.Core;
using DesktopUtility.Host.Video;
using DesktopUtility.Host.Security;
using Microsoft.Win32;

namespace DesktopUtility.Host;

public partial class MainWindow : Window
{
    private const string RegistryAppName = "DesktopUtility";
    private const string RegistryRunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const int MaxLogChars = 24_000;

    private static readonly IEasingFunction ExpoOut = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

    private readonly HostService _host;
    private bool _suppressEvents;
    private bool _switchingMode;

    public MainWindow(HostService host)
    {
        _host = host;
        InitializeComponent();

        VersionText.Text = $"v{HostService.AppVersion}";
        LoadIcon();
        PopulateQualityList();
        LoadStartupState();

        // Backend events fire on worker threads.
        host.Started += () => Dispatcher.InvokeAsync(OnHostStarted);
        host.StartFailed += reason => Dispatcher.InvokeAsync(() => OnHostStartFailed(reason));
        host.PairingChanged += () => Dispatcher.InvokeAsync(RefreshPairing);
        host.SessionsChanged += count => Dispatcher.InvokeAsync(() => RefreshSessions(count));
        host.ModeChanged += _ => Dispatcher.InvokeAsync(RefreshMode);
        host.QualityChanged += _ => Dispatcher.InvokeAsync(RefreshQuality);
        host.DisplaysChanged += () => Dispatcher.InvokeAsync(RefreshDisplays);
        host.Notice += message => Dispatcher.InvokeAsync(() => ModeDescriptionText.Text = message);

        AppLog.Instance.LineWritten += line => Dispatcher.InvokeAsync(() => AppendLog(line));
        foreach (var line in AppLog.Instance.Snapshot()) AppendLog(line);

        RefreshMode();
        RefreshQuality();
        RefreshSessions(host.SessionCount);
        if (host.PairingPin.Length > 0) OnHostStarted();

        Loaded += (_, _) => PlayIntro();
    }

    // ─── Backend → UI ───────────────────────────

    private void OnHostStarted()
    {
        StatusText.Text = Loc.Get("status.ready");
        RefreshPairing();
        RefreshDisplays();
    }

    private void OnHostStartFailed(string reason)
    {
        StatusText.Text = Loc.Get("status.failed");
        StatusDot.Fill = StatusRing.Fill = (Brush)FindResource("DangerBrush");
        ModeDescriptionText.Text = reason;
        DiagnosticsExpander.IsExpanded = true;
    }

    private void RefreshPairing()
    {
        if (_host.PairingPin.Length == 0) return;

        string pin = _host.PairingPin;
        PairingPinText.Text = pin.Length == 6 ? $"{pin[..3]} {pin[3..]}" : pin;

        string url = _host.PairingUrl;
        UrlText.Text = url;
        UrlText.ToolTip = url;
        RenderQr(url);
    }

    private void RenderQr(string url)
    {
        try
        {
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(QrCodeGenerator.GeneratePngBytes(url, 12)))
            {
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
            }
            bmp.Freeze();

            QrCodeImage.Source = bmp;
            QrPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GUI] Could not render QR code: {ex.Message}");
        }
    }

    private void RefreshSessions(int count)
    {
        DevicesCountText.Text = count.ToString();
        DevicesHintText.Text = Loc.Get(count > 0 ? "devices.some" : "devices.none");
    }

    private void RefreshMode()
    {
        bool extended = _host.CurrentMode == DisplayMode.ExtendedDisplay;
        ModeExtendedButton.IsChecked = extended;
        ModeRemoteButton.IsChecked = !extended;
        ModeDescriptionText.Text = Loc.Get(extended ? "mode.extended.desc" : "mode.remote.desc");
        RefreshDisplays();
    }

    private void RefreshQuality()
    {
        _suppressEvents = true;
        foreach (ComboBoxItem item in QualityComboBox.Items)
            if ((string)item.Tag == _host.Quality.ToString()) QualityComboBox.SelectedItem = item;
        _suppressEvents = false;
    }

    private void RefreshDisplays()
    {
        _suppressEvents = true;
        try
        {
            DisplayComboBox.Items.Clear();
            ComboBoxItem? selected = null;

            foreach (var d in _host.GetDisplays())
            {
                var item = new ComboBoxItem
                {
                    Content = $"{Loc.Get(d.IsPrimary ? "display.main" : "display.second")} · {d.Width}×{d.Height}",
                    Tag = d
                };
                DisplayComboBox.Items.Add(item);

                if (_host.ActiveDisplay?.DeviceName == d.DeviceName) selected = item;
            }

            DisplayComboBox.SelectedItem = selected ?? (DisplayComboBox.Items.Count > 0 ? DisplayComboBox.Items[0] : null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GUI] Could not list displays: {ex.Message}");
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    // ─── UI → backend ───────────────────────────

    private void ModeExtended_Click(object sender, RoutedEventArgs e) => SwitchModeAsync(DisplayMode.ExtendedDisplay);
    private void ModeRemote_Click(object sender, RoutedEventArgs e) => SwitchModeAsync(DisplayMode.RemoteControl);

    private async void SwitchModeAsync(DisplayMode mode)
    {
        if (_switchingMode || mode == _host.CurrentMode)
        {
            RefreshMode();
            return;
        }

        _switchingMode = true;
        ModeExtendedButton.IsEnabled = ModeRemoteButton.IsEnabled = false;
        ModeDescriptionText.Text = Loc.Get("mode.switching");

        try
        {
            bool ok = await _host.SetModeAsync(mode);
            if (!ok) ModeDescriptionText.Text = Loc.Get("mode.failed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GUI] Mode switch failed: {ex.Message}");
            ModeDescriptionText.Text = Loc.Get("mode.failed");
        }
        finally
        {
            _switchingMode = false;
            ModeExtendedButton.IsEnabled = ModeRemoteButton.IsEnabled = true;

            // Snap the segmented control back to the real mode (the switch may have failed).
            bool extended = _host.CurrentMode == DisplayMode.ExtendedDisplay;
            ModeExtendedButton.IsChecked = extended;
            ModeRemoteButton.IsChecked = !extended;
        }
    }

    private async void DisplayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || DisplayComboBox.SelectedItem is not ComboBoxItem { Tag: DisplayInfo display }) return;
        if (_host.ActiveDisplay?.DeviceName == display.DeviceName) return;

        try { await _host.SelectDisplayAsync(display); }
        catch (Exception ex) { Console.WriteLine($"[GUI] Could not switch display: {ex.Message}"); }
    }

    private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || QualityComboBox.SelectedItem is not ComboBoxItem { Tag: string preset }) return;

        if (Enum.TryParse<QualityPreset>(preset, out var parsed))
            _host.SetQuality(parsed);
    }

    private void NewCode_Click(object sender, RoutedEventArgs e) => _host.RotatePairingCode();

    private async void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_host.PairingUrl);
            CopyLinkButton.Content = Loc.Get("pair.copied");
            await Task.Delay(1400);
            CopyLinkButton.Content = Loc.Get("pair.copy");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GUI] Clipboard unavailable: {ex.Message}");
        }
    }

    private void PopulateQualityList()
    {
        foreach (var preset in new[] { QualityPreset.Low, QualityPreset.Medium, QualityPreset.High })
        {
            QualityComboBox.Items.Add(new ComboBoxItem
            {
                Content = Loc.Get($"quality.{preset.ToString().ToLowerInvariant()}"),
                Tag = preset.ToString()
            });
        }
    }

    // ─── Autostart ──────────────────────────────

    private void LoadStartupState()
    {
        _suppressEvents = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunPath, false);
            StartupCheckbox.IsChecked = key?.GetValue(RegistryAppName) != null;
        }
        catch { /* registry unavailable: leave unchecked */ }
        finally { _suppressEvents = false; }
    }

    private void StartupCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunPath, true);
            if (key == null) return;

            if (StartupCheckbox.IsChecked == true)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe) || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    // Running through `dotnet run`: there is no stable executable to register.
                    StartupCheckbox.IsChecked = false;
                    Console.WriteLine("[GUI] Autostart is only available for the published DesktopUtility.exe.");
                    return;
                }

                key.SetValue(RegistryAppName, $"\"{exe}\" --minimized");
                Console.WriteLine("[GUI] Added to Windows startup");
            }
            else
            {
                key.DeleteValue(RegistryAppName, false);
                Console.WriteLine("[GUI] Removed from Windows startup");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GUI] Could not change autostart: {ex.Message}");
        }
    }

    // ─── Logs ───────────────────────────────────

    private void AppendLog(string line)
    {
        LogsTextBox.AppendText(line + Environment.NewLine);

        if (LogsTextBox.Text.Length > MaxLogChars)
        {
            LogsTextBox.Text = LogsTextBox.Text[^(MaxLogChars / 2)..];
        }
        LogsTextBox.ScrollToEnd();
    }

    // ─── Window chrome ──────────────────────────

    private void LoadIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/AppIcon.ico"));
        }
        catch { /* window works without an icon */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing only hides the window; the host keeps serving from the system tray.</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting) return;

        e.Cancel = true;
        Hide();
        ((App)Application.Current).NotifyHiddenToTray();
    }

    // ─── Motion ─────────────────────────────────

    private void PlayIntro()
    {
        if (!SystemParameters.ClientAreaAnimation) return; // respect reduced-motion

        // Staggered fade-up of the cards.
        var cards = new FrameworkElement[] { PairCard, HeroCard, ModeCard, StreamCard };
        for (int i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            var shift = new TranslateTransform(0, 24);
            card.RenderTransform = shift;
            card.Opacity = 0;

            var begin = TimeSpan.FromMilliseconds(i * 80);
            var duration = TimeSpan.FromMilliseconds(600);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = ExpoOut });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, duration) { BeginTime = begin, EasingFunction = ExpoOut });
        }

        // Light pools drift slowly; the status ring pulses.
        Float(Blob1Shift, TranslateTransform.YProperty, -26, 9);
        Float(Blob1Shift, TranslateTransform.XProperty, 18, 11);
        Float(Blob2Shift, TranslateTransform.YProperty, 34, 10);
        Float(Blob2Shift, TranslateTransform.XProperty, 22, 12);
        Float(Blob3Shift, TranslateTransform.YProperty, -30, 8);
        Float(Blob3Shift, TranslateTransform.XProperty, -20, 10);

        BottomPulse.BeginAnimation(OpacityProperty, Loop(0.35, 0.9, 5));

        var pulse = Loop(0.5, 1.6, 2.2);
        pulse.EasingFunction = null;
        RingScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        RingScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        StatusRing.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0, TimeSpan.FromSeconds(2.2)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private static void Float(TranslateTransform target, DependencyProperty property, double by, double seconds) =>
        target.BeginAnimation(property, new DoubleAnimation(0, by, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

    private static DoubleAnimation Loop(double from, double to, double seconds) =>
        new(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
}
