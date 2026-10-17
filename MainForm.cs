using System.Runtime.InteropServices;
using System.Text.Json;
using HappyBot.App;
using HappyBot.App.Protocol;
using HappyBot.Cloud;
using HappyBot.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HappyBot;

/// <summary>
/// Borderless window that hosts the WebView2 control deck. It owns window
/// chrome and the timers; all behavior lives in
/// <see cref="DeckController"/>.
/// </summary>
public sealed class MainForm : Form, IUiSink, IUiDispatcher
{
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;
    private const string AppHost = "danbot.app.example";
    private const string AppOrigin = "https://" + AppHost + "/";

    private readonly BotCore _bot = new();
    private readonly DeckController _controller;
    private readonly WebView2 _webView;
    private readonly VisionOverlayForm _visionOverlay;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly System.Windows.Forms.Timer _controllerTimer;
    private bool _webReady;

    public MainForm()
    {
        Text = $"DANBOT Control Deck {BuildInfo.Display}";
        BackColor = Color.FromArgb(10, 11, 14);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 620);

        _visionOverlay = new VisionOverlayForm(_bot.GetVisionSnapshot, _bot.GetOverlayFeatures);
        ProfileStore profiles = ProfileStore.ForApplication();
        var session = new SettingsSession(new ProfileEditorController(profiles), _bot);
        var cloud = new CloudCoordinator(SupabaseRestApi.ForProject(), DpapiCloudSessionStore.ForApplication(), this,
            new ProfileStoreLocalProfiles(profiles), LicenseState.ForApplication());
        _controller = new DeckController(_bot, session, this, this, new StaticInputEnvironment(), _visionOverlay,
            new WavSoundCue(), new WinFormsSaveFilePicker(this), new InputTestRunner(), cloud);

        var screen = Screen.PrimaryScreen.Bounds;
        _controller.Initialize(screen.Width, screen.Height);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            AllowExternalDrop = false
        };
        Controls.Add(_webView);

        _statusTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _statusTimer.Tick += (_, _) => _controller.OnStatusTick();
        _statusTimer.Start();
        _controllerTimer = new System.Windows.Forms.Timer { Interval = 35 };
        _controllerTimer.Tick += (_, _) => _controller.OnControllerPollTick();
        _controllerTimer.Start();
        Shown += (_, _) => _ = InitializeWebViewAsync();
    }

    bool IUiSink.IsConnected => _webReady;

    void IUiSink.Post(UiMessage message)
    {
        if (InvokeRequired)
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => ((IUiSink)this).Post(message)));
            return;
        }
        if (!_webReady || _webView.CoreWebView2 == null || IsDisposed) return;
        _webView.CoreWebView2.PostWebMessageAsJson(UiJson.Serialize(message));
    }

    void IUiDispatcher.Post(Action action)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The form closed while background work was finishing.
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // Not next to the exe: a single-file build runs from a temporary unpack folder.
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userDataFolder = Path.Combine(string.IsNullOrWhiteSpace(appData) ? AppContext.BaseDirectory : appData,
                "DANBOT", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            CoreWebView2 core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.IsStatusBarEnabled = false;

            // Serve ui/ from a virtual https origin: ES module scripts do not load
            // over file://, and a real origin lets us pin navigation and messages.
            // A reserved .example name avoids the DNS stall .local triggers.
            core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "ui"),
                CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationCompleted += (_, _) =>
            {
                _webReady = true;
                _controller.Handle(new ReadyCommand());
            };

            // A rebuilt ui/ must never be served from a stale cache.
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            core.Navigate(AppOrigin + "index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 could not start. Install the Microsoft Edge WebView2 Runtime and try again.\n\n" + ex.Message, "DANBOT UI Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only the deck page may drive the bot.
        if (!e.Source.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) return;

        UiCommand command;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            command = UiCommandParser.Parse(document.RootElement);
        }
        catch (Exception ex)
        {
            ((IUiSink)this).Post(new ToastMessage("UI message failed: " + ex.Message, "error"));
            return;
        }

        switch (command)
        {
            case null:
                return;
            case ReadyCommand:
                _webReady = true;
                _controller.Handle(command);
                break;
            case WindowCommand window:
                HandleWindowCommand(window.Kind);
                break;
            default:
                _controller.Handle(command);
                break;
        }
    }

    private void HandleWindowCommand(WindowCommandKind kind)
    {
        switch (kind)
        {
            case WindowCommandKind.Reload:
                Application.Restart();
                break;
            case WindowCommandKind.Minimize:
                WindowState = FormWindowState.Minimized;
                break;
            case WindowCommandKind.Close:
                Close();
                break;
            case WindowCommandKind.Drag:
                ReleaseCapture();
                SendMessage(Handle, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
                break;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statusTimer.Stop();
        _controllerTimer.Stop();
        _controller.Dispose();
        _bot.Dispose();
        _visionOverlay.Dispose();
        _webView.Dispose();
        base.OnFormClosing(e);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
}
