using System.Runtime.InteropServices;
using System.Text.Json;
using HappyBot.App;
using HappyBot.App.Protocol;
using HappyBot.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HappyBot;

/// <summary>
/// Borderless window that hosts the WebView2 control deck. It owns window
/// chrome, hotkey registration and the timers; all behavior lives in
/// <see cref="DeckController"/>.
/// </summary>
public sealed class MainForm : Form, IUiSink, IUiDispatcher
{
    private static readonly (int Id, HotkeyAction Action)[] Hotkeys =
    {
        (1, HotkeyAction.OrangeDelayToggle),
        (3, HotkeyAction.CycleF),
        (4, HotkeyAction.CycleE),
        (5, HotkeyAction.OrangeParry),
        (6, HotkeyAction.Pause),
        (7, HotkeyAction.VisionOverlay)
    };
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    private readonly BotCore _bot = new();
    private readonly DeckController _controller;
    private readonly KeyboardHook _hook;
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
        var session = new SettingsSession(new ProfileEditorController(ProfileStore.ForApplication()), _bot);
        _controller = new DeckController(_bot, session, this, this, new StaticInputEnvironment(), _visionOverlay,
            new WavSoundCue(), new WinFormsSaveFilePicker(this), new InputTestRunner());

        var screen = Screen.PrimaryScreen.Bounds;
        _controller.Initialize(screen.Width, screen.Height);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            AllowExternalDrop = false
        };
        Controls.Add(_webView);

        _hook = new KeyboardHook(_controller.OnKey);
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
            string userDataFolder = Path.Combine(AppContext.BaseDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _webReady = true;
                _controller.Handle(new ReadyCommand());
            };

            string page = Path.Combine(AppContext.BaseDirectory, "ui", "index.html");
            _webView.CoreWebView2.Navigate(new Uri(page).AbsoluteUri);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "WebView2 could not start. Install the Microsoft Edge WebView2 Runtime and try again.\n\n" + ex.Message, "DANBOT UI Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
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

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        foreach ((int id, _) in Hotkeys)
        {
            // Ids match the function key number: 1 → F1 (VK 0x70), 3 → F3, ...
            Native.RegisterHotKey(Handle, id, 0, (uint)(0x70 + id - 1));
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        foreach ((int id, _) in Hotkeys)
        {
            Native.UnregisterHotKey(Handle, id);
        }
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statusTimer.Stop();
        _controllerTimer.Stop();
        _controller.Dispose();
        _bot.Dispose();
        _hook.Dispose();
        _visionOverlay.Dispose();
        _webView.Dispose();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            foreach ((int hotkeyId, HotkeyAction action) in Hotkeys)
            {
                if (hotkeyId == id)
                {
                    _controller.OnHotkey(action);
                    break;
                }
            }
            return;
        }
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
}
