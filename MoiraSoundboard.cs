using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;

[DataContract]
public partial class Settings
{
    [DataMember] public string AudioDirectory = "../永雏塔菲语音";
    [DataMember] public string[] WindowTitles = new string[] { "Overwatch", "守望先锋" };
    [DataMember] public int Volume = 70;
    [DataMember] public string CacheDirectory = "audio-cache";
    [DataMember] public int LoopCrossfadeMs = 50;
    [DataMember] public bool StartEnabled = true;
    [DataMember] public string[] WindowClasses = new string[] { "TankWindowClass" };
    [DataMember] public double OrbSelectionSeconds = 0;
    [DataMember] public int LeftKey = 1;
    [DataMember] public int RightKey = 2;
    [DataMember] public int FadeKey = 16;
    [DataMember] public int OrbKey = 69;
    [DataMember] public int UltimateKey = 81;
    [DataMember] public int PauseKey = 119;
    [DataMember] public int ChatKey = 13;
    [DataMember] public int CancelKey = 27;

    public int[] Keys { get { return InputKeys.Configured(this); } }
    public static Settings Load(string root)
    {
        string path = Path.Combine(root, "config.json");
        Settings value;
        using (FileStream stream = File.OpenRead(path))
            value = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
        if (value == null || String.IsNullOrWhiteSpace(value.AudioDirectory) || value.WindowTitles == null || value.WindowTitles.Length == 0)
            throw new Exception("config.json 缺少音频目录或游戏窗口标题。");
        foreach (string title in value.WindowTitles)
            if (String.IsNullOrWhiteSpace(title)) throw new Exception("游戏窗口标题不能留空。");
        value.ValidateRules();
        value.Rules = value.GetRules(); // Migrate old six-slot settings in memory; keep the original file until Save.
        if (value.Volume < 0 || value.Volume > 100 || Double.IsNaN(value.OrbSelectionSeconds) || Double.IsInfinity(value.OrbSelectionSeconds) || value.OrbSelectionSeconds < 0 || value.OrbSelectionSeconds > 60)
            throw new Exception("音量须为 0–100，球选择等待须为 0–60 秒，0 表示不超时。");
        if (String.IsNullOrWhiteSpace(value.CacheDirectory) || value.LoopCrossfadeMs < 5 || value.LoopCrossfadeMs > 100)
            throw new Exception("缓存目录不能为空，循环交叉淡化须为 5–100 毫秒。");
        return value;
    }
}

// Primary fire is held; abilities emit a single press event. No game state is read.
public class TriggerEngine
{
    readonly Settings settings;
    readonly List<SoundRule> rules;
    HashSet<int> previous = new HashSet<int>();
    readonly HashSet<int> selectedMouse = new HashSet<int>();
    bool wasInGame;
    int armedGate;
    public readonly List<string> Loops = new List<string>();
    public readonly List<string> OneShots = new List<string>();
    public string Once;
    public bool Enabled;
    public double OrbUntil;
    public string PauseReason = "";
    public TriggerEngine(Settings settings) { this.settings = settings; rules = settings.GetRules(); Enabled = settings.StartEnabled; }
    public void ClearHold() { Loops.Clear(); }
    public void Toggle() { Enabled = !Enabled; OrbUntil = 0; armedGate = 0; ClearHold(); selectedMouse.Clear(); PauseReason = Enabled ? "" : "手动暂停"; }
    bool Rising(HashSet<int> down, int key) { return down.Contains(key) && !previous.Contains(key); }
    public string Step(bool inGame, bool inPlayer, HashSet<int> down, double now)
    {
        Once = null; Loops.Clear(); OneShots.Clear(); selectedMouse.IntersectWith(down);
        if (!inGame || (inGame && !wasInGame)) { OrbUntil = 0; selectedMouse.Clear(); }
        if ((inGame || inPlayer) && Rising(down, settings.PauseKey)) Toggle();
        if (inGame && Enabled)
        {
            if (Rising(down, settings.ChatKey)) { Enabled = false; OrbUntil = 0; PauseReason = "聊天暂停，按 " + InputKeys.Name(settings.PauseKey) + " 恢复"; }
            else
            {
                if (now >= OrbUntil || Rising(down, settings.CancelKey)) OrbUntil = 0;
                HashSet<int> gates = new HashSet<int>();
                foreach (SoundRule rule in rules)
                    if (rule.Enabled && rule.Mode == "select") gates.Add(rule.GateKey);
                foreach (int gate in gates)
                    if (Rising(down, gate))
                    {
                        bool cancel = armedGate == gate && OrbUntil > now;
                        armedGate = gate;
                        OrbUntil = cancel ? 0 : (settings.OrbSelectionSeconds == 0 ? Double.PositiveInfinity : now + settings.OrbSelectionSeconds);
                        break;
                    }
                if (OrbUntil > now)
                {
                    foreach (SoundRule rule in rules)
                        if (rule.Enabled && rule.Mode == "select" && rule.GateKey == armedGate && Rising(down, rule.Key))
                        { OneShots.Add(rule.Id); selectedMouse.Add(rule.Key); OrbUntil = 0; break; }
                }
                foreach (SoundRule rule in rules)
                    if (rule.Enabled && rule.Mode == "once" && Rising(down, rule.Key) && !selectedMouse.Contains(rule.Key))
                    { OneShots.Add(rule.Id); OrbUntil = 0; }
                if (OrbUntil == 0)
                {
                    foreach (SoundRule rule in rules)
                        if (rule.Enabled && rule.Mode == "hold" && down.Contains(rule.Key) && !selectedMouse.Contains(rule.Key)) Loops.Add(rule.Id);
                }
            }
        }
        previous = new HashSet<int>(down); wasInGame = inGame;
        Once = OneShots.Count > 0 ? OneShots[0] : null;
        return Loops.Count > 0 ? Loops[0] : null;
    }
}

public sealed class PlaybackController
{
    readonly AudioBank audio;
    bool automatic;
    public string Status = "尚未播放。先用试听检查音频。";
    public PlaybackController(AudioBank audio) { this.audio = audio; }
    public void Stop() { audio.Stop(); automatic = false; Status = "已停止所有语音"; }
    public void Preview(string name) { audio.Play(name); Status = "试听混音：" + String.Join(" + ", audio.DisplayActiveNames()); }
    public void Update(string loop, string once, bool allowed, double now)
    { Update(loop == null ? new string[0] : new string[] { loop }, once == null ? new string[0] : new string[] { once }, allowed); }
    public void Update(IEnumerable<string> loops, IEnumerable<string> oneShots, bool allowed)
    {
        if (!allowed) { if (automatic) Stop(); return; }
        audio.SetLoops(loops);
        foreach (string name in oneShots) audio.Play(name);
        string[] active = audio.DisplayActiveNames();
        if (active.Length > 0) automatic = true;
        Status = active.Length > 0 ? "正在混音：" + String.Join(" + ", active) : "所有语音已播放完 / 已松开";
    }
}

public static class Native
{
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [StructLayout(LayoutKind.Sequential)]
    public struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputData", SetLastError = true)] public static extern uint RawSize(IntPtr handle, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputData", SetLastError = true)] public static extern uint RawData(IntPtr handle, uint command, [Out] byte[] data, ref uint size, uint headerSize);
}

public static class RawInput
{
    public static bool Register(IntPtr window, bool remove)
    {
        Native.RawDevice[] devices = new Native.RawDevice[2];
        devices[0].UsagePage = 1; devices[0].Usage = 2;
        devices[1].UsagePage = 1; devices[1].Usage = 6;
        for (int i = 0; i < 2; i++) { devices[i].Flags = remove ? 1u : 0x100u; devices[i].Target = remove ? IntPtr.Zero : window; }
        // INPUTSINK only. Never use NOLEGACY, CAPTUREMOUSE or a keyboard/mouse hook.
        return Native.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(Native.RawDevice)));
    }
    public static void Decode(byte[] data, int header, Action<int, bool> change)
    {
        if (data.Length < header + 8) return;
        uint type = BitConverter.ToUInt32(data, 0);
        if (type == 0)
        {
            ushort flags = BitConverter.ToUInt16(data, header + 4);
            int[] keys = { 1, 2, 4, 5, 6 };
            for (int i = 0; i < keys.Length; i++)
            {
                if ((flags & (1 << (i * 2))) != 0) change(keys[i], true);
                if ((flags & (2 << (i * 2))) != 0) change(keys[i], false);
            }
        }
        else if (type == 1)
        {
            int key = BitConverter.ToUInt16(data, header + 6);
            if (key > 0 && key < 255 && BitConverter.ToUInt16(data, header) != 0xff)
                change(key, (BitConverter.ToUInt16(data, header + 2) & 1) == 0);
        }
    }
}

public sealed class PlayerForm : Form
{
    Settings settings;
    TriggerEngine engine;
    readonly AudioBank audio;
    PlaybackController playback;
    readonly string root;
    readonly DataGridView soundGrid = new DataGridView();
    readonly Label help = new Label();
    readonly TrackBar volume = new TrackBar();
    bool configuring;
    readonly Timer poll = new Timer();
    readonly Label status = new Label();
    readonly Label last = new Label();
    readonly Button toggle = new Button();
    readonly Label diagnostics = new Label();
    readonly HashSet<int> rawDown = new HashSet<int>();
    readonly HashSet<int> rawSeen = new HashSet<int>();
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    bool rawRegistered;
    long gameRawPackets;
    long gameRawEdges;
    long gameAsyncSamples;
    long rawErrors;
    bool lastRawWasGame;
    string inputStatus = "原始输入尚未注册";
    readonly string ownPermission = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? "管理员权限" : "普通权限";
    string recentInput = "尚未检测到技能键";
    string windowStatus = "等待游戏窗口";
    public PlayerForm(Settings settings, string root)
    {
        this.settings = settings;
        this.root = root;
        engine = new TriggerEngine(settings);
        audio = new AudioBank(AudioLibrary.Load(settings,root), settings.LoopCrossfadeMs);
        playback = new PlaybackController(audio);
        try { audio.SetVolume(settings.Volume); }
        catch { audio.Dispose(); throw; }
        Text = "自定义语音播放器 v5";
        ClientSize = new Size(800, 700);
        Font = new Font("Microsoft YaHei UI", 10);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AddLabel("独立播放 · 原游戏语音仍会保留", 20, 18, 610, 32, 15);
        AddLabel("不改游戏文件、不访问游戏进程、不发送按键。\n非暴雪认证工具，无法保证零封号风险。", 20, 58, 610, 52, 10);
        status.SetBounds(20, 116, 760, 30); Controls.Add(status);
        toggle.Text = "启用自动播放"; toggle.SetBounds(20, 155, 190, 38);
        toggle.Click += delegate { engine.Toggle(); SafeStop(); UpdateStatus(false); };
        Controls.Add(toggle);
        Button stop = new Button(); stop.Text = "停止当前声音"; stop.SetBounds(230, 155, 170, 38);
        stop.Click += delegate { engine.Enabled = false; engine.OrbUntil = 0; engine.ClearHold(); engine.PauseReason = "停止并暂停"; SafeStop(); }; Controls.Add(stop);
        Button customize = new Button(); customize.Text = "按键和语音设置…"; customize.Name = "settings-open"; customize.SetBounds(430,155,200,38);
        customize.Click += delegate { EditSettings(); }; Controls.Add(customize);
        volume.Minimum = 0; volume.Maximum = 100; volume.Value = settings.Volume;
        volume.TickFrequency = 10; volume.SetBounds(90, 208, 300, 42);
        Label level = AddLabel("音量 " + volume.Value + "%", 410, 214, 180, 30, 10);
        AddLabel("音量", 20, 214, 65, 30, 10);
        volume.ValueChanged += delegate { try { audio.SetVolume(volume.Value); level.Text = "音量 " + volume.Value + "%"; } catch (Exception ex) { Fault(ex); } };
        Controls.Add(volume);
        soundGrid.Name="sounds-list"; soundGrid.SetBounds(20,260,760,190); soundGrid.ReadOnly=true; soundGrid.AllowUserToAddRows=false; soundGrid.AllowUserToDeleteRows=false;
        soundGrid.RowHeadersVisible=false; soundGrid.SelectionMode=DataGridViewSelectionMode.FullRowSelect; soundGrid.MultiSelect=false;
        soundGrid.Columns.Add("State","启用"); soundGrid.Columns[0].Width=55;
        soundGrid.Columns.Add("SoundName","语音名称"); soundGrid.Columns[1].Width=180;
        soundGrid.Columns.Add("Trigger","触发按键"); soundGrid.Columns[2].Width=230;
        soundGrid.Columns.Add("Mode","播放方式"); soundGrid.Columns[3].AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill; Controls.Add(soundGrid);
        Button listen=new Button(); listen.Text="试听所选语音"; listen.Name="sound-preview"; listen.SetBounds(585,206,195,38);
        listen.Click+=delegate { if(soundGrid.CurrentRow!=null) { SoundRule rule=(SoundRule)soundGrid.CurrentRow.Tag; if(rule.Enabled)Play(rule.Id,true);else last.Text="此条语音已停用，请在设置中启用后试听。"; } }; Controls.Add(listen);
        last.SetBounds(20,462,760,42); last.Text="尚未播放。先选择语音，再点试听。"; Controls.Add(last);
        diagnostics.SetBounds(20,510,760,94); diagnostics.Font=new Font("Microsoft YaHei UI",9); Controls.Add(diagnostics);
        help.SetBounds(20,614,760,76); help.Font=new Font("Microsoft YaHei UI",9); Controls.Add(help); FillSounds();
        poll.Interval = 15; poll.Tick += Poll; poll.Start();
        UpdateStatus(false);
    }
    void FillSounds()
    {
        soundGrid.Rows.Clear();
        foreach(SoundRule rule in settings.GetRules())
        { int row=soundGrid.Rows.Add(rule.Enabled?"是":"否",rule.Name,InputKeys.Trigger(rule),InputKeys.ModeName(rule.Mode)); soundGrid.Rows[row].Tag=rule; }
        help.Text=InputKeys.Name(settings.PauseKey)+" 暂停/恢复；"+InputKeys.Name(settings.ChatKey)+" 聊天暂停；"+InputKeys.Name(settings.CancelKey)+" 取消等待选择。\n按住循环：松开停止；点按一次：松开也播完；选择后点按：先前置键，再选择键。\n支持多语音混音；只在游戏前台自动播放；菜单、死亡、换英雄时请手动暂停。";
    }
    void EditSettings()
    {
        bool wasEnabled=engine.Enabled; configuring=true; poll.Stop(); playback.Stop();
        try
        {
            using(SettingsForm form=new SettingsForm(settings,root))
                if(form.ShowDialog(this)==DialogResult.OK)
                {
                    audio.Reload(form.Library,form.Result.LoopCrossfadeMs,form.Result.Volume); settings=form.Result;
                    engine=new TriggerEngine(settings); engine.Enabled=wasEnabled; playback=new PlaybackController(audio);
                    volume.Value=settings.Volume; FillSounds(); last.Text="按键和语音已保存，立即生效。";
                }
        }
        catch(Exception ex) { last.Text="设置未应用："+ex.Message; }
        finally { rawDown.Clear(); rawSeen.Clear(); configuring=false; poll.Start(); UpdateStatus(false); }
    }
    Label AddLabel(string text, int x, int y, int width, int height, float size)
    {
        Label label = new Label(); label.Text = text; label.SetBounds(x, y, width, height);
        label.Font = new Font("Microsoft YaHei UI", size); Controls.Add(label); return label;
    }
    void Fault(Exception error)
    {
        engine.Enabled = false; engine.OrbUntil = 0;
        last.Text = error.Message; UpdateStatus(false);
    }
    void SafeStop() { try { playback.Stop(); last.Text = playback.Status; } catch (Exception ex) { Fault(ex); } }
    void Play(string name, bool preview)
    {
        try { playback.Preview(name); last.Text = playback.Status; }
        catch (Exception ex) { Fault(ex); }
    }
    protected override void OnShown(EventArgs args)
    {
        base.OnShown(args); RegisterInput();
    }
    public bool RegisterInput()
    {
        rawRegistered = RawInput.Register(Handle, false);
        inputStatus = rawRegistered ? "原始输入已注册 + 按键查询备用" : "原始输入注册失败 " + Marshal.GetLastWin32Error() + "，使用按键查询";
        return rawRegistered;
    }
    bool GameWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || window == Handle) { windowStatus = "当前前台不是匹配的游戏窗口"; return false; }
        StringBuilder title = new StringBuilder(512), cls = new StringBuilder(256);
        Native.GetWindowText(window, title, title.Capacity); Native.GetClassName(window, cls, cls.Capacity);
        bool match = false;
        foreach (string item in settings.WindowTitles)
            if (title.ToString().IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
        if (settings.WindowClasses != null)
            foreach (string item in settings.WindowClasses)
                if (!String.IsNullOrWhiteSpace(item) && cls.ToString() == item) match = true;
        // Do not display titles from unrelated user windows.
        windowStatus = match ? "游戏窗口已匹配：" + title : "当前前台不是匹配的游戏窗口";
        return match;
    }
    void RawChange(int key, bool pressed)
    {
        if (Array.IndexOf(settings.Keys, key) < 0) return;
        bool changed = pressed ? rawDown.Add(key) : rawDown.Remove(key);
        rawSeen.Add(key);
        if (changed)
        {
            if (lastRawWasGame) gameRawEdges++;
            recentInput = (lastRawWasGame ? "最近游戏输入：" : "最近播放器输入：") + ((Keys)key).ToString() + (pressed ? " 按下" : " 松开");
        }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x00ff && settings != null && rawRegistered && !configuring)
        {
            IntPtr foreground = Native.GetForegroundWindow();
            bool game = GameWindow(foreground);
            if (foreground == Handle || game)
            {
                lastRawWasGame = game;
                if (game) gameRawPackets++;
                uint size = 0, header = (uint)(8 + IntPtr.Size * 2);
                if (Native.RawSize(message.LParam, 0x10000003, IntPtr.Zero, ref size, header) != UInt32.MaxValue && size >= header && size <= 4096)
                {
                    byte[] data = new byte[size];
                    if (Native.RawData(message.LParam, 0x10000003, data, ref size, header) != UInt32.MaxValue)
                    { RawInput.Decode(data, (int)header, RawChange); Poll(null, EventArgs.Empty); }
                    else rawErrors++;
                }
                else rawErrors++;
            }
        }
        base.WndProc(ref message);
    }
    void Poll(object sender, EventArgs args)
    {
        if(configuring)return;
        IntPtr foreground = Native.GetForegroundWindow();
        bool own = foreground == Handle;
        bool game = GameWindow(foreground);
        HashSet<int> down = new HashSet<int>();
        // Poll only these configured keys, and only in the game or this player's window.
        if (game || own)
            foreach (int key in settings.Keys)
            {
                bool asyncDown = Native.GetAsyncKeyState(key) < 0;
                if (game && asyncDown) gameAsyncSamples++;
                // Never let an old foreground-only raw key-up mask a working async query.
                if (rawDown.Contains(key) || asyncDown) down.Add(key);
            }
        if (!game && !own) { rawDown.Clear(); rawSeen.Clear(); }
        engine.Step(game, own, down, clock.Elapsed.TotalSeconds);
        try { playback.Update(engine.Loops, engine.OneShots, game && engine.Enabled); last.Text = playback.Status; }
        catch (Exception ex) { Fault(ex); }
        UpdateStatus(game);
    }
    void UpdateStatus(bool game)
    {
        toggle.Text = engine.Enabled ? "暂停自动播放" : "启用自动播放";
        status.Text = !engine.Enabled ? "● 已暂停：" + engine.PauseReason : (!game ? "● 已启用，等待游戏成为前台窗口" : (engine.OrbUntil > clock.Elapsed.TotalSeconds ? "● 已启用，等待选择键" : "● 已启用：按自定义规则播放"));
        status.ForeColor = engine.Enabled ? Color.DarkGreen : Color.DarkOrange;
        diagnostics.Text = ownPermission + " · " + inputStatus + "\n" + windowStatus + "\n" + recentInput +
            "\n游戏消息 " + gameRawPackets + " / 技能键事件 " + gameRawEdges + " / 按键查询 " + gameAsyncSamples + " / 解析错误 " + rawErrors + " / 音频断流 " + audio.Underruns;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { poll.Stop(); poll.Dispose(); if (rawRegistered) RawInput.Register(IntPtr.Zero, true); audio.Dispose(); }
        base.Dispose(disposing);
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        bool commandLine = args.Length > 0;
        try
        {
            Settings settings = Settings.Load(root);
            if (commandLine)
            {
                string report;
                if (args[0] == "--self-test") report = SelfTest();
                else if (args[0] == "--check-customizations") report = CustomizationTests.Run(root);
                else if (args[0] == "--check-editor") report = CustomizationTests.Editor(root, settings);
                else if (args[0] == "--render-ui")
                {
                    Application.EnableVisualStyles();
                    using(PlayerForm form=new PlayerForm(settings,root)) Render(form,Path.Combine(root,"preview-main.png"));
                    using(SettingsForm form=new SettingsForm(settings,root)) Render(form,Path.Combine(root,"preview-settings.png"));
                    using(KeyCaptureForm form=new KeyCaptureForm("设置触发键")) Render(form,Path.Combine(root,"preview-key.png"));
                    report="Own application windows rendered for layout inspection; no inputs sent.\n";
                }
                else if (args[0] == "--check-audio")
                {
                    StringBuilder text = new StringBuilder();
                    using (AudioBank bank = new AudioBank(Path.GetFullPath(Path.Combine(root, settings.CacheDirectory)), settings.LoopCrossfadeMs))
                    {
                        bank.SetVolume(0);
                        PlaybackController controller = new PlaybackController(bank);
                        foreach (string name in AudioBank.Names)
                        {
                            int length = bank.Length(name);
                            if (length <= 0) throw new Exception("Empty audio: " + name);
                            bool loop = name == "left" || name == "right";
                            controller.Update(loop ? name : null, loop ? null : name, true, 0);
                            PumpWait(80);
                            if (!loop)
                            {
                                controller.Update(null, null, true, 0.10);
                                if (bank.Mode(name) != "playing") throw new Exception("Tap release cut off audio: " + name);
                            }
                            if (loop)
                            {
                                System.Diagnostics.Stopwatch loopWait = System.Diagnostics.Stopwatch.StartNew();
                                int previousPosition = bank.Position(name); bool wrapped = false;
                                while (loopWait.ElapsedMilliseconds < length * 3 + 4000)
                                {
                                    PumpWait(30); int position = bank.Position(name);
                                    if (previousPosition - position > 100) { wrapped = true; break; }
                                    previousPosition = position;
                                }
                                if (!wrapped) throw new Exception("Loop did not rewind at end: " + name);
                            }
                            else WaitForAudioEnd(bank, name);
                            string mode = bank.Mode(name);
                            if (loop && mode != "playing") throw new Exception("Loop failed: " + name + " / " + mode);
                            if (!loop && mode == "playing") throw new Exception("One-shot incorrectly repeated: " + name);
                            controller.Update(null, null, true, length / 1000.0 + 10);
                            if (bank.Mode(name) == "playing") throw new Exception("Stop failed: " + name);
                            text.AppendLine(name + ".mp3: " + length + " ms, " + (loop ? "hold loops; release stops" : "tap plays fully after release; no repeat") + " OK");
                        }
                        controller.Update(new string[] { "left", "right" }, new string[] { "SHIFT", "E-right", "Q" }, true);
                        PumpWait(80);
                        foreach (string name in new string[] { "left", "right", "SHIFT", "E-right", "Q" })
                            if (bank.Mode(name) != "playing") throw new Exception("Concurrent voice missing: " + name);
                        controller.Update(new string[] { "right" }, new string[0], true);
                        if (bank.Mode("left") == "playing" || bank.Mode("right") != "playing" || bank.Mode("Q") != "playing")
                            throw new Exception("Primary release stopped unrelated voices.");
                        WaitForAudioEnd(bank, "SHIFT");
                        if (bank.Mode("right") != "playing" || bank.Mode("Q") != "playing") throw new Exception("Completed ability interrupted other voices.");
                        controller.Update(new string[0], new string[0], false);
                        foreach (string name in AudioBank.Names)
                            if (bank.Mode(name) == "playing") throw new Exception("Focus/pause did not stop all voices.");
                        PumpWait(80);
                        if (bank.CompletedBuffers < 100) throw new Exception("Windows PCM output made no progress.");
                        text.AppendLine("Five simultaneous voices, release isolation, one-shot completion, focus/pause stop all OK.");
                        text.AppendLine("Windows PCM output buffers completed: " + bank.CompletedBuffers + "; all device checks silent.");
                        text.AppendLine("Event-driven audio output underruns: " + bank.Underruns);
                        if (bank.Underruns != 0) throw new Exception("Audio output queue ran empty during check.");
                    }
                    report = text.ToString();
                }
                else if (args[0] == "--smoke-ui")
                {
                    Application.EnableVisualStyles();
                    using (PlayerForm form = new PlayerForm(settings, root))
                    {
                        // Hidden construction check: no keyboard polling, no audio playback.
                        if (form.Controls.Find("settings-open",true).Length!=1 || form.Controls.Find("sounds-list",true).Length!=1)
                            throw new Exception("UI controls missing.");
                        if (!form.RegisterInput()) throw new Exception("Raw input registration failed.");
                        using(SettingsForm editor = new SettingsForm(settings,root))
                        {
                            if(editor.Controls.Find("rules-grid",true).Length!=1 || editor.Controls.Find("rule-add",true).Length!=1)
                                throw new Exception("Custom settings controls missing.");
                        }
                    }
                    report = "UI construction/disposal and Windows Raw Input registration/removal OK.\n";
                }
                else throw new Exception("Unknown option.");
                File.WriteAllText(Path.Combine(root, "verification" + args[0].Replace("--", "-") + ".txt"), report, Encoding.UTF8);
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PlayerForm(settings, root)); return 0;
        }
        catch (Exception ex)
        {
            if (commandLine) File.WriteAllText(Path.Combine(root, "verification-error.txt"), ex.ToString(), Encoding.UTF8);
            else MessageBox.Show(ex.Message, "播放器无法启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception("Self-test: " + message); }
    static void Render(Form form,string path)
    {
        IntPtr handle=form.Handle; form.PerformLayout();
        using(Bitmap bitmap=new Bitmap(form.Width,form.Height))
        {
            form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));
            Point client=form.PointToScreen(Point.Empty), origin=form.Location;
            using(Graphics drawing=Graphics.FromImage(bitmap))
                foreach(Control control in form.Controls)
                    using(Bitmap part=new Bitmap(control.Width,control.Height))
                    {
                        control.DrawToBitmap(part,new Rectangle(0,0,part.Width,part.Height));
                        drawing.DrawImageUnscaled(part,client.X-origin.X+control.Left,client.Y-origin.Y+control.Top);
                    }
            bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    static void PumpWait(int milliseconds)
    {
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
    }
    static void WaitForAudioEnd(AudioBank bank, string name)
    {
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (bank.Mode(name) == "playing" && elapsed.ElapsedMilliseconds < bank.Length(name) + 5000) PumpWait(30);
        if (bank.Mode(name) == "playing") throw new Exception("One-shot did not end: " + name);
        if (bank.Position(name) < bank.Length(name) - 100) throw new Exception("One-shot stopped before clip end: " + name);
    }
    static HashSet<int> Down(params int[] keys) { return new HashSet<int>(keys); }
    static string SelfTest()
    {
        Settings settings = new Settings();
        TriggerEngine engine = new TriggerEngine(settings);
        Assert(engine.Enabled, "enabled on startup");
        Assert(engine.Step(true, false, Down(), 0) == null, "initial focus silent");
        Assert(engine.Step(true, false, Down(1), 0.1) == "left", "left begins");
        Assert(engine.Step(true, false, Down(1), 30) == "left", "left stays active while held");
        Assert(engine.Step(true, false, Down(), 30.1) == null, "left release stops");
        Assert(engine.Step(true, false, Down(2), 31) == "right", "right begins");
        Assert(engine.Step(true, false, Down(2), 32) == "right", "right hold loops");
        Assert(engine.Step(true, false, Down(), 32.1) == null, "right release stops");
        Assert(engine.Step(true, false, Down(69), 33) == null, "E alone silent");
        Assert(engine.Step(true, false, Down(), 34) == null && engine.OrbUntil > 34, "E release still awaits selection");
        Assert(engine.Step(true, false, Down(1), 60) == null && engine.Once == "E-left", "delayed healing selection emits one-shot");
        Assert(engine.Step(true, false, Down(1), 61) == null && engine.Once == null, "holding orb selection neither repeats nor plays primary");
        Assert(engine.Step(true, false, Down(), 62) == null && engine.Once == null, "orb release adds no playback event");
        Assert(engine.Step(true, false, Down(1), 63) == "left", "next press ordinary left");
        Assert(engine.Step(true, false, Down(69), 64) == null, "E interrupts hold");
        Assert(engine.Step(true, false, Down(2), 65) == null && engine.Once == "E-right", "damage orb one-shot");
        Assert(engine.Step(true, false, Down(2), 66) == null && engine.Once == null, "damage orb does not loop");
        Assert(engine.Step(true, false, Down(), 67) == null, "damage orb release");
        Assert(engine.Step(true, false, Down(16), 68) == null && engine.Once == "SHIFT", "fade one-shot");
        Assert(engine.Step(true, false, Down(16), 69) == null && engine.Once == null, "fade hold does not repeat");
        Assert(engine.Step(true, false, Down(81), 70) == null && engine.Once == "Q", "ultimate one-shot");
        Assert(engine.Step(true, false, Down(81), 80) == null && engine.Once == null, "ultimate hold does not repeat");
        Assert(engine.Step(true, false, Down(81, 13), 81) == null && !engine.Enabled, "chat pauses all holds");
        engine.Step(true, false, Down(), 82); engine.Step(true, false, Down(119), 83);
        Assert(engine.Enabled, "F8 resumes");
        engine.Step(true, false, Down(), 84);
        Assert(engine.Step(true, false, Down(69, 1), 85) == null && engine.Once == "E-left", "simultaneous E and selection");
        Assert(engine.Step(false, false, Down(1), 86) == null && engine.OrbUntil == 0, "focus loss stops");
        Assert(engine.Step(true, false, Down(1), 87) == "left", "held on refocus starts immediately");
        engine.Step(true, false, Down(), 88); engine.Step(true, false, Down(69), 89);
        engine.Step(true, false, Down(27), 90);
        Assert(engine.Step(true, false, Down(2), 91) == "right", "escape clears selection");
        engine.Step(true, false, Down(), 92); engine.Step(true, false, Down(119), 93);
        Assert(!engine.Enabled && engine.Step(true, false, Down(1), 94) == null, "pause stops held key");
        Assert(engine.Step(false, true, Down(1), 95) == null, "player controls never auto-play");
        TriggerEngine focusClick = new TriggerEngine(settings);
        Assert(focusClick.Step(true, false, Down(1), 0) == "left", "first click that focuses game plays");
        Assert(focusClick.Step(true, false, Down(1), 3) == "left", "first focus hold continues");
        Assert(focusClick.Step(true, false, Down(), 4) == null, "first focus hold release stops");
        focusClick.Step(true, false, Down(1), 5);
        focusClick.Step(true, false, Down(1, 81), 6);
        Assert(focusClick.Step(true, false, Down(1), 7) == "left", "held primary resumes after other skill released");
        TriggerEngine simultaneous = new TriggerEngine(settings);
        simultaneous.Step(true, false, Down(), 0);
        simultaneous.Step(true, false, Down(1, 2, 16, 81), 1);
        Assert(simultaneous.Loops.Count == 2 && simultaneous.Loops.Contains("left") && simultaneous.Loops.Contains("right"), "both mouse holds emitted together");
        Assert(simultaneous.OneShots.Count == 2 && simultaneous.OneShots.Contains("SHIFT") && simultaneous.OneShots.Contains("Q"), "simultaneous skill presses preserved");
        simultaneous.Step(true, false, Down(1, 2, 16, 81), 2);
        Assert(simultaneous.Loops.Count == 2 && simultaneous.OneShots.Count == 0, "skill holds never retrigger, mouse holds remain");
        simultaneous.Step(true, false, Down(2), 3);
        Assert(simultaneous.Loops.Count == 1 && simultaneous.Loops[0] == "right", "only released primary stops");
        Settings timed = new Settings(); timed.OrbSelectionSeconds = 3;
        TriggerEngine expiry = new TriggerEngine(timed); expiry.Step(true, false, Down(), 0);
        expiry.Step(true, false, Down(69), 1);
        Assert(expiry.Step(true, false, Down(1), 5) == "left", "optional selection timeout");
        for (int header = 16; header <= 24; header += 8)
        {
            byte[] mouse = new byte[header + 24]; mouse[header + 4] = 1;
            int detected = 0; bool pressed = false;
            RawInput.Decode(mouse, header, delegate(int key, bool state) { detected = key; pressed = state; });
            Assert(detected == 1 && pressed, "raw left down layout");
            mouse[header + 4] = 8;
            RawInput.Decode(mouse, header, delegate(int key, bool state) { detected = key; pressed = state; });
            Assert(detected == 2 && !pressed, "raw right up layout");
            byte[] keyboard = new byte[header + 16]; keyboard[0] = 1; keyboard[header + 6] = 69;
            RawInput.Decode(keyboard, header, delegate(int key, bool state) { detected = key; pressed = state; });
            Assert(detected == 69 && pressed, "raw keyboard make layout");
            keyboard[header + 2] = 1;
            RawInput.Decode(keyboard, header, delegate(int key, bool state) { detected = key; pressed = state; });
            Assert(detected == 69 && !pressed, "raw keyboard break layout");
        }
        MixerSelfTest();
        return "PASS: simultaneous mouse/skill events, primary hold/release, Shift/Q press-only, E selection one-shot, pause/focus, and x86/x64 raw input decoding.\nPASS: independent mixed sample contributions, repeat taps, isolated release, full one-shot completion, 8 ms stop fade, loop seam continuity across buffer boundaries, and peak limiting. No native input calls or game launched.\n";
    }
    static PcmClip ConstantClip(float left, float right, int frames)
    {
        float[] samples = new float[frames * 2];
        for (int i = 0; i < frames; i++) { samples[i * 2] = left; samples[i * 2 + 1] = right; }
        return new PcmClip(samples);
    }
    static void MixerSelfTest()
    {
        Dictionary<string, PcmClip> clips = new Dictionary<string, PcmClip>();
        clips.Add("left", ConstantClip(0.12f, 0, 4800));
        clips.Add("right", ConstantClip(0, 0.18f, 4800));
        clips.Add("SHIFT", ConstantClip(0.04f, 0.05f, 4800));
        PcmMixer mixer = new PcmMixer(clips, clips, 50); mixer.SetVolume(100);
        mixer.SetLoops(new string[] { "left", "right" }); mixer.Play("SHIFT", false); mixer.Play("SHIFT", false);
        float[] output = new float[960]; mixer.Render(output, 480);
        Assert(Math.Abs(output[900] - 0.20f) < 0.00001 && Math.Abs(output[901] - 0.28f) < 0.00001, "all four voices contribute to stereo output");
        Assert(mixer.ActiveNames().Length == 3 && String.Join(",", mixer.ActiveNames()).Contains("SHIFT ×2"), "repeat tap adds an independent voice");
        mixer.SetLoops(new string[] { "right" }); mixer.Render(output, 480);
        Assert(Math.Abs(output[958] - 0.08f) < 0.00001 && Math.Abs(output[959] - 0.28f) < 0.00001, "released left fades out without cutting abilities or right");
        mixer.Stop(); mixer.Render(output, 480);
        Assert(mixer.ActiveNames().Length == 0 && output[958] == 0 && output[959] == 0, "stop fades all sounds to silence");
        mixer.Play("SHIFT", false); mixer.Render(new float[10000], 5000);
        Assert(mixer.Mode("SHIFT") == "stopped" && mixer.Position("SHIFT") == 100, "tap reaches clip end exactly once");

        float[] rampClip = new float[12000];
        for (int i = 0; i < 6000; i++) rampClip[i * 2] = rampClip[i * 2 + 1] = 0.2f - 0.4f * i / 5999;
        Dictionary<string, PcmClip> seamClips = new Dictionary<string, PcmClip>(); seamClips.Add("left", new PcmClip(rampClip));
        PcmMixer seam = new PcmMixer(seamClips, seamClips, 50); seam.SetVolume(100); seam.Play("left", true);
        float[] continuous = new float[26000]; seam.Render(continuous, 13000);
        Assert(seam.Wraps == 2, "continuous looping progresses across multiple wraps");
        foreach (int boundary in new int[] { 6000, 11250 })
            Assert(Math.Abs(continuous[boundary * 2] - continuous[(boundary - 1) * 2]) < 0.001, "crossfade removes the raw 0.4-amplitude seam jump");
        PcmMixer chunks = new PcmMixer(seamClips, seamClips, 50); chunks.SetVolume(100); chunks.Play("left", true);
        int consumed = 0;
        while (consumed < 13000)
        {
            int count = Math.Min(333, 13000 - consumed); float[] block = new float[count * 2]; chunks.Render(block, count);
            for (int i = 0; i < block.Length; i++) Assert(Math.Abs(block[i] - continuous[consumed * 2 + i]) < 0.0000001, "chunk boundaries do not alter audio or insert gaps");
            consumed += count;
        }
        Dictionary<string, PcmClip> loud = new Dictionary<string, PcmClip>(); loud.Add("Q", ConstantClip(0.8f, 0.8f, 4800));
        PcmMixer limiter = new PcmMixer(loud, loud, 50); limiter.SetVolume(100);
        for (int i = 0; i < 6; i++) limiter.Play("Q", false);
        limiter.Render(output, 480);
        foreach (float sample in output) Assert(!Single.IsNaN(sample) && Math.Abs(sample) <= 0.94001, "mixed output avoids clipping");
        Assert(limiter.LimitedFrames > 0, "limiter actually engaged for overlapping loud clips");
    }
}
