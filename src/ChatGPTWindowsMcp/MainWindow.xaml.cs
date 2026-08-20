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
    private const string ChatGptConnectorsUrl = "https://chatgpt.com/#settings/Connectors";

    private readonly LogSink _logSink = new();
    private readonly RuntimeManager _runtime;
    private AppConfig _config;
    private int _wizardStepIndex;
    private string? _operationMessage;
    private bool _syncingApiKey;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;

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
            MessageBox.Show($"读取 config.json 失败：{ex.Message}", "配置错误", MessageBoxButton.OK, MessageBoxImage.Error);
            _config = new AppConfig();
        }

        _runtime = new RuntimeManager(_logSink);
        _logSink.LineReceived += OnLogLine;
        _runtime.StateChanged += OnStateChanged;
        _runtime.HealthChanged += OnHealthChanged;

        LoadAdvancedConfig();
        RenderMainState();

        Closing += MainWindowOnClosing;
    }

    private async void MainWindowOnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        e.Cancel = true;
        if (_shutdownInProgress)
            return;

        _shutdownInProgress = true;
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
            _logSink.Write($"退出清理时出现警告：{ex.Message}");
            _ = Task.Run(_runtime.ForceStop);
        }
        finally
        {
            _shutdownComplete = true;
            _shutdownInProgress = false;
            _ = Dispatcher.BeginInvoke(Close);
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
            Dispatcher.Invoke(RenderMainState);
            return;
        }

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

        switch (_runtime.State)
        {
            case RuntimeState.Running:
                StatusTitleText.Text = "● 已连接";
                StatusTitleText.Foreground = ResourceBrush("SuccessBrush", Brushes.ForestGreen);
                StatusDetailText.Text = "Windows MCP 已通过 OpenAI Tunnel 连接。";
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
                    ? "配置已保存，可以重试；详细错误可在高级选项中查看。"
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
                    ReconfigureButton.Visibility = Visibility.Collapsed;
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
        if (_runtime.State == RuntimeState.Running)
        {
            SetOperation("正在停止…");
            try
            {
                await _runtime.StopAsync();
            }
            catch (Exception ex)
            {
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

        LogTextBox.Clear();
        SetOperation("正在准备运行环境…");

        try
        {
            await _runtime.InstallDependenciesAsync(_config);
            SetOperation("正在连接 OpenAI Tunnel…");
            await _runtime.StartAsync(_config);

            if (_config.AutoOpenChatGptConnectors)
                OpenUrl(ChatGptConnectorsUrl);
        }
        catch (Exception ex)
        {
            _logSink.Write($"启动失败：{ex.Message}");
            AdvancedExpander.IsExpanded = true;
            MessageBox.Show(ex.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetOperation(null);
        }
    }

    private void SetOperation(string? message)
    {
        _operationMessage = message;
        RenderMainState();
    }

    private void ReconfigureButton_Click(object sender, RoutedEventArgs e) => ShowWizard(startFromBeginning: true);
    private void OpenChatGptButton_Click(object sender, RoutedEventArgs e) => OpenUrl(ChatGptConnectorsUrl);

    private void ShowWizard(bool startFromBeginning)
    {
        _wizardStepIndex = startFromBeginning
            ? 0
            : IsTunnelIdValid(_config.TunnelId) && string.IsNullOrWhiteSpace(_config.RuntimeApiKey) ? 1 : 0;

        TunnelIdTextBox.Text = _config.TunnelId;
        SetApiKeyControls(_config.RuntimeApiKey);
        WizardProxyTextBox.Text = _config.ControlPlaneHttpProxy;
        WizardAutoDetectProxyCheckBox.IsChecked = _config.AutoDetectSystemProxy;
        RenderWizardStep();
        MainScrollViewer.Visibility = Visibility.Collapsed;
        WizardRoot.Visibility = Visibility.Visible;
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
        TunnelStepPanel.Visibility = tunnelStep ? Visibility.Visible : Visibility.Collapsed;
        ApiStepPanel.Visibility = apiStep ? Visibility.Visible : Visibility.Collapsed;
        ProxyStepPanel.Visibility = proxyStep ? Visibility.Visible : Visibility.Collapsed;
        WizardStepCaption.Text = $"步骤 {_wizardStepIndex + 1} / 3";

        TunnelStepPill.Foreground = tunnelStep
            ? ResourceBrush("AccentBrush", Brushes.RoyalBlue)
            : ResourceBrush("SuccessBrush", Brushes.ForestGreen);
        TunnelStepPill.Text = tunnelStep ? "1  Tunnel ID" : "✓  Tunnel ID";
        ApiStepPill.Foreground = apiStep
            ? ResourceBrush("AccentBrush", Brushes.RoyalBlue)
            : proxyStep ? ResourceBrush("SuccessBrush", Brushes.ForestGreen) : ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
        ApiStepPill.Text = proxyStep ? "✓  API Key" : "2  API Key";
        ProxyStepPill.Foreground = proxyStep
            ? ResourceBrush("AccentBrush", Brushes.RoyalBlue)
            : ResourceBrush("TextSecondaryBrush", Brushes.DimGray);

        UpdateTunnelValidation();
        UpdateApiValidation();
        UpdateWizardProxyValidation();

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
            else
            {
                WizardProxyTextBox.Focus();
                WizardProxyTextBox.CaretIndex = WizardProxyTextBox.Text.Length;
            }
        });
    }

    private void WizardOpenTunnel_Click(object sender, RoutedEventArgs e) => OpenUrl(TunnelUrl);
    private void WizardOpenApiKey_Click(object sender, RoutedEventArgs e) => OpenUrl(RuntimeKeyUrl);

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

        _config.TunnelId = tunnelId;
        SaveConfig();
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
        if (_wizardStepIndex == 0)
        {
            var tunnelId = TunnelIdTextBox.Text.Trim();
            if (IsTunnelIdValid(tunnelId))
            {
                _config.TunnelId = tunnelId;
                SaveConfig();
            }
        }
        else if (_wizardStepIndex == 1)
        {
            SaveCurrentApiKeyIfPresent();
        }
        else
        {
            SaveWizardProxyIfValid();
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
        SaveConfig();
        _wizardStepIndex = 2;
        RenderWizardStep();
    }

    private void ProxyBackButton_Click(object sender, RoutedEventArgs e)
    {
        SaveWizardProxyIfValid();
        _wizardStepIndex = 1;
        RenderWizardStep();
    }

    private void WizardProxyTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateWizardProxyValidation();

    private void UpdateWizardProxyValidation()
    {
        if (WizardProxyValidationText is null || ProxyFinishButton is null)
            return;

        var proxy = WizardProxyTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(proxy))
        {
            WizardProxyValidationText.Text = "可留空。示例：http://127.0.0.1:7890";
            WizardProxyValidationText.Foreground = ResourceBrush("TextSecondaryBrush", Brushes.DimGray);
            ProxyFinishButton.IsEnabled = true;
        }
        else if (ProxyResolver.TryNormalizeProxyUrl(proxy, out _))
        {
            WizardProxyValidationText.Text = "✓ 代理地址格式正确";
            WizardProxyValidationText.Foreground = ResourceBrush("SuccessBrush", Brushes.ForestGreen);
            ProxyFinishButton.IsEnabled = true;
        }
        else
        {
            WizardProxyValidationText.Text = "代理地址必须以 http:// 或 https:// 开头。";
            WizardProxyValidationText.Foreground = ResourceBrush("DangerBrush", Brushes.Firebrick);
            ProxyFinishButton.IsEnabled = false;
        }
    }

    private void ProxyFinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveWizardProxyIfValid())
            return;

        ShowMainPage();
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
        SaveConfig();
        return true;
    }

    private void SaveCurrentApiKeyIfPresent()
    {
        var apiKey = CurrentApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        _config.RuntimeApiKey = apiKey;
        SaveConfig();
    }

    private void SaveConfig()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存配置失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
        AutoOpenCheckBox.IsChecked = _config.AutoOpenChatGptConnectors;
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

        _config.TunnelId = tunnelId;
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
        _config.AutoOpenChatGptConnectors = AutoOpenCheckBox.IsChecked == true;
        _config.AutoDownloadTunnelClient = AutoDownloadTunnelCheckBox.IsChecked == true;

        try
        {
            _config.Save();
            if (showSuccess)
                MessageBox.Show("高级选项已保存。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存 config.json 失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
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
        AutoOpenCheckBox.IsEnabled = enabled;
        AutoDownloadTunnelCheckBox.IsEnabled = enabled;
    }

    private void SaveAdvancedButton_Click(object sender, RoutedEventArgs e) => SaveAdvancedConfig(showSuccess: true);

    private async void DoctorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsConfigured())
        {
            MessageBox.Show("请先完成 Tunnel ID 和 Runtime API Key 配置。", "连接诊断", MessageBoxButton.OK, MessageBoxImage.Information);
            ShowWizard(startFromBeginning: false);
            return;
        }

        if (!SaveAdvancedConfig(showSuccess: false))
            return;

        LogTextBox.Clear();
        AdvancedExpander.IsExpanded = true;
        SetOperation("正在运行连接诊断…");

        try
        {
            await _runtime.InstallDependenciesAsync(_config);
            var exitCode = await _runtime.DoctorAsync(_config);
            MessageBox.Show(
                exitCode == 0 ? "诊断完成，未发现阻断性错误。" : $"诊断退出码：{exitCode}。请查看运行日志。",
                "连接诊断",
                MessageBoxButton.OK,
                exitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "诊断失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetOperation(null);
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenPath(AppPaths.LogsDirectory);
    private void OpenConfigButton_Click(object sender, RoutedEventArgs e) => OpenConfig();
    private void OpenConnectorButton_Click(object sender, RoutedEventArgs e) => OpenUrl(ChatGptConnectorsUrl);

    private void OnLogLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        AppendLog(line);
    }

    private void AppendLog(string line)
    {
        LogTextBox.AppendText(line + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private void OnStateChanged(RuntimeState _) => RenderMainState();
    private void OnHealthChanged() => RenderMainState();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
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
            MessageBox.Show(ex.Message, "无法打开 config.json", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
