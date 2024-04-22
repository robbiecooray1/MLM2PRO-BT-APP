﻿using Newtonsoft.Json;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Net;
using MLM2PRO_BT_APP.connections;
using MLM2PRO_BT_APP.devices;
using MLM2PRO_BT_APP.util;
using System.Net.Http;
using System.Text;

namespace MLM2PRO_BT_APP;
public partial class App
{
    public static SharedViewModel? SharedVm { get; private set; }
    private readonly ManualResetEvent _cleanupComplete = new ManualResetEvent(false);

    private readonly IBluetoothBaseInterface? _manager;
    private HttpPuttingServer? _puttingConnection;
    private readonly OpenConnectTcpClient _client;
    private OpenConnectServer? _openConnectServerInstance;
    private string? _lastMessage = "";
    private BluetoothScanner? _bluetoothScanner;
    private static readonly HttpClient client = new HttpClient();

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        SharedVm = new SharedViewModel();
        LoadSettings();
        _puttingConnection = new HttpPuttingServer();
        _client = new OpenConnectTcpClient();
        if (SettingsManager.Instance.Settings != null && SettingsManager.Instance.Settings.LaunchMonitor != null)
        {
            if (SettingsManager.Instance.Settings?.LaunchMonitor?.UseBackupManager ?? false)
            {
                _manager = new BluetoothManagerBackup();
            }
            else
            {
                _manager = new BluetoothManager();
            }
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            if (s != null)
            {
                Application_Exit(s, null);
                _cleanupComplete.WaitOne();
            }
        };
    }

    public byte[] GetEncryptedKeyFromHex(byte[]? input)
    {
        return _manager?.ConvertAuthRequest(input) ?? Array.Empty<byte>();
    }

    private static void CheckWebApiToken()
    {
        if (string.IsNullOrWhiteSpace(SettingsManager.Instance.Settings?.WebApiSettings?.WebApiSecret))
        {
            Logger.Log("Web api token is blank");
            if (SharedVm != null) SharedVm.LmStatus = "WEB API TOKEN NOT CONFIGURED";

            WebApiWindow webApiWindow = new()
            {
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            webApiWindow.ShowDialog();
        }
    }

    public async Task StartGsPro()
    {
        String executablePath = Path.GetFullPath(SettingsManager.Instance.Settings?.OpenConnect?.GsProExe ?? "C:\\GSProV1\\Core\\GSP\\GSPro.exe");
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
        if (processes.Length > 0)
        {
            Logger.Log("The GSPro application is already running.");
            return;
        } else if (!File.Exists(executablePath))
        {
            Logger.Log("The GSPro application does not exist.");
            return;
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = true
        };

        try
        {
            Process.Start(startInfo);
            Logger.Log("GSPro Started");
            if (SettingsManager.Instance.Settings?.OpenConnect?.SkipGsProLauncher ?? false)
            {
                await ClickButtonWhenWindowLoads("GSPro Configuration", "Play!");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error starting the GSPro process with arguments: {ex.Message}");
        }
    }
    private static async Task<bool> WaitForWindow(string windowTitle, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            AutomationElement? window = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, windowTitle));
            if (window != null)
            {
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }
    private static async Task ClickButtonWhenWindowLoads(string windowTitle, string buttonName)
    {
        Logger.Log("Application started, waiting for window...");
        bool windowLoaded = await WaitForWindow(windowTitle, TimeSpan.FromSeconds(120));
        if (windowLoaded)
        {
            var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, windowTitle));
            var button = window?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, buttonName));
            var invokePattern = button?.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
            invokePattern?.Invoke();
            Logger.Log($"{buttonName} button clicked in {windowTitle}");
        }
        else
        {
            Logger.Log("Window did not appear in time.");
        }
    }
    private async Task AutoConnectGsPro()
    {
        try
        {
            bool gsProOpenApiLoaded = await WaitForWindow("APIv1 Connect", TimeSpan.FromSeconds(120));
            if (gsProOpenApiLoaded && !_client.IsConnected)
            {
                Logger.Log("GSPro OpenAPI window loaded.");
                _client.ConnectAsync();
            }
            else
            {
                Logger.Log("GSPro OpenAPI window did not load in time.");
                if (SharedVm != null) SharedVm.GsProStatus = "NOT CONNECTED";
            }
        }
        catch (Exception ex)
        {
            Logger.Log("Exception in connecting: " + ex.Message);
        }
    }
    public void ConnectGsProButton()
    {
        if (!_client.IsConnected)
        {
            Logger.Log("Connecting to OpenConnect API.");
            _client.ConnectAsync();
        }
    }
    public void DisconnectGsPro()
    {
        try
        {
            // string? lmNotReadyJson = "{\"DeviceID\": \"GSPRO-MLM2PRO\",\"Units\": \"Yards\",\"ShotNumber\": 0,\"APIVersion\": \"1\",\"ShotDataOptions\": {\"ContainsBallData\": false,\"ContainsClubData\": false,\"LaunchMonitorIsReady\": false}}";
            // await _client.SendDirectJsonAsync(lmNotReadyJson);
            // await Task.Delay(2000);
            _client.DisconnectAndStop();
            Logger.Log("Disconnected from server.");
            if (SharedVm != null)SharedVm.GsProStatus = "DISCONNECTED";
            
        }
        catch (Exception ex)
        {
            Logger.Log($"Error disconnecting from server: {ex.Message}");
        }
    }
    public async Task SendTestShotData()
    {
        try
        {
            OpenConnectApiMessage messageSent = OpenConnectApiMessage.Instance.TestShot();
            await SendShotData(messageSent);
        }
        catch (Exception ex)
        {
            Logger.Log($"Error sending message: {ex.Message}");
        }
    }

    public async Task<bool> SendToApi(OpenConnectApiMessage? messageToSend)
    {
        var json = JsonConvert.SerializeObject(messageToSend);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("https://aw684qhrr8.execute-api.us-west-2.amazonaws.com/data", content);

        var responseText = await response.Content.ReadAsStringAsync();

        Logger.Log(responseText);

        return responseText != null;
    }

    public async Task SendShotData(OpenConnectApiMessage? messageToSend)
    {
        //bool dataSent = messageToSend != null; //&& await _client.SendDataAsync(messageToSend);
        bool dataSent = messageToSend != null && await SendToApi(messageToSend);
        try
        {
            string result;
            Logger.Log(messageToSend?.ToString());
            JsonConvert.SerializeObject(messageToSend);
            if (messageToSend is { BallData.Speed: 0 })
            {
                result = "Fail";
                await InsertRow(messageToSend, result);
                if (SharedVm != null) SharedVm.GsProStatus = "CONNECTED, LM MISREAD";
                return;
            }

            if (dataSent)
            {
                result = "Success";
                Logger.Log("message successfully sent!");
                if (messageToSend != null) await InsertRow(messageToSend, result);
                if (SharedVm != null) SharedVm.GsProStatus = "CONNECTED, SHOT SENT!";
            }
            else
            {
                Logger.Log($"Error sending message: Going to attempt a connection with GSPro");
                await AutoConnectGsPro();
                var dataSent2 = messageToSend != null && await _client.SendDataAsync(messageToSend);
                if (dataSent2)
                {
                    result = "Success";
                    Logger.Log("Second attempt worked!");
                    if (messageToSend != null) await InsertRow(messageToSend, result);
                    if (SharedVm != null) SharedVm.GsProStatus = "CONNECTED, SHOT SENT!";
                } else
                {
                    result = "Fail";
                    Logger.Log("Second attempt failed...");
                    if (messageToSend != null) await InsertRow(messageToSend, result);
                    if (SharedVm != null) SharedVm.GsProStatus = "DISCONNECTED, FAILED TO SEND SHOT";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error sending message: {ex.Message}");
        }
    }
    private static Task InsertRow(OpenConnectApiMessage inputData, string result)
    {
        HomeMenu.ShotData shotData = new()
        {
            ShotNumber = OpenConnectApiMessage.Instance.ShotNumber,
            Result = result,
            SmashFactor = MeasurementData.CalculateSmashFactor(inputData.BallData?.Speed ?? 0, inputData.ClubData?.Speed ?? 0),
            Club = DeviceManager.Instance?.ClubSelection ?? "",
            BallSpeed = inputData.BallData?.Speed ?? 0,
            SpinAxis = inputData.BallData?.SpinAxis ?? 0,
            SpinRate = inputData.BallData?.TotalSpin ?? 0,
            Vla = inputData.BallData?.Vla ?? 0,
            Hla = inputData.BallData?.Hla ?? 0,
            ClubSpeed = inputData.ClubData?.Speed ?? 0,
            BackSpin = inputData.BallData?.BackSpin ?? 0,
            SideSpin = inputData.BallData?.SideSpin ?? 0
            //ClubPath = 0,
            //ImpactAngle = 0
        };
        Current.Dispatcher.Invoke(() =>
        {
            SharedViewModel.Instance.ShotDataCollection.Insert(0, shotData);
        });
        return Task.CompletedTask;
    }
    public async Task ConnectAndSetupBluetooth()
    {
        if (SharedVm != null) SharedVm.LmStatus = "LOOKING FOR DEVICE";
        await (_manager?.RestartDeviceWatcher() ?? Task.CompletedTask);
    }
    public async Task LmArmDevice()
    {
        await (_manager?.ArmDevice() ?? Task.CompletedTask);
    }
    public async Task LmArmDeviceWithDelay()
    {
        await Task.Delay(1000);
        await LmArmDevice();
    }
    public async Task LmDisarmDevice()
    {
        await (_manager?.DisarmDevice() ?? Task.CompletedTask);

    }
    public async Task LmDisarmDeviceWithDelay()
    {
        await Task.Delay(1000);
        await LmDisarmDevice();
    }
    public void LmDisconnect()
    {
        if (!_manager?.IsBluetoothDeviceValid() ?? false) return;
        _ = _manager?.DisconnectAndCleanup();
    }
    public byte[]? GetBtKey()
    {
        return _manager?.GetEncryptionKey();
    }
    public void BtManagerReSub()
    {
        _ = _manager?.UnSubAndReSub();
    }
    public async Task PuttingEnable()
    {
        var fullPath = Path.GetFullPath(SettingsManager.Instance.Settings?.Putting?.ExePath ?? "");
        if (File.Exists(fullPath) && _puttingConnection != null)
        {
            Logger.Log("Putting executable exists.");
            var puttingStarted = _puttingConnection is { IsStarted: true };
            _puttingConnection.manualStopPutting = false;
            Logger.Log("Putting started: " + puttingStarted);
            if (puttingStarted == false)
            {
                Logger.Log("Starting putting server.");
                var isStarted = _puttingConnection.Start();
                if (isStarted != true) return;
                if (SharedVm != null) SharedVm.PuttingStatus = "CONNECTED";
                _puttingConnection.PuttingEnabled = true;
            } 
            else
            {
                if (SharedVm != null) SharedVm.PuttingStatus = "CONNECTED";
                _puttingConnection.PuttingEnabled = true;
                _puttingConnection.LaunchBallTracker = true;
            }
            if (DeviceManager.Instance?.ClubSelection == "PT" || !_puttingConnection.OnlyLaunchWhenPutting)
            {
                await Task.Delay(1000);
                StartPutting();
            }
        }
        else
        {
            Logger.Log("Putting executable missing.");
            if (SharedVm != null) SharedVm.PuttingStatus = "ball_tracking.exe missing";
        }
    }
    public void PuttingDisable()
    {
        if (_puttingConnection != null)
        {
            if (App.SharedVm != null) App.SharedVm.PuttingStatus = "SHUTDOWN";
            _puttingConnection.manualStopPutting = true;
            _puttingConnection.StopPutting(true);
            _puttingConnection.Stop();
        }
    }

    public void StartPutting()
    {
        _puttingConnection?.StartPutting();
    }

    public void StopPutting()
    {
        if (App.SharedVm != null) App.SharedVm.PuttingStatus = "DISCONNECTED";
        _puttingConnection?.StopPutting();
    }
    public void KillPutting()
    {
        _puttingConnection?.Dispose();
    }

    public async Task PuttingToggleAutoClose()
    {
        if (SettingsManager.Instance?.Settings?.Putting != null)
        {
            SettingsManager.Instance.Settings.Putting.OnlyLaunchWhenPutting = !SettingsManager.Instance.Settings.Putting.OnlyLaunchWhenPutting;
            SettingsManager.Instance.SaveSettings();
        }
        if (_puttingConnection != null)
        {
            _puttingConnection.StopPutting(true);
            _puttingConnection.Dispose();
        }
        _puttingConnection = new HttpPuttingServer();
        await PuttingEnable();
    }

    private static void LoadSettings()
    {
        SettingsManager.Instance.LoadSettings();
    }
    private void StartOpenConnectServer()
    {
        _openConnectServerInstance = new(IPAddress.Any, SettingsManager.Instance.Settings?.OpenConnect?.ApiRelayPort ?? 951);
        Logger.Log("OpenConnectServer: Starting server on port: " + SettingsManager.Instance.Settings?.OpenConnect?.ApiRelayPort);
        _openConnectServerInstance.Start();
    }
    public void StopOpenConnectServer()
    {
        _openConnectServerInstance?.Stop();
    }
    public async Task SendOpenConnectServerNewClientMessage()
    {
        if (!string.IsNullOrEmpty(_lastMessage))
        {
            await Task.Delay(1000);
            Logger.Log("OpenConnectServer: Sending message");
            Logger.Log(_lastMessage);
            Logger.Log("");
            _openConnectServerInstance?.Multicast(_lastMessage);
        }
    }
    public void SendOpenConnectServerMessage(string? incomingMessage)
    {
        if (!(_openConnectServerInstance?.IsStarted ?? false && !string.IsNullOrEmpty(incomingMessage))) return;
        Logger.Log("OpenConnectServer: Sending message");
        Logger.Log(incomingMessage);
        Logger.Log("");
        _openConnectServerInstance.Multicast(incomingMessage);
    }
    public async Task RelayOpenConnectServerMessage(string? outgoingMessage)
    {
        if (string.IsNullOrEmpty(outgoingMessage)) return;
        _lastMessage = outgoingMessage;
        Logger.Log("Relaying message to GSPro:");
        Logger.Log(outgoingMessage);
        Logger.Log("");
        var messageToRow = JsonConvert.DeserializeObject<OpenConnectApiMessage>(outgoingMessage);
        if (messageToRow != null) await InsertRow(messageToRow, "Relayed");
        await (_client.SendDirectJsonAsync(outgoingMessage) ?? Task.CompletedTask);
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow mainWindow = new();
        mainWindow.Loaded += MainWindow_Loaded;
        mainWindow.Show();
    }
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        CheckWebApiToken();

        if (SettingsManager.Instance.Settings?.Putting?.PuttingEnabled ?? false)
        {
            if (SettingsManager.Instance.Settings.Putting.AutoStartPutting)
            {
                await Task.Run(PuttingEnable);
            }
        }

        if (SettingsManager.Instance.Settings?.OpenConnect?.AutoStartGsPro ?? false)
        {
            
            await Task.Run(StartGsPro);
        }

        if (SettingsManager.Instance.Settings?.OpenConnect?.EnableApiRelay ?? false)
        {
            StartOpenConnectServer();
        }
        Logger.Log("Bluetooth Backup Manager is " + (SettingsManager.Instance.Settings?.LaunchMonitor?.UseBackupManager ?? false ? "enabled" : "disabled"));
        _bluetoothScanner = new BluetoothScanner(); // @TODO - ble scanner?
        await Task.Run(AutoConnectGsPro);
    }

    private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Logger.Log($"AppCrash: " + e.Exception);
    }

    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        Logger.Log($"AppCrash: " + exception);
    }

    private async void Application_Exit(object sender, ExitEventArgs? e)
    {
        await PerformCleanupAsync();
        await Task.Delay(200);
        _cleanupComplete.Set();
    }

    public async Task PerformCleanupAsync()
    {
        await Task.Run(KillPutting);
    }
}