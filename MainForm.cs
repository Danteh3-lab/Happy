using System.Media;

namespace HappyBot;

public sealed class MainForm : Form
{
    private const int IdF1 = 1, IdF2 = 2, IdF3 = 3, IdF4 = 4, IdF6 = 6;

    private static readonly string[] EditKeys = { "res1", "res2", "Pause", "Pause1", "Pause2", "Pause3", "Left", "Right" };

    private static readonly string[] CheckKeys =
    {
        "DodgeL", "DodgeH", "Leftdodge", "Rightdodge", "Unblockables", "Autoblock", "Lightbash",
        "Parry", "Crushing", "Deflect", "Parry2", "Crushing2", "Nohero", "YourHero", "Legit",
        "Warden", "Peacekeeper", "Centurion", "Blackprior", "Gryphon", "Conqueror", "Lawbringer", "Gladiator", "Warmonger",
        "Raider", "Berserker", "Highlander", "Jormungandr", "Warlord", "Valkyrie", "Shaman", "Varangian", "Null",
        "Kensei", "Orochi", "Shinobi", "Hitokiri", "Sohei", "Shugoki", "Nobushi", "Aramusha", "Kyoshin",
        "Tiandi", "Nuxia", "Zhanhu", "Jiangjun", "Shaolin", "Juren",
        "Pirate", "Afeera", "Medjay", "Khatun", "Ocelotl", "Virtuosa"
    };

    private readonly BotCore _bot = new();
    private readonly KeyboardHook _hook;
    private Button _startBtn;
    private Label _statusLabel;
    private CancellationTokenSource _testCts = new();
    private int _testMode;
    private bool _uiReady;
    private bool _shiftDown;
    private bool _rDown;
    private bool _fKeyDown;

    private static readonly (string Name, Action Press)[] Tests =
    {
        ("Dodge (A)", () => Input.KeyTap(Input.VK_SPACE)),
        ("Heavy (RT)", () => Input.MouseClick(Input.VK_RBUTTON)),
        ("Light (RB)", () => Input.MouseClick(Input.VK_LBUTTON)),
        ("Guard Break (X)", () => Input.KeyTap(Input.VK_NUMPAD5)),
        ("Guard Top", () => Input.KeyTap(Input.VK_NUMPAD8)),
        ("Guard Left", () => Input.KeyTap(Input.VK_NUMPAD4)),
        ("Guard Right", () => Input.KeyTap(Input.VK_NUMPAD6))
    };

    private static readonly Color EditBg = Color.FromArgb(0x39, 0x02, 0x02);

    private static readonly string HowToText =
@"IN GAME SETTINGS

1)In Display Settings:
-Set Field of View to 81 
-Set Contrast to 55 

2) In Graphics Settings:
- Disable Shadows
- Motion Blur 
- Ambient Occlusion
- Dynamic Reflections 
- Dynamic Shadows

3) Keymapping (bind all in secondary slots unless specified primary):
Movements: 
-Arrow Up = Forward 
-Arrow Down = Backward 
-Arrow Left = Left 
-Arrow Right = Right

Autoblokk: 
-Numpad 4 = Left Guard, 
-Numpad 6 = Right Guard, 
-Numpad 8 = Top Guard.

- Parie: Right Mouse Button.
- Counters: Numpad 9 (secondary) for Fast Attack and Full Guard.
- Other characters Full Guard: C (primary), 
- Guard Break = Numpad 5.

4) After all keybinds are ready:
- Select characters you are playing.
- Tick the menu features you want to use (one per section For [E]/[F])
- If you are using a working character and want their special features uses Your Character [F]
- Put your resolution where X and Y (e.g., X=1920, Y=1080). 
- Resolution in script GUI and in game should match.
- Press the small OK button, then press Start to run the script.

5) In-game:
- Features work automatically.
- For Parie, hold E or F before enemy starts an attack.
- If enemy does a fake heavy, release E or F every time to refresh parie/blokk direction.

6) Script works in any game mode with Borderless Windowed or Fullscreen.
- Not working in Windowed mode!
- Best on 1920x1080, FOV 81, FPS 120 (minimum), Monitor Hz 120 (minimum).

7) If you are having issues with leaving guard mode rebind sprint to another key.

8) This script was made for QWERTY keyboards. 
No i will not chnage the hotkeys for you.
No i will not add controller support. 

9) Discord for questions: VTB4

10) If you paid for this you have been scammed lmfao. ";

    private static readonly string ReadMeText =
@"Features:
-Auto Parie [F]/[E]
-Auto Counter [F]/[E]
-Auto Defleckt [F]
-Character Specific Evades/Parie
-Auto Evade 
-Auto Blokk
-Delays 

Working Characters:
-Werden Counter Top 
-Black Pryor Counter 
-Warlordd Full Guard
-Shamon Step Back
-Varangien Full Guard 
-Orochii Step Back
-Shinobii Stealth Stance
-Aramushar Full Guard
-Jiang Jan Stealth Stance 

Planned Updates:
-Improve Fake Counter
-Improve Defleckt
-Add Counter Guard Break
-Add Character Specific Atk After Evades
-Clean Up Code ✓
-Better Ui
-Add Evade To Left/Right
-Fix Old Hotkeys

Community Notes:
If you guys have anything you wanted 
to be added that isnt [removed]ed let me know.
This is a open source project.

If you paid for this you have been scammed lmfao. ";

    public MainForm()
    {
        Text = "HappyBot Rebuilt FREE";
        BackColor = Color.Black;
        ForeColor = Color.White;
        ClientSize = new Size(715, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft Sans Serif", 8.25f);

        BuildUi();
        var screen = Screen.PrimaryScreen.Bounds;
        SetText("res1", screen.Width.ToString());
        SetText("res2", screen.Height.ToString());
        ApplyResolution(screen.Width, screen.Height);
        _hook = new KeyboardHook(OnKey);

        _statusLabel = new Label
        {
            ForeColor = Color.Lime,
            Location = new Point(10, 445),
            Size = new Size(555, 20),
            Text = "Press Start to begin"
        };
        Controls.Add(_statusLabel);

        var scanBtn = new Button
        {
            Text = "Scan Screen",
            Location = new Point(570, 442),
            Size = new Size(85, 24)
        };
        scanBtn.Click += OnScan;
        Controls.Add(scanBtn);

        var testBtn = new Button
        {
            Text = "Test Input",
            Location = new Point(660, 442),
            Size = new Size(45, 24)
        };
        testBtn.Click += OnTestInput;
        Controls.Add(testBtn);

        var statusTimer = new System.Windows.Forms.Timer { Interval = 200 };
        statusTimer.Tick += (_, _) =>
        {
            _bot.FHeld = _fKeyDown || Input.HoldButtonHeld();
            UpdateStatus();
        };
        statusTimer.Start();
        _uiReady = true;
    }

    private void OnScan(object sender, EventArgs e)
    {
        ReadControlsToSettings();
        if (int.TryParse(_bot.S.Res1, out int w) && int.TryParse(_bot.S.Res2, out int h) && w > 0 && h > 0)
            ApplyResolution(w, h);
        MessageBox.Show(this, _bot.DebugScan(), "Screen Scan");
    }

    private void OnTestInput(object sender, EventArgs e)
    {
        (string name, Action press) = Tests[_testMode];
        _testMode = (_testMode + 1) % Tests.Length;
        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;
        MessageBox.Show(this, $"Test: {name} — will send 5 presses after 3 seconds.", "Test Input");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, ct);
                for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++)
                {
                    press();
                    Thread.Sleep(250);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void UpdateStatus()
    {
        if (!string.IsNullOrEmpty(_bot.LastError))
        {
            _statusLabel.Text = "Error: " + _bot.LastError;
            return;
        }

        if (_bot.IsRunning)
        {
            _statusLabel.Text =
                $"P1:{(_bot.S.Parry ? "ON" : "off")} P2:{(_bot.S.Parry2 ? "ON" : "off")} U:{(_bot.S.Unblockables ? "ON" : "off")} " +
                $"L:{(_bot.S.Legit ? "ON" : "off")} " +
                $"M:{(_bot.MarkerFound ? "FOUND" : "MISSING")} F:{(_bot.FHeld ? "DOWN" : "up")} " +
                $"Ind:{(_bot.AttackIndicator ? "YES" : "no")} G:{_bot.GuardDir} " +
                $"Fl:{(_bot.Flash ? "YES" : "no")} P:{_bot.ParryCount} S:{Input.InjectedCount} " +
                $"Inj:{Input.LastSendResult}/{Input.LastSendError} " +
                $"Interc:{(InterceptionInput.IsAvailable ? "ON" : "OFF")} V:{(ViGEmInput.IsAvailable ? "ON" : "OFF")} Src:{(ViGEmInput.SourceConnected ? "ON" : "OFF")} " +
                $"In:{Input.ActiveMode} " +
                $"Elev:{(Input.IsElevated() ? "yes" : "NO")} {_bot.LoopHz}Hz";
        }
        else
        {
            _statusLabel.Text = "Press Start to begin";
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        for (int i = 0; i < 6; i++)
        {
            if (i == 4) continue;
            Native.RegisterHotKey(Handle, i + 1, 0, (uint)(0x70 + i));
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        for (int i = 0; i < 6; i++)
        {
            if (i == 4) continue;
            Native.UnregisterHotKey(Handle, i + 1);
        }
        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _testCts?.Cancel();
        _bot.Stop();
        _hook?.Dispose();
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            HandleHotkey(m.WParam.ToInt32());
            return;
        }
        base.WndProc(ref m);
    }

    private void OnKey(int vk, bool down)
    {
        if (vk == Input.VK_E)
        {
            _bot.EHeld = down;
        }
        else if (vk == Input.VK_F)
        {
            _fKeyDown = down;
            _bot.FHeld = _fKeyDown || Input.HoldButtonHeld();
        }
        else if (vk == Input.VK_LSHIFT)
        {
            if (down && !_shiftDown && _bot.S.Active12 == 1)
                Input.KeyTap(Input.VK_NUMPAD3);
            _shiftDown = down;
        }
        else if (vk == Input.VK_R)
        {
            if (down && !_rDown)
            {
                _bot.S.Active1 = 0;
                _bot.ScheduleRele();
            }
            _rDown = down;
        }
    }

    private void HandleHotkey(int id)
    {
        var s = _bot.S;
        switch (id)
        {
            case IdF1:
                if (s.Pause == 0) { s.Pause = 80; Sound("buttonclick"); }
                else { s.Pause = 0; Sound("buttonunclick"); }
                break;
            case IdF2:
                if (s.Active1 == 1) { s.Active1 = 0; Sound("buttonunclick"); }
                else { s.Active1 = 1; Sound("buttonclick"); }
                break;
            case IdF3:
                if (s.Active3 == 1) { s.Active3 = 0; s.Active4 = 1; Sound("buttonclick"); }
                else if (s.Active4 == 1) { s.Active3 = 1; s.Active4 = 0; Sound("buttonclick"); }
                break;
            case IdF4:
                s.NMode++;
                if (s.NMode == 1) { s.Active9 = 1; s.Active11 = 0; s.Active12 = 0; Sound("buttonclick"); }
                else if (s.NMode == 2) { s.Active9 = 0; s.Active11 = 1; s.Active12 = 0; Sound("buttonclick"); }
                else if (s.NMode == 3) { s.Active9 = 0; s.Active11 = 0; s.Active12 = 1; Sound("buttonclick"); s.NMode = 0; }
                break;
            case IdF6:
                _bot.TogglePause();
                Sound("buttonclick");
                break;
        }
    }

    private static void Sound(string name)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, name + ".wav");
            if (File.Exists(path))
                using (var player = new SoundPlayer(path)) player.Play();
        }
        catch
        {
        }
    }

    private GroupBox AddGroup(string title, Color fore, int x, int y, int w, int h)
    {
        var g = new GroupBox
        {
            Text = title,
            ForeColor = fore,
            Location = new Point(x, y),
            Size = new Size(w, h)
        };
        Controls.Add(g);
        return g;
    }

    private CheckBox AddCheck(GroupBox parent, string name, string text, int x, int y, int w = 100)
    {
        var cb = new CheckBox
        {
            Name = name,
            Text = text,
            ForeColor = Color.White,
            Location = new Point(x - parent.Left, y - parent.Top),
            Size = new Size(w, 20)
        };
        cb.CheckedChanged += (_, _) =>
        {
            if (_uiReady) ReadControlsToSettings();
        };
        parent.Controls.Add(cb);
        return cb;
    }

    private TextBox AddEdit(GroupBox parent, string name, int x, int y, int w, int h)
    {
        var tb = new TextBox
        {
            Name = name,
            Text = "0",
            ForeColor = Color.White,
            BackColor = EditBg,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Center,
            Location = new Point(x - parent.Left, y - parent.Top),
            Size = new Size(w, h)
        };
        parent.Controls.Add(tb);
        return tb;
    }

    private Label AddText(GroupBox parent, string text, int x, int y, int w, int h, Color? fore = null)
    {
        var l = new Label
        {
            Text = text,
            ForeColor = fore ?? Color.White,
            Location = new Point(x - parent.Left, y - parent.Top),
            Size = new Size(w, h)
        };
        parent.Controls.Add(l);
        return l;
    }

    private Button AddButton(GroupBox parent, string name, string text, int x, int y, int w, int h, EventHandler onClick, Color? fore = null)
    {
        var b = new Button
        {
            Name = name,
            Text = text,
            ForeColor = fore ?? Color.White,
            Location = new Point(x - parent.Left, y - parent.Top),
            Size = new Size(w, h)
        };
        b.Click += onClick;
        parent.Controls.Add(b);
        return b;
    }

    private void BuildUi()
    {
        var gold = AddGroup("Knights", Color.FromArgb(0xF3, 0xBB, 0x3E), 10, 10, 115, 210);
        AddCheck(gold, "Warden", "Warden", 20, 30);
        AddCheck(gold, "Peacekeeper", "Peacekeeper", 20, 50);
        AddCheck(gold, "Centurion", "Centurion", 20, 70, 90);
        AddCheck(gold, "Blackprior", "Black Prior", 20, 90, 90);
        AddCheck(gold, "Gryphon", "Gryphon", 20, 110);
        AddCheck(gold, "Conqueror", "Conqueror", 20, 130);
        AddCheck(gold, "Lawbringer", "Lawbringer", 20, 150);
        AddCheck(gold, "Gladiator", "Gladiator", 20, 170);
        AddCheck(gold, "Warmonger", "Warmonger", 20, 190);

        var red = AddGroup("Vikings", Color.Red, 130, 10, 115, 210);
        AddCheck(red, "Raider", "Raider", 140, 30);
        AddCheck(red, "Berserker", "Berserker", 140, 50);
        AddCheck(red, "Highlander", "Highlander", 140, 70, 90);
        AddCheck(red, "Jormungandr", "Jormungandr", 140, 90);
        AddCheck(red, "Warlord", "Warlord", 140, 110);
        AddCheck(red, "Valkyrie", "Valkyrie", 140, 130);
        AddCheck(red, "Shaman", "Shaman", 140, 150);
        AddCheck(red, "Varangian", "Varangian", 140, 170);
        AddCheck(red, "Null", "Null", 140, 190);

        var teal = AddGroup("Samurai", Color.FromArgb(0x16, 0xAF, 0xA7), 250, 10, 115, 210);
        AddCheck(teal, "Kensei", "Kensei", 260, 30);
        AddCheck(teal, "Orochi", "Orochi", 260, 50);
        AddCheck(teal, "Shinobi", "Shinobi", 260, 70, 80);
        AddCheck(teal, "Hitokiri", "Hitokiri", 260, 90);
        AddCheck(teal, "Sohei", "Sohei", 260, 110);
        AddCheck(teal, "Shugoki", "Shugoki", 260, 130);
        AddCheck(teal, "Nobushi", "Nobushi", 260, 150);
        AddCheck(teal, "Aramusha", "Aramusha", 260, 170);
        AddCheck(teal, "Kyoshin", "Kyoshin", 260, 190);

        var purple = AddGroup("Wu Lin", Color.FromArgb(0xC0, 0x80, 0xFF), 370, 10, 85, 150);
        AddCheck(purple, "Tiandi", "Tiandi", 380, 30, 60);
        AddCheck(purple, "Nuxia", "Nuxia", 380, 50, 60);
        AddCheck(purple, "Zhanhu", "Zhanhu", 380, 70, 60);
        AddCheck(purple, "Jiangjun", "Jiang Jun", 380, 90, 70);
        AddCheck(purple, "Shaolin", "Shaolin", 380, 110, 60);
        AddCheck(purple, "Juren", "Juren", 380, 130, 60);

        var blue = AddGroup("Outlanders", Color.FromArgb(0x33, 0xA8, 0xD0), 460, 10, 80, 150);
        AddCheck(blue, "Pirate", "Pirate", 470, 30, 60);
        AddCheck(blue, "Afeera", "Afeera", 470, 50, 60);
        AddCheck(blue, "Medjay", "Medjay", 470, 70, 60);
        AddCheck(blue, "Khatun", "Khatun", 470, 90, 60);
        AddCheck(blue, "Ocelotl", "Ocelotl", 470, 110, 60);
        AddCheck(blue, "Virtuosa", "Virtuosa", 470, 130, 60);

        var fBtn = AddGroup("For Button - [F]", Color.White, 10, 220, 170, 155);
        AddCheck(fBtn, "Parry", "Auto Parry", 20, 240, 110);
        AddCheck(fBtn, "Crushing", "Auto CC", 20, 260, 110);
        AddCheck(fBtn, "Deflect", "Auto Deflect", 20, 280, 110);
        AddCheck(fBtn, "Nohero", "No Hero", 20, 300, 135);
        AddCheck(fBtn, "YourHero", "Your Hero", 20, 320, 135);
        AddCheck(fBtn, "Legit", "Legit Mode", 20, 345, 135);

        var auto = AddGroup("Auto features", Color.White, 185, 220, 180, 125);
        AddCheck(auto, "Unblockables", "Dodge Bashes/Unblockables", 195, 240, 170);
        AddCheck(auto, "Autoblock", "Auto Block", 195, 260, 110);
        AddCheck(auto, "Lightbash", "Bash After Dodge", 195, 280, 110);
        AddCheck(auto, "DodgeH", "RMouse after dodge", 195, 300, 160);
        AddCheck(auto, "DodgeL", "LMouse after dodge", 195, 320, 160);

        var eBtn = AddGroup("For Button - [E]", Color.White, 10, 385, 170, 60);
        AddCheck(eBtn, "Parry2", "Auto Parry", 20, 402, 110);
        AddCheck(eBtn, "Crushing2", "Auto CC", 20, 422, 110);

        var credits = AddGroup("Credits", Color.White, 185, 345, 180, 60);
        AddText(credits, "Developed by FlorasSecret", 195, 365, 170, 20, Color.Cyan);
        AddText(credits, "Edited by VTB4", 195, 385, 170, 20, Color.Cyan);

        var res = AddGroup("Resolution", Color.White, 370, 160, 170, 60);
        AddText(res, "X:", 375, 185, 18, 20);
        AddEdit(res, "res1", 395, 183, 45, 20);
        AddText(res, "Y:", 444, 185, 18, 20);
        AddEdit(res, "res2", 462, 183, 45, 20);
        AddButton(res, "ButtonOK", "OK", 511, 183, 25, 20, OnOk);

        var delays = AddGroup("Delays", Color.White, 370, 220, 170, 185);
        AddText(delays, "Evade delay (ms):", 380, 240, 105, 20);
        AddEdit(delays, "Pause", 492, 240, 40, 18);
        AddText(delays, "Fake check delay:", 380, 260, 105, 20);
        AddEdit(delays, "Pause1", 492, 260, 40, 18);
        AddText(delays, "Block delay (ms):", 380, 280, 105, 20);
        AddEdit(delays, "Pause3", 492, 280, 40, 18);
        AddText(delays, "L/R mouse delay:", 380, 300, 105, 20);
        AddEdit(delays, "Pause2", 492, 300, 40, 18);
        AddText(delays, "Left deflect delay:", 380, 320, 105, 20);
        AddEdit(delays, "Left", 492, 320, 40, 18);
        AddText(delays, "Right deflect delay:", 380, 340, 105, 20);
        AddEdit(delays, "Right", 492, 340, 40, 18);
        AddCheck(delays, "Leftdodge", "Dodge to the left", 380, 360, 150);
        AddCheck(delays, "Rightdodge", "Dodge to the right", 380, 380, 150);

        var hotkeys = AddGroup("Hotkeys", Color.White, 545, 10, 160, 150);
        AddText(hotkeys, "[Pause/Resume]  - F6", 555, 30, 150, 20);
        AddText(hotkeys, "[Dodges On/Off]  - F2", 555, 50, 150, 20);
        AddText(hotkeys, "[Orange Dodge] - tick Dodge", 555, 70, 160, 20);
        AddText(hotkeys, "[Parry/Button 1]  - E", 555, 90, 150, 20);
        AddText(hotkeys, "[Parry/Button 2]  - F", 555, 110, 150, 20);
        AddText(hotkeys, "[Flip/Button]  - W", 555, 130, 150, 20);

        var help = AddGroup("Help and Settings", Color.White, 545, 160, 160, 112);
        AddButton(help, "ButtonH1", "How to Use", 555, 178, 140, 19, (_, _) => MessageBox.Show(this, HowToText, "How To Use?"));
        AddButton(help, "ButtonH2", "Load Settings", 555, 200, 140, 19, (_, _) => OnLoad());
        AddButton(help, "ButtonH3", "Save Settings", 555, 222, 140, 19, (_, _) => OnSave());
        AddButton(help, "ButtonH4", "Apply Settings", 555, 244, 140, 19, (_, _) => OnApply());

        var controls = AddGroup("Controls", Color.White, 545, 275, 160, 80);
        AddButton(controls, "ButtonH7", "Reload", 555, 298, 140, 20, (_, _) => Application.Restart());
        _startBtn = AddButton(controls, "ButtonH5", "Start", 555, 321, 140, 20, (s, e) => OnStart(s, e));

        var readme = AddGroup("[removed]", Color.Red, 545, 360, 160, 45);
        AddButton(readme, "ButtonH8", "READ ME", 555, 380, 140, 20, (_, _) => MessageBox.Show(this, ReadMeText, "Updates/Features"), Color.Red);
    }

    private Control Get(string name) => Controls.Find(name, true)[0];

    private string GetText(string name) => ((TextBox)Get(name)).Text;

    private void SetText(string name, string value) => ((TextBox)Get(name)).Text = value;

    private bool GetCheck(string name) => ((CheckBox)Get(name)).Checked;

    private void SetCheck(string name, bool value) => ((CheckBox)Get(name)).Checked = value;

    private static int ToInt(string value) => int.TryParse(value, out int n) ? n : 0;

    private void ReadControlsToSettings()
    {
        var s = _bot.S;
        s.Res1 = GetText("res1");
        s.Res2 = GetText("res2");
        s.Pause = ToInt(GetText("Pause"));
        s.Pause1 = ToInt(GetText("Pause1"));
        s.Pause2 = ToInt(GetText("Pause2"));
        s.Pause3 = ToInt(GetText("Pause3"));
        s.Left = ToInt(GetText("Left"));
        s.Right = ToInt(GetText("Right"));
        s.DodgeH = GetCheck("DodgeH");
        s.DodgeL = GetCheck("DodgeL");
        s.Leftdodge = GetCheck("Leftdodge");
        s.Rightdodge = GetCheck("Rightdodge");
        s.Unblockables = GetCheck("Unblockables");
        s.Autoblock = GetCheck("Autoblock");
        s.Lightbash = GetCheck("Lightbash");
        s.Parry = GetCheck("Parry");
        s.Crushing = GetCheck("Crushing");
        s.Deflect = GetCheck("Deflect");
        s.Parry2 = GetCheck("Parry2");
        s.Crushing2 = GetCheck("Crushing2");
        s.Nohero = GetCheck("Nohero");
        s.YourHero = GetCheck("YourHero");
        s.Legit = GetCheck("Legit");
        foreach (string key in CheckKeys)
            s.Chars[key] = GetCheck(key);
    }

    private void ApplyResolution(int w, int h)
    {
        _bot.B55 = w / 1920.0;
        _bot.Y55 = h / 1080.0;
        _bot.X8 = (w / 1920.0) * 860;
        _bot.Y8 = (h / 1080.0) * 80;
        _bot.X9 = (w / 1920.0) * 1075;
        _bot.Y9 = (h / 1080.0) * 425;
        _bot.X18 = (w / 1920.0) * 670;
        _bot.Y18 = (h / 1080.0) * 300;
        _bot.X19 = (w / 1920.0) * 820;
        _bot.Y19 = (h / 1080.0) * 510;
    }

    private void OnOk(object sender, EventArgs e)
    {
        string r1 = GetText("res1").Trim();
        string r2 = GetText("res2").Trim();
        if (r1 == "0" || r2 == "0" || r1.Length == 0 || r2.Length == 0 ||
            !int.TryParse(r1, out int w) || !int.TryParse(r2, out int h) || w < h)
        {
            MessageBox.Show(this, $"Resolution can not be {r1} x {r2}", "ERROR");
            return;
        }
        ApplyResolution(w, h);
        MessageBox.Show(this, $"New Resolution {r1} x {r2}", "Resolution Updated");
    }

    private void OnStart(object sender, EventArgs e)
    {
        ReadControlsToSettings();
        if (!int.TryParse(_bot.S.Res1, out int w) || !int.TryParse(_bot.S.Res2, out int h) ||
            w == 0 || h == 0 || w < h)
        {
            MessageBox.Show(this, "Invalid resolution. Set it in the Resolution box and press OK first.", "ERROR");
            return;
        }
        ApplyResolution(w, h);
        SystemSounds.Beep.Play();
        _bot.Start();
        _startBtn.Enabled = false;
    }

    private void OnApply()
    {
        ReadControlsToSettings();
        MessageBox.Show(this, "Settings successfully updated!", "Successful");
    }

    private void OnLoad()
    {
        foreach (string key in EditKeys)
            SetText(key, Config.Read(key));
        foreach (string key in CheckKeys)
            SetCheck(key, Config.Read(key) == "1");
        MessageBox.Show(this, "Settings successfully loaded!", "Successful");
    }

    private void OnSave()
    {
        ReadControlsToSettings();
        foreach (string key in EditKeys)
            Config.Write(key, GetText(key));
        foreach (string key in CheckKeys)
            Config.Write(key, GetCheck(key) ? "1" : "0");
        MessageBox.Show(this, "Settings successfully saved!", "Successful");
    }
}
