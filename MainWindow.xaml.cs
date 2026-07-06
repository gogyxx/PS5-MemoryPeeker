using Microsoft.Win32;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace PS5MemoryPeeker;

public partial class MainWindow : Window
{
    private static readonly TimeSpan MemoryMapRefreshTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MemoryMapCacheDuration = TimeSpan.FromSeconds(20);
    private const int LibdebugPort = 744;
    private readonly PeekerState _state = new();
    private readonly ObservableCollection<ConnectionHistoryItem> _history = [];
    private readonly List<MediaPlayer> _startupSoundPlayers = [];
    private readonly IConsoleDebugClient _client = new LibdebugConsoleClient();
    private readonly DispatcherTimer _liveCheatTimer;
    private ScanEngine _scanEngine;
    private CancellationTokenSource? _operationCts;
    private DateTime _scanStartedAt;
    private bool _statusIsSuccess;
    private bool _diagnosticsGood;
    private bool _isOledTheme;
    private bool _operationInProgress;
    private bool _suppressSectionTotals;
    private int _memoryRefreshVersion;
    private int? _prefetchPid;
    private int? _memoryMapCachePid;
    private DateTime _memoryMapCacheAt;
    private Task<IReadOnlyList<MemorySection>>? _memoryMapPrefetch;
    private IReadOnlyList<MemorySection>? _memoryMapCache;
    private ProcessItem? _selectedProcess;
    private string _connectionState = "Idle";
    private bool _ebootProbeInProgress;
    private DateTime _nextEbootProbeAt;
    private double _displayedProgress;
    private DateTime _lastProgressUiUpdate = DateTime.MinValue;
    private bool _cheatWriteInProgress;

    private readonly record struct ConnectionProbe(
        bool PayloadReady,
        string PayloadPath,
        bool PayloadPortValid,
        int PayloadPort,
        bool PayloadPortOpen,
        bool DebugPortOpen);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _state;
        _scanEngine = new ScanEngine(_client);
        _state.Sections.CollectionChanged += Sections_CollectionChanged;

        _liveCheatTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _liveCheatTimer.Tick += LiveCheatTimer_Tick;
        _liveCheatTimer.Start();
        foreach (ConnectionHistoryItem item in ConnectionHistoryService.Load())
        {
            _history.Add(item);
        }

        ApplyTheme();
        InstallInputFilters();
        Loaded += MainWindow_Loaded;
    }

    private void InstallInputFilters()
    {
        IpBox.PreviewTextInput += IpBox_PreviewTextInput;
        PortBox.PreviewTextInput += PortBox_PreviewTextInput;
        DataObject.AddPastingHandler(IpBox, IpBox_Pasting);
        DataObject.AddPastingHandler(PortBox, PortBox_Pasting);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        StartStartupUnfoldAnimation();
    }

    private static bool ContainsOnlyIpCharacters(string text)
    {
        return text.All(c => char.IsDigit(c) || c == '.');
    }

    private static bool ContainsOnlyDigits(string text)
    {
        return text.All(char.IsDigit);
    }

    private void IpBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !ContainsOnlyIpCharacters(e.Text);
    }

    private void PortBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !ContainsOnlyDigits(e.Text);
    }

    private void IpBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        PasteFilteredText(IpBox, e, c => char.IsDigit(c) || c == '.');
    }

    private void PortBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        PasteFilteredText(PortBox, e, char.IsDigit);
    }

    private static void PasteFilteredText(TextBox textBox, DataObjectPastingEventArgs e, Func<char, bool> isAllowed)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text) ||
            e.DataObject.GetData(DataFormats.Text) is not string text)
        {
            e.CancelCommand();
            return;
        }

        string filtered = new(text.Where(isAllowed).ToArray());
        e.CancelCommand();
        if (filtered.Length == 0)
        {
            return;
        }

        int start = textBox.SelectionStart;
        textBox.SelectedText = filtered;
        textBox.SelectionStart = start + filtered.Length;
        textBox.SelectionLength = 0;
    }

    private void StartStartupUnfoldAnimation()
    {
        StartupFoldOverlay.Visibility = Visibility.Visible;
        StartupFoldOverlay.IsHitTestVisible = true;
        StartupFoldOverlay.Focus();
        ResetStartupFoldState();

        TimeSpan step = TimeSpan.FromMilliseconds(330);
        TimeSpan duration = TimeSpan.FromMilliseconds(560);
        IEasingFunction easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        Storyboard storyboard = new();
        AddPaperRevealAnimation(storyboard, FoldTopLeft, "RenderTransform.Children[0].ScaleX", "RenderTransform.Children[0].ScaleY", "RenderTransform.Children[1].AngleX", 0, 8, TimeSpan.Zero, duration, easing);
        AddPaperRevealAnimation(storyboard, FoldTopRight, "RenderTransform.Children[0].ScaleX", null, "RenderTransform.Children[1].AngleX", 0, -20, step, duration, easing);
        AddPaperRevealAnimation(storyboard, FoldBottomLeft, null, "RenderTransform.Children[0].ScaleY", "RenderTransform.Children[1].AngleY", 0, -15, step + step, duration, easing);
        AddPaperRevealAnimation(storyboard, FoldBottomRight, "RenderTransform.Children[0].ScaleX", null, "RenderTransform.Children[1].AngleX", 0, -18, step + step + step, duration, easing);
        _ = PlayStartupPaperSoundsAsync(step);

        DoubleAnimation overlayFade = new(1, 0, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            BeginTime = step + step + step + duration - TimeSpan.FromMilliseconds(40),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(overlayFade, StartupFoldOverlay);
        Storyboard.SetTargetProperty(overlayFade, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(overlayFade);

        storyboard.Completed += (_, _) =>
        {
            StartupFoldOverlay.Visibility = Visibility.Collapsed;
            StartupFoldOverlay.IsHitTestVisible = false;
            StartupFoldOverlay.Opacity = 1;
        };

        storyboard.Begin(this, true);
    }

    private void ResetStartupFoldState()
    {
        StartupFoldOverlay.Opacity = 1;
        SetFoldTransform(FoldTopLeft, scaleX: 1, scaleY: 1, skewX: 0, skewY: 0, opacity: 0.98);
        SetFoldTransform(FoldTopRight, scaleX: 1, scaleY: 1, skewX: 0, skewY: 0, opacity: 0.98);
        SetFoldTransform(FoldBottomLeft, scaleX: 1, scaleY: 1, skewX: 0, skewY: 0, opacity: 0.98);
        SetFoldTransform(FoldBottomRight, scaleX: 1, scaleY: 1, skewX: 0, skewY: 0, opacity: 0.98);
    }

    private static void SetFoldTransform(
        FrameworkElement fold,
        double scaleX,
        double scaleY,
        double skewX,
        double skewY,
        double opacity)
    {
        fold.Opacity = opacity;
        if (fold.RenderTransform is not TransformGroup group ||
            group.Children.Count < 2 ||
            group.Children[0] is not ScaleTransform scale ||
            group.Children[1] is not SkewTransform skew)
        {
            return;
        }

        scale.ScaleX = scaleX;
        scale.ScaleY = scaleY;
        skew.AngleX = skewX;
        skew.AngleY = skewY;
    }

    private static void AddPaperRevealAnimation(
        Storyboard storyboard,
        FrameworkElement fold,
        string? scaleXPath,
        string? scaleYPath,
        string skewPath,
        double skewFrom,
        double skewTo,
        TimeSpan beginTime,
        TimeSpan duration,
        IEasingFunction easing)
    {
        if (scaleXPath is not null)
        {
            DoubleAnimation scaleX = new(1, 0.02, new Duration(duration))
            {
                BeginTime = beginTime,
                EasingFunction = easing
            };
            Storyboard.SetTarget(scaleX, fold);
            Storyboard.SetTargetProperty(scaleX, new PropertyPath(scaleXPath));
            storyboard.Children.Add(scaleX);
        }

        if (scaleYPath is not null)
        {
            DoubleAnimation scaleY = new(1, 0.02, new Duration(duration))
            {
                BeginTime = beginTime,
                EasingFunction = easing
            };
            Storyboard.SetTarget(scaleY, fold);
            Storyboard.SetTargetProperty(scaleY, new PropertyPath(scaleYPath));
            storyboard.Children.Add(scaleY);
        }

        DoubleAnimation skew = new(skewFrom, skewTo, new Duration(TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.72)))
        {
            BeginTime = beginTime,
            AutoReverse = true,
            EasingFunction = easing
        };
        DoubleAnimation fade = new(0.98, 0, new Duration(TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.62)))
        {
            BeginTime = beginTime + TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.38),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        Storyboard.SetTarget(skew, fold);
        Storyboard.SetTarget(fade, fold);
        Storyboard.SetTargetProperty(skew, new PropertyPath(skewPath));
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

        storyboard.Children.Add(skew);
        storyboard.Children.Add(fade);
    }

    private async Task PlayStartupPaperSoundsAsync(TimeSpan step)
    {
        _startupSoundPlayers.Clear();
        for (int i = 0; i < 4; i++)
        {
            PlayStartupFoldSound();
            await Task.Delay(step);
        }

        await Task.Delay(900);
        foreach (MediaPlayer player in _startupSoundPlayers)
        {
            player.Close();
        }

        _startupSoundPlayers.Clear();
    }

    private void PlayStartupFoldSound()
    {
        try
        {
            MediaPlayer player = new();
            player.Open(new Uri("pack://siteoforigin:,,,/Assets/Sounds/Animation_Sound.mp3", UriKind.Absolute));
            player.Volume = 0.48;
            _startupSoundPlayers.Add(player);
            player.Play();
        }
        catch
        {
            // Startup sound is polish only; never block the app if audio is unavailable.
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await ConnectCurrentInputAsync();
    }

    private async Task ConnectCurrentInputAsync()
    {
        string host = IpBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            SetStatus("Enter your PS5 IP address first.");
            IpBox.Focus();
            return;
        }

        ClearMemoryMapCache();
        SetConnectionState("Connecting");
        await RunOperationAsync("Connecting...", async token =>
        {
            try
            {
                ConnectionProbe probe = await ProbeConnectionAsync(host, token);
                if (probe.DebugPortOpen)
                {
                    SetDiagnostics(BuildDiagnostics(probe, "ready"), true);
                    SetStatus("PS5Debug is already running. Connecting...", true);
                }
                else
                {
                    if (!probe.PayloadPortValid)
                    {
                        throw new InvalidOperationException("Enter a valid payload port before connecting.");
                    }

                    if (!probe.PayloadReady)
                    {
                        throw new FileNotFoundException("PS5Debug.bin was not found next to the app. Place PS5Debug.bin in the app folder, then connect again.");
                    }

                    if (!probe.PayloadPortOpen)
                    {
                        throw new TimeoutException($"Payload port {probe.PayloadPort} did not answer. Check the PS5 IP, payload-loader port, and that the PS5 is awake.");
                    }

                    SetDiagnostics(BuildDiagnostics(probe, "sending"), true);
                    await TrySendDefaultPayloadAsync(host, probe.PayloadPort, probe.PayloadPath, token);
                    await WaitForDebugPortAsync(host, token);
                }

                await _client.ConnectAsync(host, token);
                bool processListAnswered = await RefreshProcessesAsync(token);
                SetConnectionState(processListAnswered ? "Connected" : "Idle");
                SetDiagnostics($"Payload: ready | {LibdebugPort}: connected | EBOOT: {(processListAnswered ? "✅" : "❌")}", processListAnswered);
                SaveConnectionHistory(host, PortBox.Text.Trim());
            }
            catch
            {
                SetConnectionState("Idle");
                throw;
            }
        });
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await KillConnectionAsync();
    }

    private async Task KillConnectionAsync()
    {
        if (_connectionState != "Connected" && !_client.IsConnected)
        {
            ResetRuntimeState("Idle", "Idle.");
            return;
        }

        await RunOperationAsync("Killing connection...", async _ =>
        {
            await _client.DisconnectAsync();
            ResetRuntimeState("Disconnected", "Connection killed. App reset.", true);
        });
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        ContextMenu menu = new();
        if (_history.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No saved connections", IsEnabled = false });
        }
        else
        {
            foreach (ConnectionHistoryItem item in _history)
            {
                MenuItem menuItem = new() { Header = item.Display, Tag = item };
                menuItem.Click += async (_, _) =>
                {
                    IpBox.Text = item.Host;
                    PortBox.Text = item.Port;
                    await ConnectCurrentInputAsync();
                };
                menu.Items.Add(menuItem);
            }
        }

        menu.Items.Add(new Separator());
        MenuItem killConnectionItem = new() { Header = "Kill Connection", Foreground = ThemeBrush(_isOledTheme ? "#FF5A52" : "#B42318") };
        killConnectionItem.Click += async (_, _) => await KillConnectionAsync();
        menu.Items.Add(killConnectionItem);
        MenuItem clearHistoryItem = new() { Header = "Clear History" };
        clearHistoryItem.Click += (_, _) =>
        {
            _history.Clear();
            ConnectionHistoryService.Save(_history);
            SetStatus("Connection history cleared.", true);
        };
        menu.Items.Add(clearHistoryItem);

        HistoryButton.ContextMenu = menu;
        menu.PlacementTarget = HistoryButton;
        menu.IsOpen = true;
    }

    private async void FirstScan_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProcess(out ProcessItem? process))
        {
            return;
        }

        ProcessItem selectedProcess = process!;
        IReadOnlyList<MemorySection> sections = GetSelectedSections();
        if (sections.Count == 0)
        {
            await RunOperationAsync("Refreshing process memory...", async token =>
            {
                await ReloadMemoryMapAsync(selectedProcess, token);
            });
            sections = GetSelectedSections();
        }

        if (sections.Count == 0)
        {
            SetStatus("No process memory selected. Click Refresh, or enable All in Process Memory.");
            return;
        }

        ScanCompareKind selectedCompareKind = MemoryValueCodec.CompareFromDisplayName((string)CompareTypeBox.SelectedItem);
        if (!ValidateScanValueInput(selectedCompareKind, isFirstScan: true))
        {
            return;
        }

        await RunOperationAsync("Process Memory | Peeking...", async token =>
        {
            bool resumeLiveCheats = PauseLiveCheatsForOperation();
            _scanStartedAt = DateTime.UtcNow;
            try
            {
                bool processPaused = false;
                if (AutoPauseBox.IsChecked == true)
                {
                    await _client.PauseProcessAsync(selectedProcess.Pid, token);
                    processPaused = true;
                }

                try
                {
                    MemoryValueKind valueKind = MemoryValueCodec.FromDisplayName((string)ValueTypeBox.SelectedItem);
                    ScanCompareKind compareKind = MemoryValueCodec.CompareFromDisplayName((string)CompareTypeBox.SelectedItem);
                    Progress<(double Progress, string Message)> progress = new(p => SetProgress(p.Progress, p.Message));
                    IReadOnlyList<ScanResultRow> rows = await _scanEngine.FirstScanAsync(
                        selectedProcess.Pid,
                        sections,
                        valueKind,
                        compareKind,
                        ValueBox.Text,
                        SecondValueBox.Text,
                        AlignmentBox.IsChecked == true,
                        progress,
                        token);

                    ReplaceResults(rows);

                    CompareTypeBox.ItemsSource = MemoryValueCodec.NextScanTypes;
                    CompareTypeBox.SelectedIndex = 0;
                    UpdateSecondValueVisibility();
                    ScanTimeText.Text = (DateTime.UtcNow - _scanStartedAt).ToString(@"mm\:ss\.fff");
                    SetStatus($"First scan completed. {rows.Count:N0} matches.", true);
                }
                finally
                {
                    if (processPaused)
                    {
                        await _client.ResumeProcessAsync(CancellationToken.None);
                    }
                }
            }
            finally
            {
                RestoreLiveCheatsAfterOperation(resumeLiveCheats);
            }
        });
    }

    private async void NextScan_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProcess(out ProcessItem? process))
        {
            return;
        }

        ProcessItem selectedProcess = process!;
        if (!_scanEngine.HasPreviousScan)
        {
            SetStatus("Run First Scan before Next Scan.");
            return;
        }

        ScanCompareKind selectedCompareKind = MemoryValueCodec.CompareFromDisplayName((string)CompareTypeBox.SelectedItem);
        if (!ValidateScanValueInput(selectedCompareKind, isFirstScan: false))
        {
            return;
        }

        await RunOperationAsync("Process Memory | Analyzing...", async token =>
        {
            bool resumeLiveCheats = PauseLiveCheatsForOperation();
            _scanStartedAt = DateTime.UtcNow;
            try
            {
                MemoryValueKind valueKind = MemoryValueCodec.FromDisplayName((string)ValueTypeBox.SelectedItem);
                ScanCompareKind compareKind = MemoryValueCodec.CompareFromDisplayName((string)CompareTypeBox.SelectedItem);
                Progress<(double Progress, string Message)> progress = new(p => SetProgress(p.Progress, p.Message));
                IReadOnlyList<ScanResultRow> rows = await _scanEngine.NextScanAsync(
                    selectedProcess.Pid,
                    valueKind,
                    compareKind,
                    ValueBox.Text,
                    SecondValueBox.Text,
                    progress,
                    token);

                ReplaceResults(rows);

                ScanTimeText.Text = (DateTime.UtcNow - _scanStartedAt).ToString(@"mm\:ss\.fff");
                SetStatus($"Next scan completed. {rows.Count:N0} matches.", true);
            }
            finally
            {
                RestoreLiveCheatsAfterOperation(resumeLiveCheats);
            }
        });
    }

    private bool ValidateScanValueInput(ScanCompareKind compareKind, bool isFirstScan)
    {
        bool needsNoValue = compareKind is ScanCompareKind.UnknownInitialValue or
            ScanCompareKind.ChangedValue or
            ScanCompareKind.UnchangedValue or
            ScanCompareKind.FuzzyValue;

        if (needsNoValue)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            SetStatus(isFirstScan
                ? "Please input value first and then hit First Scan."
                : "Please input value first and then hit Next Scan.");
            ValueBox.Focus();
            return false;
        }

        if (compareKind == ScanCompareKind.BetweenValue && string.IsNullOrWhiteSpace(SecondValueBox.Text))
        {
            SetStatus("Please input both values first and then scan.");
            SecondValueBox.Focus();
            return false;
        }

        return true;
    }

    private async void RefreshResults_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProcess(out ProcessItem? process))
        {
            return;
        }

        ProcessItem selectedProcess = process!;
        await RunOperationAsync("Refreshing visible results...", async token =>
        {
            foreach (ScanResultRow row in _state.Results.Take(5000))
            {
                byte[] bytes = await _client.ReadMemoryAsync(selectedProcess.Pid, row.Address, row.Bytes.Length, token);
                row.Bytes = bytes;
                row.Value = MemoryValueCodec.ToDisplay(row.Type, bytes);
                row.Hex = MemoryValueCodec.ToHex(bytes);
            }

            ResultsGrid.Items.Refresh();
            SetStatus("Visible results refreshed.", true);
        });
    }

    private void ReplaceResults(IReadOnlyList<ScanResultRow> rows)
    {
        using (ResultsGrid.Items.DeferRefresh())
        {
            _state.Results.ReplaceAll(rows);
        }
    }

    private async void RefreshMemory_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProcess(out ProcessItem? process))
        {
            await RunOperationAsync("Refreshing EBOOT process...", async token =>
            {
                await RefreshProcessesAsync(token);
            });
            return;
        }

        ProcessItem selectedProcess = process!;
        await RunOperationAsync("Refreshing process memory...", async token =>
        {
            await ReloadMemoryMapAsync(selectedProcess, token);
        });
    }

    private void ResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is ScanResultRow row)
        {
            AddCheatFromResult(row);
        }
    }

    private void NewCheat_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is ScanResultRow row)
        {
            AddCheatFromResult(row);
            return;
        }

        ManualAddressWindow dialog = new() { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            CheatRow cheat = dialog.ToCheatRow();
            TryAttachSectionInfo(cheat);
            _state.Cheats.Add(cheat);
            SetStatus("Address added.", true);
        }
    }

    private async void RefreshCheats_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProcess(out ProcessItem? process))
        {
            return;
        }

        CheatsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CheatsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        ProcessItem selectedProcess = process!;
        await RunOperationAsync("Refreshing cheat list...", async token =>
        {
            int written = 0;
            int read = 0;
            foreach (CheatRow cheat in _state.Cheats.Where(c => c.IsActive))
            {
                TryAttachSectionInfo(cheat);
                if (cheat.IsLocked)
                {
                    await WriteCheatAsync(selectedProcess.Pid, cheat, token);
                    written++;
                    continue;
                }

                await ReadCheatAsync(selectedProcess.Pid, cheat, token);
                read++;
            }

            CheatsGrid.Items.Refresh();
            SetStatus($"Cheat list refreshed. {read:N0} read, {written:N0} locked value(s) written.", true);
        });
    }

    private async void SaveCheats_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Save cheat table",
            FileName = "ps5-cheats.pmpt",
            Filter = "PS5-MemoryPeeker table (*.pmpt)|*.pmpt|JSON (*.json)|*.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunOperationAsync("Saving cheat table...", async token =>
        {
            await CheatFileService.SaveAsync(dialog.FileName, _state.Cheats, token);
            SetStatus("Cheat table saved.", true);
        });
    }

    private async void LoadCheats_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Load cheat table",
            Filter = "PS5-MemoryPeeker table (*.pmpt)|*.pmpt|JSON (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunOperationAsync("Loading cheat table...", async token =>
        {
            IReadOnlyList<CheatRow> cheats = await CheatFileService.LoadAsync(dialog.FileName, token);
            _state.Cheats.Clear();
            foreach (CheatRow cheat in cheats)
            {
                TryAttachSectionInfo(cheat);
                _state.Cheats.Add(cheat);
            }

            SetStatus($"Loaded {cheats.Count:N0} cheats.", true);
        });
    }

    private async void ExportCheats_Click(object sender, RoutedEventArgs e)
    {
        CheatsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CheatsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        if (_state.Cheats.Count == 0)
        {
            SetStatus("No cheats to export.");
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "Export cheats",
            FileName = "ps5-memory-cheats.json",
            Filter = "JSON cheat (*.json)|*.json|SHN XML cheat (*.shn)|*.shn|MC4 encrypted cheat (*.mc4)|*.mc4"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunOperationAsync("Exporting cheats...", async token =>
        {
            await CheatExportService.ExportAsync(dialog.FileName, _state.Cheats, _selectedProcess, token);
            int offsetCount = _state.Cheats.Count(c => c.IsActive && c.SectionStart > 0);
            SetStatus($"Exported {_state.Cheats.Count(c => c.IsActive):N0} active cheat(s). {offsetCount:N0} include section offsets; no pointer chains generated.", true);
        });
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetProcess(out ProcessItem? process))
        {
            return;
        }

        ProcessItem selectedProcess = process!;
        await RunOperationAsync("Pausing process...", async token =>
        {
            await _client.PauseProcessAsync(selectedProcess.Pid, token);
            SetStatus("Process paused.", true);
        });
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("Resuming process...", async token =>
        {
            await _client.ResumeProcessAsync(token);
            SetStatus("Process resumed.", true);
        });
    }

    private async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionState != "Connected" && !_client.IsConnected && _selectedProcess is null)
        {
            ResetRuntimeState("Idle", "Idle.");
            return;
        }

        await RunOperationAsync("Killing process...", async token =>
        {
            bool killed = false;
            try
            {
                if (_client.IsConnected)
                {
                    await _client.KillProcessAsync(token);
                    killed = true;
                }
            }
            finally
            {
                await _client.DisconnectAsync();
                ResetRuntimeState("Disconnected", killed ? "Process killed. App reset." : "Kill ended. App reset.", killed);
            }
        });
    }

    private void SelectAllBox_Click(object sender, RoutedEventArgs e)
    {
        bool selected = SelectAllBox.IsChecked == true;
        _suppressSectionTotals = true;
        foreach (MemorySection section in _state.Sections)
        {
            section.IsSelected = selected;
        }
        _suppressSectionTotals = false;

        SectionsList.Items.Refresh();
        UpdateSectionTotals(updateStatus: true);
    }

    private void SectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSectionTotals(updateStatus: true);
    }

    private void CompareTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSecondValueVisibility();
    }

    private async void LiveCheatTimer_Tick(object? sender, EventArgs e)
    {
        if (!TryGetProcess(out ProcessItem? process, quiet: true))
        {
            await TryAutoHookEbootAsync();
            return;
        }

        ProcessItem selectedProcess = process!;
        foreach (CheatRow cheat in _state.Cheats.Where(c => c.IsActive && c.IsLocked).ToList())
        {
            try
            {
                await WriteCheatAsync(selectedProcess.Pid, cheat, CancellationToken.None);
            }
            catch (Exception ex)
            {
                SetStatus($"Live cheat write failed: {ex.Message}");
                return;
            }
        }
    }

    private async Task TryAutoHookEbootAsync()
    {
        if (_operationInProgress ||
            _ebootProbeInProgress ||
            !_client.IsConnected ||
            _connectionState != "Connected" ||
            _selectedProcess is not null ||
            DateTime.UtcNow < _nextEbootProbeAt)
        {
            return;
        }

        _ebootProbeInProgress = true;
        _nextEbootProbeAt = DateTime.UtcNow.AddSeconds(3);
        try
        {
            using CancellationTokenSource probeCts = new(TimeSpan.FromSeconds(3));
            IReadOnlyList<ProcessItem> processes = await _client.GetProcessesAsync(probeCts.Token);
            _state.Processes.Clear();
            foreach (ProcessItem process in processes)
            {
                _state.Processes.Add(process);
            }

            if (_state.Processes.Count == 0)
            {
                SetEbootDisplay(false);
                return;
            }

            _selectedProcess = _state.Processes[0];
            SetEbootDisplay(true);
            StartMemoryMapPrefetch(_selectedProcess);
            SetDiagnostics($"Payload: ready | {LibdebugPort}: connected | EBOOT: ✅", true);
            SetStatus("EBOOT HOOKED ✅ automatically. Click Refresh to load process memory.", true);
        }
        catch
        {
            _nextEbootProbeAt = DateTime.UtcNow.AddSeconds(5);
        }
        finally
        {
            _ebootProbeInProgress = false;
        }
    }

    private async void CheatsGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        CheatsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CheatsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        if (CheatsGrid.SelectedItem is CheatRow cheat)
        {
            await WriteEditedCheatAsync(cheat);
        }
    }

    private async void CheatsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit ||
            e.Row.Item is not CheatRow cheat ||
            e.Column.Header?.ToString() != "Value")
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(async () =>
        {
            CheatsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            CheatsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            await WriteEditedCheatAsync(cheat);
        }, DispatcherPriority.Background);
    }

    private void CheatsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || CheatsGrid.CurrentColumn?.Header?.ToString() != "Value")
        {
            return;
        }

        CheatsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CheatsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (CheatsGrid.CurrentItem is CheatRow cheat)
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(async () => await WriteEditedCheatAsync(cheat), DispatcherPriority.Background);
        }
    }

    private async Task WriteEditedCheatAsync(CheatRow cheat)
    {
        if (_cheatWriteInProgress || cheat is not { IsActive: true })
        {
            return;
        }

        if (!TryGetProcess(out ProcessItem? process, quiet: true))
        {
            return;
        }

        _cheatWriteInProgress = true;
        try
        {
            TryAttachSectionInfo(cheat);
            await WriteCheatAsync(process!.Pid, cheat, CancellationToken.None);
            SetStatus(cheat.IsLocked ? "Locked value written." : "Value written.", true);
        }
        catch (Exception ex)
        {
            SetStatus($"Value write failed: {ex.Message}");
        }
        finally
        {
            _cheatWriteInProgress = false;
        }
    }

    private void Sections_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressSectionTotals)
        {
            return;
        }

        UpdateSectionTotals();
    }

    private async Task<bool> RefreshProcessesAsync(CancellationToken token)
    {
        IReadOnlyList<ProcessItem> processes;
        try
        {
            processes = await _client.GetProcessesAsync(token).WaitAsync(TimeSpan.FromSeconds(8), token);
        }
        catch (Exception ex)
        {
            await _client.DisconnectAsync();
            _state.Processes.Clear();
            ClearMemoryMapCache();
            _selectedProcess = null;
            SetEbootDisplay(false);
            SetStatus(ToUserFacingLibdebugError(ex));
            return false;
        }

        _state.Processes.Clear();
        foreach (ProcessItem process in processes)
        {
            _state.Processes.Add(process);
        }

        if (_state.Processes.Count > 0)
        {
            _selectedProcess = _state.Processes[0];
            SetEbootDisplay(true);
            StartMemoryMapPrefetch(_selectedProcess);
            SetStatus("Connected. EBOOT HOOKED ✅; click Refresh to load process memory.", true);
            return true;
        }

        _selectedProcess = null;
        SetEbootDisplay(false);
        SetStatus("Connected to PS5Debug. EBOOT WAITING ❌. Start a game, then connect/refresh again.");
        return true;
    }

    private async Task ReloadMemoryMapAsync(ProcessItem process, CancellationToken token)
    {
        int refreshVersion = ++_memoryRefreshVersion;
        IReadOnlyList<MemorySection>? sections = await TryFetchMemorySectionsAsync(process, refreshVersion, token);
        if (sections is null || refreshVersion != _memoryRefreshVersion)
        {
            return;
        }

        _suppressSectionTotals = true;
        _state.Sections.Clear();
        foreach (MemorySection section in ApplySectionFilter(sections))
        {
            if (SelectAllBox.IsChecked == true)
            {
                section.IsSelected = true;
            }

            _state.Sections.Add(section);
        }
        _suppressSectionTotals = false;

        SectionsList.Items.Refresh();
        _scanEngine.Clear();
        _state.Results.Clear();
        UpdateSectionTotals();

        if (_state.Sections.Count == 0)
        {
            SetStatus("No readable process memory returned for EBOOT. Keep the game open, then restart PS5Debug and Refresh.");
            return;
        }

        SetStatus(BuildSectionStatus(), true);
    }

    private async Task<IReadOnlyList<MemorySection>?> TryFetchMemorySectionsAsync(ProcessItem process, int refreshVersion, CancellationToken token)
    {
        if (_memoryMapCachePid == process.Pid &&
            _memoryMapCache is not null &&
            DateTime.UtcNow - _memoryMapCacheAt <= MemoryMapCacheDuration)
        {
            return CloneSections(_memoryMapCache);
        }

        Task<IReadOnlyList<MemorySection>> loadTask = GetOrStartMemoryMapLoad(process);
        _ = loadTask.ContinueWith(task =>
        {
            _ = task.Exception;
        }, TaskContinuationOptions.OnlyOnFaulted);

        Task timeoutTask = Task.Delay(MemoryMapRefreshTimeout, token);
        Task completed = await Task.WhenAny(loadTask, timeoutTask);

        if (completed != loadTask)
        {
            if (refreshVersion == _memoryRefreshVersion)
            {
                _client.AbortActiveConnection();
                SetConnectionState("Disconnected");
                SetProgress(0, "0%");
                SetStatus($"EBOOT refresh timeout after {MemoryMapRefreshTimeout.TotalSeconds:0}s. The debug payload did not return process maps. Re-send payload, wait one second, then Connect again.");
            }

            return null;
        }

        try
        {
            IReadOnlyList<MemorySection> loaded = await loadTask;
            StoreMemoryMapCache(process.Pid, loaded);
            return CloneSections(loaded);
        }
        catch (Exception ex)
        {
            if (refreshVersion == _memoryRefreshVersion)
            {
                string detail = ToUserFacingLibdebugError(ex);
                SetProgress(0, "0%");
                SetStatus($"EBOOT refresh failed. {detail}");
            }

            return null;
        }
    }

    private void StartMemoryMapPrefetch(ProcessItem process)
    {
        _prefetchPid = process.Pid;
        _memoryMapPrefetch = _client.GetMemoryMapAsync(process.Pid, CancellationToken.None);
        _ = _memoryMapPrefetch.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion)
            {
                Dispatcher.Invoke(() =>
                {
                    if (_prefetchPid == process.Pid)
                    {
                        StoreMemoryMapCache(process.Pid, task.Result);
                    }
                });
            }
            else
            {
                _ = task.Exception;
            }
        }, CancellationToken.None);
    }

    private Task<IReadOnlyList<MemorySection>> GetOrStartMemoryMapLoad(ProcessItem process)
    {
        if (_prefetchPid == process.Pid && _memoryMapPrefetch is not null && !_memoryMapPrefetch.IsCompleted)
        {
            return _memoryMapPrefetch;
        }

        _prefetchPid = process.Pid;
        _memoryMapPrefetch = _client.GetMemoryMapAsync(process.Pid, CancellationToken.None);
        return _memoryMapPrefetch;
    }

    private void StoreMemoryMapCache(int pid, IReadOnlyList<MemorySection> sections)
    {
        _memoryMapCachePid = pid;
        _memoryMapCacheAt = DateTime.UtcNow;
        _memoryMapCache = CloneSections(sections);
    }

    private void ClearMemoryMapCache()
    {
        _prefetchPid = null;
        _memoryMapPrefetch = null;
        _memoryMapCachePid = null;
        _memoryMapCache = null;
        _memoryMapCacheAt = DateTime.MinValue;
    }

    private void ResetRuntimeState(string connectionState, string status, bool success = false)
    {
        _operationCts?.Cancel();
        _memoryRefreshVersion++;
        _selectedProcess = null;
        _state.Processes.Clear();
        _state.Sections.Clear();
        _state.Results.Clear();
        _state.Cheats.Clear();
        _scanEngine.Clear();
        ClearMemoryMapCache();

        SetEbootDisplay(false);
        SetConnectionState(connectionState);
        SetDiagnostics("");
        SetProgress(0, "0%");
        ScanTimeText.Text = "-";

        ValueBox.Text = "";
        SecondValueBox.Text = "";
        ValueTypeBox.SelectedIndex = 2;
        CompareTypeBox.ItemsSource = MemoryValueCodec.FirstScanTypes;
        CompareTypeBox.SelectedIndex = 0;
        UpdateSecondValueVisibility();

        AlignmentBox.IsChecked = true;
        AutoPauseBox.IsChecked = false;
        SelectAllBox.IsChecked = false;
        FilterBox.IsChecked = true;
        FilterTextBox.Text = "anon, exe, Game, dlmalloc, Sce";
        SectionsList.Items.Refresh();
        CheatsGrid.Items.Refresh();
        UpdateSectionTotals();
        SetStatus(status, success);
    }

    private static IReadOnlyList<MemorySection> CloneSections(IEnumerable<MemorySection> sections)
    {
        return sections.Select(section => new MemorySection
        {
            IsSelected = section.IsSelected,
            Name = section.Name,
            Index = section.Index,
            Kind = section.Kind,
            SelectionScore = section.SelectionScore,
            Start = section.Start,
            End = section.End,
            Protection = section.Protection
        }).ToList();
    }

    private IReadOnlyList<MemorySection> ApplySectionFilter(IEnumerable<MemorySection> sections)
    {
        if (FilterBox.IsChecked != true)
        {
            return sections.ToList();
        }

        string[] tokens = FilterTextBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return sections.ToList();
        }

        List<MemorySection> all = sections.ToList();
        List<MemorySection> filtered = all
            .Where(section => tokens.Any(token => section.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return filtered.Count > 0 ? filtered : all;
    }

    private IReadOnlyList<MemorySection> GetSelectedSections()
    {
        return _state.Sections
            .Where(s => s.IsSelected)
            .OrderByDescending(s => s.SelectionScore)
            .ThenBy(s => s.ByteLength)
            .ToList();
    }

    private bool TryGetProcess(out ProcessItem? process, bool quiet = false)
    {
        process = _selectedProcess;
        if (process is null && !quiet)
        {
            SetStatus("No EBOOT process is selected. Start a game, then Connect/Refresh.");
        }

        return process is not null;
    }

    private static bool TryParsePort(string text, out int port)
    {
        return int.TryParse(text.Trim(), out port) && port is > 0 and <= 65535;
    }

    private static string ToUserFacingLibdebugError(Exception ex)
    {
        string message = ex.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = ex.GetType().Name;
        }

        if (message.Contains("libdbg status", StringComparison.OrdinalIgnoreCase))
        {
            return "PS5Debug answered, but libdebug rejected the protocol/status. Check that the port is the PS5Debug memory port for this payload and that the game is already running.";
        }

        if (message.Contains("did not answer within", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("did not answer after payload/connect retries", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Verbindungsversuch ist fehlgeschlagen", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Host nicht reagiert", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("failed to respond", StringComparison.OrdinalIgnoreCase))
        {
            return "PS5Debug did not answer after sending the payload. Check the payload port, payload compatibility, and wait until the PS5 exploit host reports that PS5Debug is running.";
        }

        if (message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("abgelehnt", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection refused. Check the PS5 IP, port, and that PS5Debug is running.";
        }

        if (message.Contains("Remotehost geschlossen", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection reset", StringComparison.OrdinalIgnoreCase))
        {
            return "The payload port closed the connection. If PS5Debug is already running, the app will connect directly through port 744.";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "Connection timed out. Check the PS5 IP, port, and network.";
        }

        return message;
    }

    private void AddCheatFromResult(ScanResultRow row)
    {
        CheatRow cheat = new()
        {
            Address = row.Address,
            SectionStart = row.SectionStart,
            Type = row.Type,
            Value = row.Value,
            OriginalHex = row.Hex,
            Section = row.Section,
            Description = row.Section
        };
        TryAttachSectionInfo(cheat);
        _state.Cheats.Add(cheat);
        SetStatus("Result added to cheat list.", true);
    }

    private bool TryAttachSectionInfo(CheatRow cheat)
    {
        if (cheat.SectionStart > 0)
        {
            return true;
        }

        MemorySection? section = _state.Sections
            .Where(candidate => cheat.Address >= candidate.Start && cheat.Address < candidate.End)
            .OrderBy(candidate => candidate.ByteLength)
            .FirstOrDefault();

        if (section is null)
        {
            return false;
        }

        cheat.SectionStart = section.Start;
        if (string.IsNullOrWhiteSpace(cheat.Section))
        {
            cheat.Section = section.Name;
        }

        return true;
    }

    private async Task ReadCheatAsync(int pid, CheatRow cheat, CancellationToken token)
    {
        int length = MemoryValueCodec.GetSize(cheat.Type, cheat.Value);
        byte[] bytes = await _client.ReadMemoryAsync(pid, cheat.Address, length, token);
        cheat.Value = MemoryValueCodec.ToDisplay(cheat.Type, bytes);
    }

    private async Task WriteCheatAsync(int pid, CheatRow cheat, CancellationToken token)
    {
        byte[] bytes = MemoryValueCodec.ToBytes(cheat.Type, cheat.Value);
        await _client.WriteMemoryAsync(pid, cheat.Address, bytes, token);
    }

    private bool PauseLiveCheatsForOperation()
    {
        bool shouldResume = _liveCheatTimer.IsEnabled;
        if (shouldResume)
        {
            _liveCheatTimer.Stop();
        }

        return shouldResume;
    }

    private void RestoreLiveCheatsAfterOperation(bool shouldResume)
    {
        if (shouldResume)
        {
            _liveCheatTimer.Start();
        }
    }

    private async Task TrySendDefaultPayloadAsync(string host, string portText, CancellationToken token)
    {
        if (!TryParsePort(portText, out int port))
        {
            return;
        }

        string payloadPath = ResolvePayloadPath();
        if (string.IsNullOrWhiteSpace(payloadPath))
        {
            SetStatus("No PS5Debug.bin payload found next to the app. Trying to connect to an already running PS5Debug session.");
            return;
        }

        await TrySendDefaultPayloadAsync(host, port, payloadPath, token);
    }

    private async Task TrySendDefaultPayloadAsync(string host, int port, string payloadPath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
        {
            throw new FileNotFoundException("PS5Debug.bin was not found next to the app.");
        }

        using CancellationTokenSource payloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        payloadTimeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await PayloadSender.SendAsync(host, port, payloadPath, payloadTimeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("PS5Debug.bin payload port did not answer within 4s. Check PS5 power, IP address, network, and port.");
        }

        SetStatus("PS5Debug.bin sent. Waiting for PS5Debug to start...", true);
        await Task.Delay(3500, token);
    }

    private async Task WaitForDebugPortAsync(string host, CancellationToken token)
    {
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            if (await IsTcpPortOpenAsync(host, LibdebugPort, TimeSpan.FromSeconds(1), token))
            {
            SetDiagnostics($"Payload: ready | {LibdebugPort}: open | EBOOT: ❌", true);
                SetStatus("PS5Debug port is open. Connecting...", true);
                return;
            }

            SetDiagnostics($"Payload: sent | {LibdebugPort}: waiting {attempt}/10");
            await Task.Delay(1000, token);
        }

        SetStatus("Payload was sent, but PS5Debug port 744 did not open yet. Trying libdebug anyway.");
    }

    private async Task<ConnectionProbe> ProbeConnectionAsync(string host, CancellationToken token)
    {
        bool payloadPortValid = TryParsePort(PortBox.Text, out int payloadPort);
        string payloadPath = ResolvePayloadPath();
        bool payloadReady = !string.IsNullOrWhiteSpace(payloadPath) && File.Exists(payloadPath);

        SetDiagnostics("Payload: checking | Debug: checking");
        bool debugPortOpen = await IsTcpPortOpenAsync(host, LibdebugPort, TimeSpan.FromMilliseconds(900), token);
        bool payloadPortOpen = false;
        if (!debugPortOpen && payloadPortValid)
        {
            payloadPortOpen = await IsTcpPortOpenAsync(host, payloadPort, TimeSpan.FromMilliseconds(900), token);
        }

        ConnectionProbe probe = new(payloadReady, payloadPath, payloadPortValid, payloadPort, payloadPortOpen, debugPortOpen);
        SetDiagnostics(BuildDiagnostics(probe), debugPortOpen || payloadPortOpen);
        return probe;
    }

    private static string BuildDiagnostics(ConnectionProbe probe, string mode = "")
    {
        string payload = probe.PayloadReady ? "ready" : "missing";
        string payloadPort = probe.PayloadPortValid
            ? $"{probe.PayloadPort}: {(probe.PayloadPortOpen ? "open" : "closed")}"
            : "payload port: invalid";
        string debug = $"{LibdebugPort}: {(probe.DebugPortOpen ? "open" : "closed")}";
        return string.IsNullOrWhiteSpace(mode)
            ? $"Payload: {payload} | {payloadPort} | {debug}"
            : $"Payload: {payload} | {payloadPort} | {debug} | {mode}";
    }

    private static async Task<bool> IsTcpPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken token)
    {
        using TcpClient client = new();
        try
        {
            Task connectTask = client.ConnectAsync(host, port, token).AsTask();
            Task completed = await Task.WhenAny(connectTask, Task.Delay(timeout, token));
            if (completed != connectTask)
            {
                return false;
            }

            await connectTask;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolvePayloadPath()
    {
        string ps5PayloadPath = Path.Combine(AppContext.BaseDirectory, "PS5Debug.bin");
        return File.Exists(ps5PayloadPath) ? ps5PayloadPath : "";
    }

    private void SaveConnectionHistory(string host, string port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        ConnectionHistoryItem item = new() { Host = host.Trim(), Port = port.Trim() };
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Host.Equals(item.Host, StringComparison.OrdinalIgnoreCase) &&
                _history[i].Port.Equals(item.Port, StringComparison.OrdinalIgnoreCase))
            {
                _history.RemoveAt(i);
            }
        }

        _history.Insert(0, item);
        while (_history.Count > 12)
        {
            _history.RemoveAt(_history.Count - 1);
        }

        ConnectionHistoryService.Save(_history);
    }

    private async Task RunOperationAsync(string startingStatus, Func<CancellationToken, Task> operation)
    {
        if (_operationInProgress)
        {
            SetStatus("Flow guard: operation already running. Please wait.");
            return;
        }

        _operationInProgress = true;
        _operationCts = new CancellationTokenSource();
        SetStatus(startingStatus);
        SetProgress(0, startingStatus);
        Stopwatch stopwatch = Stopwatch.StartNew();
        Cursor = System.Windows.Input.Cursors.Wait;

        try
        {
            await operation(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus(ToUserFacingLibdebugError(ex));
        }
        finally
        {
            stopwatch.Stop();
            Cursor = null;
            _operationInProgress = false;
            if (ScanTimeText.Text == "-")
            {
                ScanTimeText.Text = stopwatch.Elapsed.ToString(@"mm\:ss\.fff");
            }
        }
    }

    private void SetProgress(double progress, string message)
    {
        double percent = Math.Clamp(progress * 100, 0, 100);
        AnimateProgress(percent);
        ProgressText.Text = $"{percent:0}%";

        DateTime now = DateTime.UtcNow;
        if (percent <= 0 || percent >= 100 || now - _lastProgressUiUpdate >= TimeSpan.FromMilliseconds(120))
        {
            StatusText.Text = message;
            _lastProgressUiUpdate = now;
        }

        _statusIsSuccess = false;
        UpdateStatusForeground();
    }

    private void AnimateProgress(double targetPercent)
    {
        if (targetPercent <= 0 || targetPercent >= 100)
        {
            ScanProgress.BeginAnimation(RangeBase.ValueProperty, null);
            ScanProgress.Value = targetPercent;
            _displayedProgress = targetPercent;
            return;
        }

        double from = double.IsNaN(ScanProgress.Value) ? _displayedProgress : ScanProgress.Value;
        if (Math.Abs(targetPercent - from) < 0.15)
        {
            return;
        }

        DoubleAnimation animation = new()
        {
            From = from,
            To = targetPercent,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) => _displayedProgress = targetPercent;
        ScanProgress.BeginAnimation(RangeBase.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetStatus(string message, bool success = false)
    {
        StatusText.Text = message;
        _statusIsSuccess = success;
        UpdateStatusForeground();
    }

    private void SetDiagnostics(string message, bool good = false)
    {
        if (ConnectionDiagnosticsText is null)
        {
            return;
        }

        _diagnosticsGood = good;
        ConnectionDiagnosticsText.Text = message;
        ConnectionDiagnosticsText.Foreground = good
            ? ThemeBrush(_isOledTheme ? "#00E676" : "#128A4C")
            : ThemeBrush(_isOledTheme ? "#9AA7B4" : "#68737D");
    }

    private void SetConnectionState(string state)
    {
        if (ConnectionBadge is null)
        {
            return;
        }

        _connectionState = state;
        ConnectionBadge.Text = state;
        UpdateConnectionBadge();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemePopup.IsOpen = !ThemePopup.IsOpen;
    }

    private void ThemeLight_Click(object sender, RoutedEventArgs e)
    {
        SetTheme(false);
    }

    private void ThemeDark_Click(object sender, RoutedEventArgs e)
    {
        SetTheme(true);
    }

    private void SetTheme(bool oledTheme)
    {
        _isOledTheme = oledTheme;
        ThemePopup.IsOpen = false;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (RootGrid is null ||
            HeaderBorder is null ||
            ToolbarBorder is null ||
            MemoryPanel is null ||
            StatusBorder is null)
        {
            return;
        }

        Brush background = ThemeBrush(_isOledTheme ? "#000000" : "#F5F6F7");
        Brush surface = ThemeBrush(_isOledTheme ? "#000000" : "#FFFFFF");
        Brush control = ThemeBrush(_isOledTheme ? "#05070A" : "#FFFFFF");
        Brush foreground = ThemeBrush(_isOledTheme ? "#F8FAFC" : "#151A1E");
        Brush muted = ThemeBrush(_isOledTheme ? "#9AA7B4" : "#68737D");
        Brush line = ThemeBrush(_isOledTheme ? "#1B2430" : "#DDE2E7");
        Brush gridLine = ThemeBrush(_isOledTheme ? "#121923" : "#EEF1F4");
        Brush accent = ThemeBrush(_isOledTheme ? "#0A84FF" : "#1677FF");
        Brush danger = ThemeBrush(_isOledTheme ? "#FF5A52" : "#C2413D");

        Resources["ButtonHoverBrush"] = ThemeBrush(_isOledTheme ? "#0B121A" : "#F2F6FA");
        Resources["ButtonPressedBrush"] = ThemeBrush(_isOledTheme ? "#111B27" : "#E8EEF5");
        Resources["PrimaryButtonHoverBrush"] = ThemeBrush(_isOledTheme ? "#006BD6" : "#075FD8");
        Resources["PrimaryButtonPressedBrush"] = ThemeBrush(_isOledTheme ? "#0057B8" : "#064FB4");

        Background = background;
        RootGrid.Background = background;
        HeaderBorder.Background = surface;
        HeaderBorder.BorderBrush = line;
        ToolbarBorder.Background = surface;
        ToolbarBorder.BorderBrush = line;
        ProcessDisplayBox.Background = control;
        ProcessDisplayBox.BorderBrush = line;
        ProcessDisplayText.Foreground = foreground;
        ThemePopupBorder.Background = control;
        ThemePopupBorder.BorderBrush = line;
        ApplyFoldPaperTheme();
        FoldTopLeft.BorderBrush = line;
        FoldTopRight.BorderBrush = line;
        FoldBottomLeft.BorderBrush = line;
        FoldBottomRight.BorderBrush = line;
        MemoryPanel.Background = surface;
        MemoryPanel.BorderBrush = line;
        StatusBorder.Background = surface;
        StatusBorder.BorderBrush = line;

        ApplyThemeToChildren(RootGrid, foreground, muted, control, surface, line, gridLine, accent, danger);
        UpdateConnectionBadge();
        UpdateThemeToggle();
        UpdateThemePopupButtons(foreground, control, accent, line);
        SetDiagnostics(ConnectionDiagnosticsText.Text, _diagnosticsGood);
        UpdateStatusForeground();
    }

    private void ApplyFoldPaperTheme()
    {
        FoldTopLeft.Background = BuildPaperBrush(reverse: false);
        FoldTopRight.Background = BuildPaperBrush(reverse: true);
        FoldBottomLeft.Background = BuildPaperBrush(reverse: true);
        FoldBottomRight.Background = BuildPaperBrush(reverse: false);
    }

    private Brush BuildPaperBrush(bool reverse)
    {
        string bright = _isOledTheme ? "#101820" : "#FFFFFF";
        string mid = _isOledTheme ? "#080D13" : "#EEF3F8";
        string soft = _isOledTheme ? "#05070A" : "#F8FAFC";

        LinearGradientBrush brush = new()
        {
            StartPoint = reverse ? new Point(1, 0) : new Point(0, 0),
            EndPoint = reverse ? new Point(0, 1) : new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bright), 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(mid), 0.48));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(soft), 1));
        return brush;
    }

    private void ApplyThemeToChildren(
        DependencyObject parent,
        Brush foreground,
        Brush muted,
        Brush control,
        Brush surface,
        Brush line,
        Brush gridLine,
        Brush accent,
        Brush danger)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);

            switch (child)
            {
                case TextBlock textBlock:
                    if (textBlock == ConnectionBadge ||
                        textBlock == StatusText ||
                        textBlock == ConnectionDiagnosticsText)
                    {
                        break;
                    }

                    textBlock.Foreground = IsInsidePrimaryButton(textBlock) ? Brushes.White : foreground;
                    break;

                case CheckBox checkBox:
                    checkBox.Foreground = foreground;
                    ApplySystemBrushes(checkBox, foreground, control, accent);
                    break;

                case TextBox textBox:
                    textBox.Background = control;
                    textBox.Foreground = foreground;
                    textBox.BorderBrush = line;
                    textBox.CaretBrush = foreground;
                    ApplySystemBrushes(textBox, foreground, control, accent);
                    break;

                case ComboBox comboBox:
                    comboBox.Background = control;
                    comboBox.Foreground = foreground;
                    comboBox.BorderBrush = line;
                    comboBox.ItemContainerStyle = BuildComboBoxItemStyle(control, foreground, accent);
                    ApplySystemBrushes(comboBox, foreground, control, accent);
                    break;

                case ComboBoxItem comboBoxItem:
                    comboBoxItem.Background = control;
                    comboBoxItem.Foreground = foreground;
                    ApplySystemBrushes(comboBoxItem, foreground, control, accent);
                    break;

                case Button button:
                    bool primary = button.Content is string buttonText && (buttonText == "Connect" || buttonText == "First Scan" || buttonText == "Next Scan" || buttonText == "Export");
                    bool kill = button.Content is string killText && killText == "Kill";
                    button.Background = primary ? accent : control;
                    button.BorderBrush = kill ? danger : primary ? accent : line;
                    button.Foreground = kill ? danger : primary ? Brushes.White : foreground;
                    break;

                case DataGrid dataGrid:
                    dataGrid.Background = surface;
                    dataGrid.Foreground = foreground;
                    dataGrid.BorderBrush = line;
                    dataGrid.RowBackground = surface;
                    dataGrid.AlternatingRowBackground = surface;
                    dataGrid.HorizontalGridLinesBrush = gridLine;
                    dataGrid.VerticalGridLinesBrush = gridLine;
                    dataGrid.ColumnHeaderStyle = BuildDataGridHeaderStyle(surface, foreground, line);
                    dataGrid.RowStyle = BuildDataGridRowStyle(surface, foreground);
                    dataGrid.CellStyle = BuildDataGridCellStyle(surface, foreground, gridLine);
                    ApplySystemBrushes(dataGrid, foreground, surface, accent);
                    break;

                case ListBox listBox:
                    listBox.Background = control;
                    listBox.Foreground = foreground;
                    listBox.BorderBrush = line;
                    listBox.ItemContainerStyle = BuildListBoxItemStyle(control, foreground, accent);
                    ApplySystemBrushes(listBox, foreground, control, accent);
                    break;

                case ProgressBar progressBar:
                    progressBar.Background = progressBar == ScanProgress ? Brushes.Transparent : control;
                    progressBar.Foreground = progressBar == ScanProgress ? ThemeBrush("#D4AF37") : accent;
                    progressBar.BorderBrush = line;
                    break;
            }

            ApplyThemeToChildren(child, foreground, muted, control, surface, line, gridLine, accent, danger);
        }
    }

    private Style BuildDataGridHeaderStyle(Brush background, Brush foreground, Brush border)
    {
        Style style = new(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        return style;
    }

    private static bool IsInsidePrimaryButton(DependencyObject element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is Button button &&
                button.Content is string text &&
                (text == "Connect" || text == "First Scan" || text == "Next Scan" || text == "Export"))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private Style BuildDataGridRowStyle(Brush background, Brush foreground)
    {
        Style style = new(typeof(DataGridRow));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        return style;
    }

    private Style BuildDataGridCellStyle(Brush background, Brush foreground, Brush border)
    {
        Style style = new(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
        return style;
    }

    private Style BuildListBoxItemStyle(Brush background, Brush foreground, Brush accent)
    {
        Style style = new(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, background));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Resources[SystemColors.HighlightBrushKey] = accent;
        style.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        return style;
    }

    private Style BuildComboBoxItemStyle(Brush background, Brush foreground, Brush accent)
    {
        Style style = new(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, background));
        style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        style.Resources[SystemColors.HighlightBrushKey] = accent;
        style.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        return style;
    }

    private static void ApplySystemBrushes(FrameworkElement element, Brush foreground, Brush background, Brush accent)
    {
        element.Resources[SystemColors.ControlTextBrushKey] = foreground;
        element.Resources[SystemColors.WindowTextBrushKey] = foreground;
        element.Resources[SystemColors.WindowBrushKey] = background;
        element.Resources[SystemColors.ControlBrushKey] = background;
        element.Resources[SystemColors.HighlightBrushKey] = accent;
        element.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
    }

    private void UpdateConnectionBadge()
    {
        if (ConnectionBadge is null)
        {
            return;
        }

        ConnectionBadge.Foreground = ConnectionBadge.Text switch
        {
            "Connected" => ThemeBrush(_isOledTheme ? "#00E676" : "#128A4C"),
            "Disconnected" => ThemeBrush(_isOledTheme ? "#FF5A52" : "#B42318"),
            _ => ThemeBrush(_isOledTheme ? "#9AA7B4" : "#68737D")
        };
    }

    private void UpdateStatusForeground()
    {
        if (StatusText is null)
        {
            return;
        }

        StatusText.Foreground = _statusIsSuccess
            ? ThemeBrush(_isOledTheme ? "#00E676" : "#128A4C")
            : ThemeBrush(_isOledTheme ? "#F8FAFC" : "#151A1E");
    }

    private static Brush ThemeBrush(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private void UpdateSecondValueVisibility()
    {
        if (SecondValueBox is null || CompareTypeBox is null)
        {
            return;
        }

        string selected = CompareTypeBox.SelectedItem as string ?? "";
        SecondValueBox.Visibility = selected == "Between Value" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetEbootDisplay(bool hooked)
    {
        if (ProcessDisplayBox is null ||
            ProcessDisplayText is null ||
            ProcessDisplayEmoji is null)
        {
            return;
        }

        ProcessDisplayText.Text = hooked ? "EBOOT HOOKED" : "EBOOT WAITING";
        ProcessDisplayEmoji.Text = hooked ? "✅" : "❌";
        ProcessDisplayEmoji.Foreground = ThemeBrush(hooked ? "#00E676" : "#FF3B30");
    }

    private void UpdateThemeToggle()
    {
        if (ThemeToggleButton is null)
        {
            return;
        }

        ThemeToggleButton.Content = BuildThemeButtonContent();
        ThemeToggleButton.ToolTip = "Theme";
    }

    private object BuildThemeButtonContent()
    {
        return new TextBlock
        {
            Text = "Theme",
            Foreground = ThemeBrush(_isOledTheme ? "#F8FAFC" : "#151A1E"),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void UpdateThemePopupButtons(Brush foreground, Brush control, Brush accent, Brush line)
    {
        ApplyThemePopupButton(ThemeLightButton, isActive: !_isOledTheme, foreground, control, accent, line);
        ApplyThemePopupButton(ThemeDarkButton, isActive: _isOledTheme, foreground, control, accent, line);
    }

    private void ApplyThemePopupButton(Button button, bool isActive, Brush foreground, Brush control, Brush accent, Brush line)
    {
        button.Background = isActive
            ? ThemeBrush(_isOledTheme ? "#0B1D33" : "#EAF3FF")
            : control;
        button.BorderBrush = isActive ? accent : line;
        button.Foreground = isActive ? accent : foreground;
        button.FontWeight = FontWeights.Normal;
    }

    private string BuildSectionStatus()
    {
        return $"EBOOT HOOKED ✅: {_state.Sections.Count:N0} sections loaded, {_state.Sections.Count(s => s.IsSelected):N0} selected.";
    }

    private void UpdateSectionTotals(bool updateStatus = false)
    {
        SelectedBox.Text = _state.Sections.Count(s => s.IsSelected).ToString();
        TotalBox.Text = _state.Sections.Count.ToString();

        if (updateStatus && _state.Sections.Count > 0)
        {
            SetStatus(BuildSectionStatus(), true);
        }
    }
}
