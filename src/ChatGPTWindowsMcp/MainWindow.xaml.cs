using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChatGPTWindowsMcp;

public partial class MainWindow : Window
{
    private const string TunnelUrl = "https://platform.openai.com/settings/organization/tunnels";
    private const string RuntimeKeyUrl = "https://platform.openai.com/settings/organization/api-keys";
    private const string ChatGptPluginsUrl = "https://chatgpt.com/plugins";
    private const string ChatGptHomeUrl = "https://chatgpt.com/";

    private readonly LogSink _logSink = App.Log;
    private readonly RuntimeManager _runtime;
    private AppConfig _config;
    private int _wizardStepIndex;
    private string _pluginCreationPrompt = "";
    private bool _pluginSetupOnly;
    private Task<bool>? _wizardStartupTask;
    private bool _registrationInProgress;
    private bool _isPreparingRuntime;
    private int? _startupPercent;
    private int _startupSequence;
    private string? _operationMessage;
    private bool _syncingApiKey;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;
    private readonly CancellationTokenSource _windowLifetime = new();
    private WindowsStartup? _windowsStartup;
    private TrayIcon? _tray;
    private bool _exitRequested;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        AdvancedExpander.IsExpanded = false;

        try
        {
            _config = AppConfig.Load();
        }
        catch (Exception ex)
        {
            _logSink.Error("config-load", ex);
            MessageBox.Show($"读取 config.json 失败：{ex.Message}", "配置错误", MessageBoxButton.OK, MessageBoxImage.Error);
            _config = new AppConfig();
        }

        _runtime = new RuntimeManager(_logSink);
        _logSink.LineReceived += OnLogLine;
        _runtime.StateChanged += OnStateChanged;
        _runtime.HealthChanged += OnHealthChanged;
        _logSink.ProtectSecret(_config.RuntimeApiKey);
        _logSink.Write("ui", "主窗口已初始化。");

        LoadAdvancedConfig();
        RenderMainState();

        Closing += MainWindowOnClosing;
        InitializeWindowsStartup();
        SilentStartupCheckBox.IsChecked = _config.SilentStartup;
        try
        {
            _tray = new TrayIcon(ShowFromTray, ToggleFromTray,
                () => OpenPath(AppPaths.LogsDirectory), RequestExit);
        }
        catch (Exception ex)
        {
            _logSink.Error("tray-create", ex);
        }
        RenderMainState();
    }

    internal async void StartApplication(string[] args)
    {
        var silent = _config.SilentStartup && IsConfigured() && _tray is not null;
        if (!silent) Show();
        if ((silent || args.Contains(WindowsStartup.StartupArgument, StringComparer.OrdinalIgnoreCase)) && IsConfigured())
            await StartConfiguredRuntimeAsync();
    }

    private void ShowFromTray()
    {
        if (_shutdownInProgress || _shutdownComplete) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleFromTray()
    {
        if (_operationMessage is not null || _shutdownInProgress) return;
        ShowFromTray();
        PrimaryButton_Click(this, new RoutedEventArgs());
    }

    private void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private void SilentStartupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var previous = _config.SilentStartup;
        _config.SilentStartup = SilentStartupCheckBox.IsChecked == true;
        if (SaveConfig()) return;
        _config.SilentStartup = previous;
        SilentStartupCheckBox.IsChecked = previous;
    }

    private void InitializeWindowsStartup()
    {
        try
        {
            _windowsStartup = new WindowsStartup(Environment.ProcessPath ?? "");
            WindowsStartupCheckBox.IsChecked = _windowsStartup.IsEnabled();
        }
        catch (Exception ex)
        {
            _logSink.Error("windows-startup-read", ex);
            WindowsStartupCheckBox.IsEnabled = false;
            WindowsStartupHelpText.Text = $"无法读取开机自启设置：{ex.Message}";
        }
    }

    private void WindowsStartupCheckBox_Click(object sender, RoutedEventArgs e)
        => SetWindowsStartupEnabled(WindowsStartupCheckBox.IsChecked == true);

    private void SetWindowsStartupEnabled(bool enabled)
    {
        if (_windowsStartup is null) return;
        try
        {
            _windowsStartup.SetEnabled(enabled);
            WindowsStartupCheckBox.IsChecked = _windowsStartup.IsEnabled();
            _logSink.Write("windows-startup", enabled ? "已启用登录 Windows 时自动启动。" : "已关闭开机自启。");
        }
        catch (Exception ex)
        {
            _logSink.Error("windows-startup-write", ex);
            InitializeWindowsStartup();
            MessageBox.Show($"无法更改开机自启设置：{ex.Message}", "开机自启", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OfferWindowsStartupAfterSetup()
    {
        if (_shutdownInProgress || _windowsStartup is null || !WindowsStartupCheckBox.IsEnabled) return;
        try
        {
            var enabled = _windowsStartup.IsEnabled();
            WindowsStartupCheckBox.IsChecked = enabled;
            if (enabled) return;
        }
        catch (Exception ex)
        {
            _logSink.Error("windows-startup-read", ex);
            InitializeWindowsStartup();
            return;
        }

        var choice = MessageBox.Show(this,
            "配置已完成，服务已成功连接。是否开启开机自启？\n\n开启后，登录 Windows 时将自动启动程序并连接已保存的服务。\n以后可在“高级选项 → 运行设置”中修改。",
            "开启开机自启", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice == MessageBoxResult.Yes) SetWindowsStartupEnabled(true);
    }

    private async void MainWindowOnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        e.Cancel = true;
        if (!_exitRequested && _tray is not null)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Notify("已收起到托盘", "服务会继续运行。点击托盘图标可打开窗口，右键选择“退出”可停止服务并退出程序。");
            }
            return;
        }
        if (_shutdownInProgress)
            return;

        _shutdownInProgress = true;
        _windowLifetime.Cancel();
        _logSink.Write("shutdown", "用户退出程序，取消正在执行的任务并停止本机服务。");
        IsEnabled = false;
        _operationMessage = "正在退出并清理本地资源…";
        RenderMainState();

        try
        {
            await _runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            _logSink.Write("退出清理超过 3 秒；窗口将立即关闭，Windows Job Object 会继续保证子进程随主进程退出。 ");
            _ = Task.Run(_runtime.ForceStop);
        }
        catch (Exception ex)
        {
            _logSink.Error("shutdown", ex);
            _logSink.Write($"退出清理时出现警告：{ex.Message}");
            _ = Task.Run(_runtime.ForceStop);
        }
        finally
        {
            _shutdownComplete = true;
            _shutdownInProgress = false;
            _tray?.Dispose();
            _tray = null;
            _logSink.LineReceived -= OnLogLine;
            _runtime.StateChanged -= OnStateChanged;
            _runtime.HealthChanged -= OnHealthChanged;
            _ = Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        }
    }
    private Brush ResourceBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private bool IsConfigured() =>
        IsTunnelIdValid(_config.TunnelId) && !string.IsNullOrWhiteSpace(_config.RuntimeApiKey);

    private static bool IsTunnelIdValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().StartsWith("tunnel_", StringComparison.OrdinalIgnoreCase);

    private void RenderMainState()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(RenderMainState);
            return;
        }

        UpdatePluginPrompt();

        var trayHealth = _runtime.Health;
        _tray?.Update(_operationMessage ?? (trayHealth.State == RuntimeState.Running
                ? RuntimeHealthState.RunningTitle(trayHealth)
                : trayHealth.State == RuntimeState.Faulted ? "连接失败" :
                  trayHealth.State == RuntimeState.Starting ? "正在连接…" :
                  trayHealth.State == RuntimeState.Stopping ? "正在停止…" :
                  IsConfigured() ? "服务已停止" : "尚未配置"),
            trayHealth.State == RuntimeState.Running,
            _operationMessage is not null || _shutdownInProgress || trayHealth.State is RuntimeState.Starting or RuntimeState.Stopping,
            trayHealth.McpReady && trayHealth.TunnelReady == true,
            trayHealth.State == RuntimeState.Faulted);

        if (_operationMessage is not null)
        {
            StatusTitleText.Text = _operationMessage;
            StatusTitleText.Foreground = ResourceBrush("AccentBrush", Brushes.RoyalBlue);
            StatusDetailText.Text = "请稍候，程序会自动完成所需步骤。";
            PrimaryButton.Content = "处理中…";
            PrimaryButton.IsEnabled = false;
            OpenChatGptButton.Visibility = Visibility.Collapsed;
            ReconfigureButton.Visibility = Visibility.Collapsed;
            SetAdvancedEditorsEnabled(false);
            return;
        }

        var configured = IsConfigured();
        var partiallyConfigured = IsTunnelIdValid(_config.TunnelId) || !string.IsNullOrWhiteSpace(_config.RuntimeApiKey);

        var health = _runtime.Health;
        switch (health.State)
        {
            case RuntimeState.Running:
                StatusTitleText.Text = RuntimeHealthState.RunningTitle(health);
                StatusTitleText.Foreground = health.McpReady && health.TunnelReady == true
                    ? ResourceBrush("SuccessBrush", Brushes.ForestGreen)
                    : ResourceBrush("WarningBrush", Brushes.DarkGoldenrod);
                StatusDetailText.Text = health.Detail +
                    (health.ObservedAt is { } observed ? $" 检查时间：{observed.ToLocalTime():HH:mm:ss}。" : "") +
                    " 当前聊天的工具清单、权限及协议会话仍需在 ChatGPT 中核对。";
                PrimaryButton.Content = "停止";
                PrimaryButton.IsEnabled = true;
                OpenChatGptButton.Visibility = Visibility.Visible;
                ReconfigureButton.Visibility = Visibility.Collapsed;
                SetAdvancedEditorsEnabled(false);
                break;

            case RuntimeState.Starting:
                StatusTitleText.Text = "正在连接…";
                StatusTitleText.Foreground = ResourceBrush("AccentBrush", Brushes.RoyalBlue);
                StatusDetailText.Text = "正在启动 Windows-MCP 并连接 OpenAI Tunnel。";
                PrimaryButton.Content = "正在连接…";
                PrimaryButton.IsEnabled = false;
                OpenChatGptButton.Visibility = Visibility.Collapsed;
                ReconfigureButton.Visibility = Visibility.Collapsed;
                SetAdvancedEditorsEnabled(false);
                break;

            case RuntimeState.Stopping:
                StatusTitleText.Text = "正在停止…";
                StatusTitleText.Foreground = ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
                StatusDetailText.Text = "正在关闭本地 MCP 和 Tunnel。";
                PrimaryButton.Content = "正在停止…";
                PrimaryButton.IsEnabled = false;
                OpenChatGptButton.Visibility = Visibility.Collapsed;
                ReconfigureButton.Visibility = Visibility.Collapsed;
                SetAdvancedEditorsEnabled(false);
                break;

            case RuntimeState.Faulted:
                StatusTitleText.Text = "● 连接失败";
                StatusTitleText.Foreground = ResourceBrush("DangerBrush", Brushes.Firebrick);
                StatusDetailText.Text = configured
                    ? health.Detail + " 其余子进程可能仍在运行；重试或重新配置会先清理，不会重放工具操作。"
                    : "请先完成连接配置。";
                PrimaryButton.Content = configured ? "重新连接" : partiallyConfigured ? "继续配置" : "创建链接";
                PrimaryButton.IsEnabled = true;
                OpenChatGptButton.Visibility = Visibility.Collapsed;
                ReconfigureButton.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
                SetAdvancedEditorsEnabled(true);
                break;

            default:
                if (configured)
                {
                    StatusTitleText.Text = "✓ 配置完成";
                    StatusTitleText.Foreground = ResourceBrush("SuccessBrush", Brushes.ForestGreen);
                    StatusDetailText.Text = "Tunnel ID 和 Runtime API Key 已配置。";
                    PrimaryButton.Content = "启动";
                    ReconfigureButton.Visibility = Visibility.Visible;
                }
                else if (partiallyConfigured)
                {
                    StatusTitleText.Text = "○ 配置未完成";
                    StatusTitleText.Foreground = ResourceBrush("WarningBrush", Brushes.DarkGoldenrod);
                    StatusDetailText.Text = "继续完成连接配置后即可启动。";
                    PrimaryButton.Content = "继续配置";
                    ReconfigureButton.Visibility = Visibility.Visible;
                }
                else
                {
                    StatusTitleText.Text = "○ 尚未配置连接";
                    StatusTitleText.Foreground = ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
                    StatusDetailText.Text = "创建连接只需要 Tunnel ID 和 Runtime API Key。";
                    PrimaryButton.Content = "创建链接";
                    ReconfigureButton.Visibility = Visibility.Collapsed;
                }

                PrimaryButton.IsEnabled = true;
                OpenChatGptButton.Visibility = Visibility.Collapsed;
                SetAdvancedEditorsEnabled(true);
                break;
        }
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _shutdownInProgress) return;
        if (_runtime.State == RuntimeState.Running)
        {
            SetOperation("正在停止…");
            try
            {
                await _runtime.StopAsync();
            }
            catch (Exception ex)
            {
                _logSink.Error("stop-ui", ex);
                MessageBox.Show(ex.Message, "停止失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetOperation(null);
            }
            return;
        }

        if (!IsConfigured())
        {
            ShowWizard(startFromBeginning: false);
            return;
        }

        if (!SaveAdvancedConfig(showSuccess: false))
            return;

        await StartConfiguredRuntimeAsync();
    }

    private async Task<bool> StartConfiguredRuntimeAsync()
    {
        _isPreparingRuntime = true;
        _startupPercent = 0;
        var sequence = ++_startupSequence;
        var progress = new Progress<(int Percent, string Detail)>(value => ReportStartupProgress(sequence, value));
        _logSink.ProtectSecret(_config.RuntimeApiKey);
        _logSink.Write("startup", $"开始启动；Tunnel={_config.TunnelId}；MCP 端口={_config.McpPort}；Python={_config.PythonVersion}；包={_config.WindowsMcpSpec}。");
        SetOperation("正在准备运行环境…");

        try
        {
            await _runtime.InstallDependenciesAsync(_config, _windowLifetime.Token, progress);
            await _runtime.StartAsync(_config, _windowLifetime.Token, progress);
            _startupPercent = 100;
            return true;
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
        {
            _logSink.Write("startup", "启动已因窗口关闭而取消。");
            return false;
        }
        catch (Exception ex)
        {
            if (_shutdownInProgress) return false;
            _logSink.Error("startup-ui", ex);
            AdvancedExpander.IsExpanded = true;
            if (!IsVisible && _tray is not null)
                _tray.Notify("连接失败", "无法启动已保存的服务。点击此通知或托盘图标查看运行日志并重试。", error: true);
            else
                MessageBox.Show(ex.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _isPreparingRuntime = false;
            SetOperation(null);
        }
    }

    private Task<bool> EnsureWizardRuntimeAsync()
    {
        if (_shutdownInProgress || !IsConfigured()) return Task.FromResult(false);
        if (_wizardStartupTask is { IsCompleted: false }) return _wizardStartupTask;
        if (_runtime.State == RuntimeState.Running && _runtime.ActiveTunnelId == _config.TunnelId.Trim())
            return Task.FromResult(true);
        if (_operationMessage is not null) return Task.FromResult(false);
        _wizardStartupTask = StartConfiguredRuntimeAsync();
        return _wizardStartupTask;
    }

    private void ReportStartupProgress(int sequence, (int Percent, string Detail) value)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => ReportStartupProgress(sequence, value));
            return;
        }
        if (!_isPreparingRuntime || _shutdownInProgress || sequence != _startupSequence) return;
        if (value.Percent < (_startupPercent ?? 0)) return;
        _startupPercent = Math.Max(_startupPercent ?? 0, Math.Clamp(value.Percent, 0, 100));
        SetOperation(value.Detail);
    }

    private void RenderStartupProgress()
    {
        var visibility = _startupPercent.HasValue ? Visibility.Visible : Visibility.Collapsed;
        MainStartupPercentText.Visibility = PluginStartupPercentText.Visibility = visibility;
        MainStartupPercentText.Text = PluginStartupPercentText.Text = $"{_startupPercent ?? 0}%";
    }

    private void SetOperation(string? message)
    {
        _operationMessage = message;
        if (message is not null) _logSink.Write("operation", message);
        RenderMainState();
    }

    private async void ReconfigureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _shutdownInProgress) return;
        if (_runtime.State != RuntimeState.Stopped)
        {
            SetOperation("正在清理上次运行…");
            try { await _runtime.StopAsync(); }
            catch (Exception ex)
            {
                _logSink.Error("reconfigure-stop", ex);
                if (!_shutdownInProgress)
                    MessageBox.Show(ex.Message, "停止失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally { SetOperation(null); }
        }
        if (!_shutdownInProgress && !_shutdownComplete) ShowWizard(startFromBeginning: true);
    }
    private void OpenChatGptButton_Click(object sender, RoutedEventArgs e) => OpenUrl(ChatGptHomeUrl);

    private void CopyChatGptLinkButton_Click(object sender, RoutedEventArgs e) =>
        CopyText(ChatGptHomeUrl, PluginPromptCopyFeedbackText, "✓ ChatGPT 链接已复制。");

    private void CopyPluginTunnelIdButton_Click(object sender, RoutedEventArgs e) =>
        CopyText(_runtime.ActiveTunnelId ?? _config.TunnelId.Trim(), PluginPromptCopyFeedbackText, "✓ Tunnel ID 已复制，请在 ChatGPT 选择 Tunnel 并使用此 ID。");

    private async void RegisterTunnelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _shutdownInProgress || _registrationInProgress) return;
        _registrationInProgress = true;
        RegisterTunnelButton.IsEnabled = false;
        RegisterTunnelButton.Content = "正在检查连接…";
        try
        {
            if (!await EnsureWizardRuntimeAsync()) return;
            var summary = await _runtime.CheckRegistrationAsync(_config, _windowLifetime.Token);
            PluginPromptCopyFeedbackText.Text = summary + " 在插件页点击“添加 → 创建 MCP 应用”，连接选择“隧道”，粘贴 Tunnel ID，身份验证选择“无需身份验证”。";
            PluginPromptCopyFeedbackText.Foreground = ResourceBrush("SuccessBrush", Brushes.ForestGreen);
            OpenUrl(ChatGptPluginsUrl);
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logSink.Error("registration-ui", ex);
            PluginPromptCopyFeedbackText.Text = ex.Message;
            PluginPromptCopyFeedbackText.Foreground = ResourceBrush("DangerBrush", Brushes.Firebrick);
        }
        finally
        {
            _registrationInProgress = false;
            RegisterTunnelButton.Content = "创建 MCP 应用";
            UpdatePluginPrompt();
        }
    }

    private async void PluginSetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _shutdownInProgress || !IsConfigured()) return;
        _pluginSetupOnly = true;
        _wizardStepIndex = 3;
        PluginNameTextBox.Text = _config.PluginDisplayName;
        PluginConnectionLinkTextBox.Text = _config.PluginConnectionAppId;
        RenderWizardStep();
        MainScrollViewer.Visibility = Visibility.Collapsed;
        WizardRoot.Visibility = Visibility.Visible;
        await EnsureWizardRuntimeAsync();
    }

    private void PluginSettings_TextChanged(object sender, TextChangedEventArgs e) => UpdatePluginPrompt();

    private void UpdatePluginPrompt()
    {
        if (_config is null || PluginNameTextBox is null || CopyPluginPromptButton is null ||
            PluginNameValidationText is null || PluginPromptCopyFeedbackText is null || PluginFinishButton is null ||
            PluginConnectionLinkTextBox is null || PluginConnectionValidationText is null || PluginSetupButton is null) return;
        var validName = PluginCreationPrompt.IsNameValid(PluginNameTextBox.Text);
        var emptyConnection = string.IsNullOrWhiteSpace(PluginConnectionLinkTextBox.Text);
        var validConnection = PluginCreationPrompt.TryGetConnectionId(PluginConnectionLinkTextBox.Text, out var appId);
        PluginNameValidationText.Text = validName ? "" : "请填写插件名字（最多 80 个字符）。";
        PluginNameValidationText.Visibility = validName ? Visibility.Collapsed : Visibility.Visible;
        PluginNameValidationText.Foreground = ResourceBrush("DangerBrush", Brushes.Firebrick);
        PluginConnectionValidationText.Text = emptyConnection ? "可留空，AI 会尝试查找或创建连接；缺少连接能力时会提示你完成手动步骤。"
            : validConnection ? "✓ 连接 ID 已识别；请确认此连接使用复制的 Tunnel ID。"
            : "请粘贴包含 plugin_asdk_app... 的 MCP 应用详情链接（可选），或该连接 ID。";
        PluginConnectionValidationText.Foreground = ResourceBrush(!emptyConnection && !validConnection ? "DangerBrush" : "TextSecondaryBrush", Brushes.DimGray);
        var prompt = validName && (emptyConnection || validConnection) && IsTunnelIdValid(_config.TunnelId)
            ? PluginCreationPrompt.Build(PluginNameTextBox.Text, _config.TunnelId, appId) : "";
        if (_pluginCreationPrompt != prompt)
        {
            _pluginCreationPrompt = prompt;
            PluginPromptCopyFeedbackText.Text = "";
        }
        CopyPluginPromptButton.IsEnabled = prompt.Length > 0 && _operationMessage is null && !_registrationInProgress && !_shutdownInProgress;
        PluginFinishButton.IsEnabled = validName && (emptyConnection || validConnection) && _operationMessage is null && !_registrationInProgress && !_shutdownInProgress;
        PluginSetupButton.Visibility = IsConfigured() ? Visibility.Visible : Visibility.Collapsed;
        PluginSetupButton.IsEnabled = _operationMessage is null && !_shutdownInProgress;
        RenderStartupProgress();
        RegisterTunnelButton.IsEnabled = _operationMessage is null && !_registrationInProgress && !_shutdownInProgress;
        PluginBackButton.IsEnabled = _operationMessage is null && !_registrationInProgress && !_shutdownInProgress;
        PluginServiceStatusText.Text = _operationMessage ?? (_runtime.State == RuntimeState.Running
            ? "本机服务和 Tunnel 已就绪，可以创建 MCP 应用。"
            : _runtime.State == RuntimeState.Faulted ? _runtime.Health.Detail : "正在自动准备本机服务和 Tunnel，完成后即可创建 MCP 应用。");
        PluginTunnelIdText.Text = $"{(_runtime.ActiveTunnelId is null ? "配置" : "当前运行")}的 Tunnel：{_runtime.ActiveTunnelId ?? _config.TunnelId.Trim()}";
    }

    private bool SavePluginSettings()
    {
        UpdatePluginPrompt();
        if (!PluginCreationPrompt.IsNameValid(PluginNameTextBox.Text)) return false;
        var input = PluginConnectionLinkTextBox.Text;
        if (!string.IsNullOrWhiteSpace(input) && !PluginCreationPrompt.TryGetConnectionId(input, out _)) return false;
        _config.PluginDisplayName = PluginNameTextBox.Text.Trim();
        _config.PluginConnectionAppId = PluginCreationPrompt.TryGetConnectionId(input, out var appId) ? appId : "";
        return SaveConfig();
    }

    private async void CopyPluginPromptButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _registrationInProgress || _shutdownInProgress) return;
        if (!SavePluginSettings() || _pluginCreationPrompt.Length == 0) return;
        if (!await EnsureWizardRuntimeAsync()) return;
        CopyText(_pluginCreationPrompt, PluginPromptCopyFeedbackText, "✓ 提示词已复制，可发送给 @Plugin Creator。");
    }

    private void ShowWizard(bool startFromBeginning)
    {
        _pluginSetupOnly = false;
        _wizardStepIndex = startFromBeginning
            ? 0
            : IsConfigured() ? 2 : IsTunnelIdValid(_config.TunnelId) ? 1 : 0;

        TunnelIdTextBox.Text = _config.TunnelId;
        SetApiKeyControls(_config.RuntimeApiKey);
        WizardProxyTextBox.Text = _config.ControlPlaneHttpProxy;
        WizardAutoDetectProxyCheckBox.IsChecked = _config.AutoDetectSystemProxy;
        PluginNameTextBox.Text = _config.PluginDisplayName;
        PluginConnectionLinkTextBox.Text = _config.PluginConnectionAppId;
        RenderWizardStep();
        MainScrollViewer.Visibility = Visibility.Collapsed;
        WizardRoot.Visibility = Visibility.Visible;
        TunnelLinkFeedbackText.Text = "";
        ApiKeyLinkFeedbackText.Text = "";
        _logSink.Write("wizard", $"进入配置步骤 {_wizardStepIndex + 1}。");
    }

    private void ShowMainPage()
    {
        WizardRoot.Visibility = Visibility.Collapsed;
        MainScrollViewer.Visibility = Visibility.Visible;
        LoadAdvancedConfig();
        RenderMainState();
        MainScrollViewer.ScrollToTop();
    }

    private void RenderWizardStep()
    {
        var tunnelStep = _wizardStepIndex == 0;
        var apiStep = _wizardStepIndex == 1;
        var proxyStep = _wizardStepIndex == 2;
        var pluginStep = _wizardStepIndex == 3;
        TunnelStepPanel.Visibility = tunnelStep ? Visibility.Visible : Visibility.Collapsed;
        ApiStepPanel.Visibility = apiStep ? Visibility.Visible : Visibility.Collapsed;
        ProxyStepPanel.Visibility = proxyStep ? Visibility.Visible : Visibility.Collapsed;
        PluginStepPanel.Visibility = pluginStep ? Visibility.Visible : Visibility.Collapsed;
        PluginBackButton.Visibility = _pluginSetupOnly ? Visibility.Collapsed : Visibility.Visible;
        WizardStepCaption.Text = $"步骤 {_wizardStepIndex + 1} / 4";

        TunnelStepPill.Foreground = tunnelStep
            ? ResourceBrush("AccentBrush", Brushes.RoyalBlue)
            : ResourceBrush("SuccessBrush", Brushes.ForestGreen);
        TunnelStepPill.Text = tunnelStep ? "1  Tunnel ID" : "✓  Tunnel ID";
        ApiStepPill.Foreground = apiStep
            ? ResourceBrush("AccentBrush", Brushes.RoyalBlue)
            : _wizardStepIndex > 1 ? ResourceBrush("SuccessBrush", Brushes.ForestGreen) : ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
        ApiStepPill.Text = _wizardStepIndex > 1 ? "✓  API Key" : "2  API Key";
        ProxyStepPill.Foreground = proxyStep ? ResourceBrush("AccentBrush", Brushes.RoyalBlue)
            : pluginStep ? ResourceBrush("SuccessBrush", Brushes.ForestGreen) : ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
        ProxyStepPill.Text = pluginStep ? "✓  网络代理" : "3  网络代理";
        PluginStepPill.Foreground = pluginStep
            ? ResourceBrush("AccentBrush", Brushes.RoyalBlue)
            : ResourceBrush("TextSecondaryBrush", Brushes.DimGray);

        UpdateTunnelValidation();
        UpdateApiValidation();
        UpdateWizardProxyValidation();
        UpdatePluginPrompt();

        Dispatcher.BeginInvoke(() =>
        {
            if (tunnelStep)
            {
                TunnelIdTextBox.Focus();
                TunnelIdTextBox.CaretIndex = TunnelIdTextBox.Text.Length;
            }
            else if (apiStep && ShowApiKeyCheckBox.IsChecked == true)
            {
                ApiKeyTextBox.Focus();
                ApiKeyTextBox.CaretIndex = ApiKeyTextBox.Text.Length;
            }
            else if (apiStep)
            {
                ApiKeyPasswordBox.Focus();
            }
            else if (proxyStep)
            {
                WizardProxyTextBox.Focus();
                WizardProxyTextBox.CaretIndex = WizardProxyTextBox.Text.Length;
            }
            else
            {
                PluginNameTextBox.Focus();
            }
        });
    }

    private void WizardOpenTunnel_Click(object sender, RoutedEventArgs e) => OpenUrl(TunnelUrl);
    private void WizardOpenApiKey_Click(object sender, RoutedEventArgs e) => OpenUrl(RuntimeKeyUrl);

    private void WizardCopyTunnel_Click(object sender, RoutedEventArgs e) =>
        CopyCreationLink(TunnelUrl, TunnelLinkFeedbackText);

    private void WizardCopyApiKey_Click(object sender, RoutedEventArgs e) =>
        CopyCreationLink(RuntimeKeyUrl, ApiKeyLinkFeedbackText);

    private void CopyCreationLink(string url, TextBlock feedback)
        => CopyText(url, feedback, "✓ 链接已复制，可粘贴到浏览器中打开。");

    private void CopyText(string text, TextBlock feedback, string successMessage)
    {
        try
        {
            Clipboard.SetText(text);
            feedback.Text = successMessage;
            feedback.Foreground = ResourceBrush("SuccessBrush", Brushes.ForestGreen);
            _logSink.Write("clipboard", "复制调用成功。");
        }
        catch (Exception ex)
        {
            _logSink.Error("clipboard", ex);
            feedback.Text = "未能确认复制结果，请尝试粘贴或重试。";
            feedback.Foreground = ResourceBrush("DangerBrush", Brushes.Firebrick);
        }
    }

    private void TunnelIdTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateTunnelValidation();

    private void UpdateTunnelValidation()
    {
        if (TunnelValidationText is null || TunnelNextButton is null)
            return;

        var value = TunnelIdTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            TunnelValidationText.Text = "Tunnel ID 通常以 tunnel_ 开头。";
            TunnelValidationText.Foreground = ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
        }
        else if (IsTunnelIdValid(value))
        {
            TunnelValidationText.Text = "✓ Tunnel ID 格式正确";
            TunnelValidationText.Foreground = ResourceBrush("SuccessBrush", Brushes.ForestGreen);
        }
        else
        {
            TunnelValidationText.Text = "Tunnel ID 格式不正确，应以 tunnel_ 开头。";
            TunnelValidationText.Foreground = ResourceBrush("DangerBrush", Brushes.Firebrick);
        }

        TunnelNextButton.IsEnabled = IsTunnelIdValid(value);
    }

    private void TunnelNextButton_Click(object sender, RoutedEventArgs e)
    {
        var tunnelId = TunnelIdTextBox.Text.Trim();
        if (!IsTunnelIdValid(tunnelId))
            return;

        SetTunnelId(tunnelId);
        if (!SaveConfig()) return;
        _wizardStepIndex = 1;
        RenderWizardStep();
    }

    private void WizardBack_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentApiKeyIfPresent();
        _wizardStepIndex = 0;
        RenderWizardStep();
    }

    private void WizardCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _registrationInProgress || _shutdownInProgress) return;
        if (_wizardStepIndex == 0)
        {
            var tunnelId = TunnelIdTextBox.Text.Trim();
            if (IsTunnelIdValid(tunnelId))
            {
                SetTunnelId(tunnelId);
                SaveConfig();
            }
        }
        else if (_wizardStepIndex == 1)
        {
            SaveCurrentApiKeyIfPresent();
        }
        else if (_wizardStepIndex == 2)
        {
            SaveWizardProxyIfValid();
        }
        else
        {
            SavePluginSettings();
        }

        ShowMainPage();
    }

    private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingApiKey)
            return;

        if (ShowApiKeyCheckBox.IsChecked != true)
        {
            _syncingApiKey = true;
            ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
            _syncingApiKey = false;
        }

        UpdateApiValidation();
    }

    private void ApiKeyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingApiKey)
            return;

        if (ShowApiKeyCheckBox.IsChecked == true)
        {
            _syncingApiKey = true;
            ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
            _syncingApiKey = false;
        }

        UpdateApiValidation();
    }

    private void ShowApiKeyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ApiKeyPasswordBox is null || ApiKeyTextBox is null)
            return;

        _syncingApiKey = true;
        if (ShowApiKeyCheckBox.IsChecked == true)
        {
            ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
            ApiKeyTextBox.Focus();
            ApiKeyTextBox.CaretIndex = ApiKeyTextBox.Text.Length;
        }
        else
        {
            ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
            ApiKeyPasswordBox.Visibility = Visibility.Visible;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ApiKeyPasswordBox.Focus();
        }
        _syncingApiKey = false;
        UpdateApiValidation();
    }

    private string CurrentApiKey() =>
        ShowApiKeyCheckBox.IsChecked == true ? ApiKeyTextBox.Text.Trim() : ApiKeyPasswordBox.Password.Trim();

    private void SetApiKeyControls(string value)
    {
        _syncingApiKey = true;
        ApiKeyPasswordBox.Password = value ?? string.Empty;
        ApiKeyTextBox.Text = value ?? string.Empty;
        ShowApiKeyCheckBox.IsChecked = false;
        ApiKeyPasswordBox.Visibility = Visibility.Visible;
        ApiKeyTextBox.Visibility = Visibility.Collapsed;
        _syncingApiKey = false;
    }

    private void UpdateApiValidation()
    {
        if (ApiValidationText is null || ApiFinishButton is null)
            return;

        var apiKey = CurrentApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ApiValidationText.Text = "请粘贴 Runtime API Key。";
            ApiValidationText.Foreground = ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
            ApiFinishButton.IsEnabled = false;
        }
        else
        {
            ApiValidationText.Text = "✓ API Key 已填写";
            ApiValidationText.Foreground = ResourceBrush("SuccessBrush", Brushes.ForestGreen);
            ApiFinishButton.IsEnabled = true;
        }
    }

    private void ApiFinishButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = CurrentApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        _config.RuntimeApiKey = apiKey;
        if (!SaveConfig()) return;
        _wizardStepIndex = 2;
        RenderWizardStep();
        _logSink.Write("wizard", "进入第 3 步：设置网络代理。");
    }

    private void ProxyBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveWizardProxyIfValid()) return;
        _wizardStepIndex = 1;
        RenderWizardStep();
    }

    private async void ProxyNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _shutdownInProgress) return;
        if (!SaveWizardProxyIfValid()) return;
        _wizardStepIndex = 3;
        RenderWizardStep();
        _logSink.Write("wizard", "进入第 4 步：填写插件名字并复制创建提示词。");
        await EnsureWizardRuntimeAsync();
    }

    private async void PluginBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _registrationInProgress || _shutdownInProgress) return;
        if (_runtime.State != RuntimeState.Stopped)
        {
            SetOperation("正在停止服务，以便修改代理…");
            try { await _runtime.StopAsync(); }
            catch (Exception ex)
            {
                _logSink.Error("wizard-stop", ex);
                if (!_shutdownInProgress) MessageBox.Show(ex.Message, "停止失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally { SetOperation(null); }
        }
        if (_shutdownInProgress) return;
        _wizardStepIndex = 2;
        RenderWizardStep();
    }

    private void WizardProxyTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateWizardProxyValidation();

    private void UpdateWizardProxyValidation()
    {
        if (WizardProxyValidationText is null || ProxyNextButton is null)
            return;

        var proxy = WizardProxyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(proxy))
        {
            WizardProxyValidationText.Text = "可留空。示例：http://127.0.0.1:7890";
            WizardProxyValidationText.Foreground = ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
            ProxyNextButton.IsEnabled = true;
        }
        else if (ProxyResolver.TryNormalizeProxyUrl(proxy, out _))
        {
            WizardProxyValidationText.Text = "✓ 代理地址格式正确";
            WizardProxyValidationText.Foreground = ResourceBrush("SuccessBrush", Brushes.ForestGreen);
            ProxyNextButton.IsEnabled = true;
        }
        else
        {
            WizardProxyValidationText.Text = "代理地址必须以 http:// 或 https:// 开头。";
            WizardProxyValidationText.Foreground = ResourceBrush("DangerBrush", Brushes.Firebrick);
            ProxyNextButton.IsEnabled = false;
        }
    }

    private async void PluginFinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _registrationInProgress || _shutdownInProgress) return;
        if (!SavePluginSettings())
            return;
        _registrationInProgress = true;
        PluginFinishButton.Content = "正在检查…";
        UpdatePluginPrompt();
        var setupCompleted = false;
        try
        {
            var errors = _config.Validate();
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            if (!await EnsureWizardRuntimeAsync())
                throw new InvalidOperationException("本机服务尚未启动成功，配置还未完成。请检查启动错误后再次点击“完成配置”。");
            SetOperation("正在确认 MCP 工具和 Tunnel 连接就绪…");
            var summary = await _runtime.CheckRegistrationAsync(_config, _windowLifetime.Token);
            _windowLifetime.Token.ThrowIfCancellationRequested();
            _logSink.Write("wizard", "完成配置检查通过；" + summary + " ChatGPT 端应用创建状态需在 ChatGPT 中确认。");
            ShowMainPage();
            setupCompleted = true;
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logSink.Error("wizard-finish", ex);
            PluginPromptCopyFeedbackText.Text = "配置未完成：" + ex.Message;
            PluginPromptCopyFeedbackText.Foreground = ResourceBrush("DangerBrush", Brushes.Firebrick);
        }
        finally
        {
            _registrationInProgress = false;
            PluginFinishButton.Content = "完成配置";
            SetOperation(null);
        }
        if (setupCompleted) OfferWindowsStartupAfterSetup();
    }

    private bool SaveWizardProxyIfValid()
    {
        var proxy = WizardProxyTextBox.Text.Trim();
        var normalized = "";
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            if (!ProxyResolver.TryNormalizeProxyUrl(proxy, out normalized))
                return false;
        }

        _config.ControlPlaneHttpProxy = string.IsNullOrWhiteSpace(proxy) ? "" : normalized;
        _config.AutoDetectSystemProxy = WizardAutoDetectProxyCheckBox.IsChecked == true;
        return SaveConfig();
    }

    private void SaveCurrentApiKeyIfPresent()
    {
        var apiKey = CurrentApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        _config.RuntimeApiKey = apiKey;
        SaveConfig();
    }

    private bool SaveConfig()
    {
        try
        {
            _logSink.ProtectSecret(_config.RuntimeApiKey);
            _config.Save();
            _logSink.Write("config", "配置已保存（不记录 API Key 内容）。");
            return true;
        }
        catch (Exception ex)
        {
            _logSink.Error("config-save", ex);
            MessageBox.Show($"保存配置失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void LoadAdvancedConfig()
    {
        AdvancedTunnelIdTextBox.Text = _config.TunnelId;
        AdvancedApiKeyPasswordBox.Password = _config.RuntimeApiKey;
        PortTextBox.Text = _config.McpPort.ToString();
        ProfileTextBox.Text = _config.ProfileName;
        WindowsMcpTextBox.Text = _config.WindowsMcpSpec;
        PythonTextBox.Text = _config.PythonVersion;
        ProxyTextBox.Text = _config.ControlPlaneHttpProxy;
        AutoDetectProxyCheckBox.IsChecked = _config.AutoDetectSystemProxy;
        ReuseMcpCheckBox.IsChecked = _config.ReuseExistingMcp;
        AutoDownloadTunnelCheckBox.IsChecked = _config.AutoDownloadTunnelClient;
    }

    private bool SaveAdvancedConfig(bool showSuccess)
    {
        var tunnelId = AdvancedTunnelIdTextBox.Text.Trim();
        if (!IsTunnelIdValid(tunnelId))
        {
            MessageBox.Show("Tunnel ID 无效，应以 tunnel_ 开头。", "高级选项", MessageBoxButton.OK, MessageBoxImage.Warning);
            AdvancedExpander.IsExpanded = true;
            AdvancedTunnelIdTextBox.Focus();
            return false;
        }

        var apiKey = AdvancedApiKeyPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("Runtime API Key 不能为空。", "高级选项", MessageBoxButton.OK, MessageBoxImage.Warning);
            AdvancedExpander.IsExpanded = true;
            AdvancedApiKeyPasswordBox.Focus();
            return false;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            MessageBox.Show("MCP 本地端口必须在 1024–65535 之间。", "高级选项", MessageBoxButton.OK, MessageBoxImage.Warning);
            AdvancedExpander.IsExpanded = true;
            PortTextBox.Focus();
            return false;
        }

        var proxy = ProxyTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(proxy) && !ProxyResolver.TryNormalizeProxyUrl(proxy, out _))
        {
            MessageBox.Show("代理地址必须以 http:// 或 https:// 开头，例如 http://127.0.0.1:7890。", "高级选项", MessageBoxButton.OK, MessageBoxImage.Warning);
            AdvancedExpander.IsExpanded = true;
            ProxyTextBox.Focus();
            return false;
        }

        SetTunnelId(tunnelId);
        _config.RuntimeApiKey = apiKey;
        _config.McpPort = port;
        _config.ProfileName = string.IsNullOrWhiteSpace(ProfileTextBox.Text) ? "windows-mcp" : ProfileTextBox.Text.Trim();
        _config.WindowsMcpSpec = string.IsNullOrWhiteSpace(WindowsMcpTextBox.Text) ? "windows-mcp" : WindowsMcpTextBox.Text.Trim();
        _config.PythonVersion = string.IsNullOrWhiteSpace(PythonTextBox.Text) ? "3.13" : PythonTextBox.Text.Trim();
        _config.ControlPlaneHttpProxy = string.IsNullOrWhiteSpace(proxy)
            ? ""
            : ProxyResolver.TryNormalizeProxyUrl(proxy, out var normalizedProxy) ? normalizedProxy : proxy;
        _config.AutoDetectSystemProxy = AutoDetectProxyCheckBox.IsChecked == true;
        _config.ReuseExistingMcp = ReuseMcpCheckBox.IsChecked == true;
        _config.AutoDownloadTunnelClient = AutoDownloadTunnelCheckBox.IsChecked == true;

        try
        {
            _logSink.ProtectSecret(_config.RuntimeApiKey);
            _config.Save();
            _logSink.Write("config", "高级选项已保存。");
            RenderMainState();
            if (showSuccess)
                MessageBox.Show("高级选项已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            _logSink.Error("config-advanced-save", ex);
            MessageBox.Show($"保存 config.json 失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void SetTunnelId(string tunnelId)
    {
        if (!string.Equals(_config.TunnelId, tunnelId, StringComparison.Ordinal))
        {
            _config.PluginConnectionAppId = "";
            PluginConnectionLinkTextBox.Text = "";
        }
        _config.TunnelId = tunnelId;
        UpdatePluginPrompt();
    }

    private void SetAdvancedEditorsEnabled(bool enabled)
    {
        AdvancedTunnelIdTextBox.IsEnabled = enabled;
        AdvancedApiKeyPasswordBox.IsEnabled = enabled;
        PortTextBox.IsEnabled = enabled;
        ProfileTextBox.IsEnabled = enabled;
        WindowsMcpTextBox.IsEnabled = enabled;
        PythonTextBox.IsEnabled = enabled;
        ProxyTextBox.IsEnabled = enabled;
        AutoDetectProxyCheckBox.IsEnabled = enabled;
        ReuseMcpCheckBox.IsEnabled = enabled;
        AutoDownloadTunnelCheckBox.IsEnabled = enabled;
    }

    private void SaveAdvancedButton_Click(object sender, RoutedEventArgs e) => SaveAdvancedConfig(showSuccess: true);

    private async void DoctorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationMessage is not null || _shutdownInProgress) return;
        if (_runtime.State == RuntimeState.Running)
        {
            try
            {
                var summary = await _runtime.CheckRegistrationAsync(_config, _windowLifetime.Token);
                MessageBox.Show(summary + "\n若 ChatGPT 仍创建失败，请确认使用此 ID，并核对工作区关联及 Tunnels Read + Use 权限。",
                    "连接诊断", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logSink.Error("doctor-ui", ex);
                if (!_shutdownInProgress) MessageBox.Show(ex.Message, "连接诊断", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }
        if (!IsConfigured())
        {
            MessageBox.Show("请先完成 Tunnel ID 和 Runtime API Key 配置。", "连接诊断", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowWizard(startFromBeginning: false);
            return;
        }

        if (!SaveAdvancedConfig(showSuccess: false))
            return;

        _logSink.Write("doctor", "用户开始连接诊断。");
        AdvancedExpander.IsExpanded = true;
        SetOperation("正在运行连接诊断…");

        try
        {
            await _runtime.InstallDependenciesAsync(_config, _windowLifetime.Token);
            var exitCode = await _runtime.DoctorAsync(_config, _windowLifetime.Token);
            MessageBox.Show(
                exitCode == 0 ? "诊断完成，未发现阻断性错误。" : $"诊断退出码：{exitCode}。请查看运行日志。",
                "连接诊断",
                MessageBoxButton.OK,
                exitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_shutdownInProgress) return;
            _logSink.Error("doctor-ui", ex);
            MessageBox.Show(ex.Message, "诊断失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetOperation(null);
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenPath(AppPaths.LogsDirectory);
    private void OpenConfigButton_Click(object sender, RoutedEventArgs e) => OpenConfig();
    private void OpenConnectorButton_Click(object sender, RoutedEventArgs e) => OpenUrl(ChatGptPluginsUrl);

    private void OnLogLine(string line)
    {
        if (Dispatcher.HasShutdownStarted || _shutdownComplete) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        AppendLog(line);
    }

    private void AppendLog(string line)
    {
        // Bound the UI buffer; the on-disk log is the diagnostic source.
        if (LogTextBox.Text.Length > 200_000) LogTextBox.Clear();
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private void OnStateChanged(RuntimeState _) => RenderMainState();
    private void OnHealthChanged() => RenderMainState();

    private void OpenUrl(string url)
    {
        try
        {
            _logSink.Write("browser", $"打开页面：{url}");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logSink.Error("browser", ex);
            MessageBox.Show(ex.Message, "无法打开链接", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log.Error("open-directory", ex);
            MessageBox.Show(ex.Message, "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenConfig()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigPath))
                new AppConfig().Save();

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppPaths.ConfigPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log.Error("open-config", ex);
            MessageBox.Show(ex.Message, "无法打开 config.json", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
