using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;

[DataContract]
public sealed class SoundRule
{
    [DataMember] public string Id;
    [DataMember] public string Name;
    [DataMember] public bool Enabled = true;
    [DataMember] public int Key;
    [DataMember] public string Mode = "once";
    [DataMember] public int GateKey;
    [DataMember] public string AudioFile = "";
}

public partial class Settings
{
    [DataMember] public List<SoundRule> Rules;
    [OnDeserializing]
    void Defaults(StreamingContext context)
    {
        AudioDirectory = "../永雏塔菲语音"; CacheDirectory = "audio-cache"; LoopCrossfadeMs = 50;
        Volume = 70; StartEnabled = true; WindowTitles = new string[] { "Overwatch", "守望先锋" };
        WindowClasses = new string[] { "TankWindowClass" }; LeftKey = 1; RightKey = 2; FadeKey = 16;
        OrbKey = 69; UltimateKey = 81; PauseKey = 119; ChatKey = 13; CancelKey = 27;
    }
    public List<SoundRule> GetRules()
    {
        if (Rules != null) return Rules;
        return new List<SoundRule> {
            new SoundRule { Id="left", Name="治疗", Key=LeftKey, Mode="hold" },
            new SoundRule { Id="right", Name="伤害", Key=RightKey, Mode="hold" },
            new SoundRule { Id="SHIFT", Name="消散", Key=FadeKey },
            new SoundRule { Id="E-left", Name="治疗球", Key=LeftKey, Mode="select", GateKey=OrbKey },
            new SoundRule { Id="E-right", Name="伤害球", Key=RightKey, Mode="select", GateKey=OrbKey },
            new SoundRule { Id="Q", Name="聚合射线", Key=UltimateKey }
        };
    }
    public Settings Copy()
    {
        using (MemoryStream stream = new MemoryStream())
        {
            DataContractJsonSerializer codec = new DataContractJsonSerializer(typeof(Settings));
            codec.WriteObject(stream, this); stream.Position = 0;
            Settings copy = (Settings)codec.ReadObject(stream); copy.Rules = copy.GetRules(); return copy;
        }
    }
    public void ValidateRules()
    {
        HashSet<int> controls = new HashSet<int>();
        foreach (int key in new int[] { PauseKey, ChatKey, CancelKey })
            if (key < 1 || key > 254 || !controls.Add(key)) throw new Exception("暂停、聊天、取消三个控制键须有效且互不重复。");
        HashSet<string> ids = new HashSet<string>(); HashSet<int> direct = new HashSet<int>();
        HashSet<int> gates = new HashSet<int>(); HashSet<string> choices = new HashSet<string>();
        foreach (SoundRule rule in GetRules())
        {
            if (rule == null || String.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id)) throw new Exception("语音条目编号无效或重复。");
            if (!rule.Enabled) continue;
            if (String.IsNullOrWhiteSpace(rule.Name)) throw new Exception("请为启用的语音填写名称。");
            if (rule.Key < 1 || rule.Key > 254 || controls.Contains(rule.Key)) throw new Exception(rule.Name + "：触发键未设置，或与控制键冲突。");
            if (rule.Mode == "select")
            {
                if (rule.GateKey < 1 || rule.GateKey > 254 || rule.GateKey == rule.Key || controls.Contains(rule.GateKey))
                    throw new Exception(rule.Name + "：请设置一个有效且不同于选择键的前置键。");
                if (!choices.Add(rule.GateKey + ":" + rule.Key)) throw new Exception(rule.Name + "：前置键和选择键组合重复。");
                gates.Add(rule.GateKey);
            }
            else if (rule.Mode == "hold" || rule.Mode == "once")
            {
                if (!direct.Add(rule.Key)) throw new Exception(rule.Name + "：与另一条直接触发语音使用了相同按键。");
            }
            else throw new Exception(rule.Name + "：播放方式无效。");
        }
        foreach (int gate in gates)
            if (direct.Contains(gate)) throw new Exception("前置键 " + InputKeys.Name(gate) + " 不能同时用于直接触发语音。");
        foreach (SoundRule rule in GetRules())
            if (rule.Enabled && rule.Mode == "select" && gates.Contains(rule.Key)) throw new Exception(rule.Name + "：选择键不能与任何前置键相同。");
    }
    public void Save(string root)
    {
        ValidateRules();
        string path = Path.Combine(root, "config.json"), temp = Path.Combine(root, "config-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, this);
            if (File.Exists(path)) File.Replace(temp, path, Path.Combine(root, "config.previous.json"));
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class InputKeys
{
    public static string Name(int key)
    {
        switch (key)
        {
            case 0: return "未设置"; case 1: return "鼠标左键"; case 2: return "鼠标右键";
            case 4: return "鼠标中键"; case 5: return "鼠标侧键1"; case 6: return "鼠标侧键2";
            case 16: return "Shift"; case 17: return "Ctrl"; case 18: return "Alt";
            case 13: return "Enter"; case 27: return "Esc"; case 32: return "空格";
            default: return ((Keys)key).ToString();
        }
    }
    public static string ModeName(string mode) { return mode == "hold" ? "按住循环" : mode == "select" ? "选择后点按" : "点按一次"; }
    public static string Trigger(SoundRule rule)
    { return rule.Mode == "select" ? Name(rule.GateKey) + " → " + Name(rule.Key) : Name(rule.Key); }
    public static int[] Configured(Settings settings)
    {
        HashSet<int> keys = new HashSet<int> { settings.PauseKey, settings.ChatKey, settings.CancelKey };
        foreach (SoundRule rule in settings.GetRules())
            if (rule.Enabled) { keys.Add(rule.Key); if (rule.Mode == "select") keys.Add(rule.GateKey); }
        int[] result = new int[keys.Count]; keys.CopyTo(result); return result;
    }
}

public sealed class PreparedAudio
{
    public readonly Dictionary<string, PcmClip> Clips = new Dictionary<string, PcmClip>();
    public readonly Dictionary<string, PcmClip> Loops = new Dictionary<string, PcmClip>();
    public readonly Dictionary<string, string> Labels = new Dictionary<string, string>();
}

public static class AudioLibrary
{
    public static PreparedAudio Builtin(string directory)
    {
        PreparedAudio result = new PreparedAudio();
        foreach (string name in AudioBank.Names) result.Clips.Add(name, PcmClip.Load(Path.Combine(directory, name + ".wav")));
        foreach (string name in new string[] { "left", "right" }) result.Loops.Add(name, PcmClip.Load(Path.Combine(directory, name + "-loop.wav")));
        return result;
    }
    public static string FileFor(SoundRule rule, Settings settings, string root)
    {
        if (!String.IsNullOrWhiteSpace(rule.AudioFile)) return Path.GetFullPath(Path.Combine(root, rule.AudioFile));
        if (Array.IndexOf(AudioBank.Names, rule.Id) < 0) throw new Exception(rule.Name + "：尚未选择语音文件。");
        return Path.GetFullPath(Path.Combine(root, settings.CacheDirectory, rule.Id + ".wav"));
    }
    public static PreparedAudio Load(Settings settings, string root)
    {
        settings.ValidateRules(); PreparedAudio result = new PreparedAudio();
        foreach (SoundRule rule in settings.GetRules())
        {
            if (!rule.Enabled) continue;
            try
            {
                PcmClip clip = PcmClip.Load(FileFor(rule, settings, root)); result.Clips.Add(rule.Id, clip); result.Labels.Add(rule.Id,rule.Name);
                if (rule.Mode == "hold")
                {
                    string builtinLoop = Path.Combine(root, settings.CacheDirectory, rule.Id + "-loop.wav");
                    result.Loops.Add(rule.Id, String.IsNullOrWhiteSpace(rule.AudioFile) && File.Exists(builtinLoop) ? PcmClip.Load(builtinLoop) : Trim(clip));
                }
            }
            catch (Exception ex) { throw new Exception(rule.Name + "：" + ex.Message); }
        }
        return result;
    }
    public static PcmClip Trim(PcmClip clip)
    {
        int block = 96, count = (clip.Frames + block - 1) / block; float[] rms = new float[count]; float peak = 0;
        for (int k = 0; k < count; k++)
        {
            double energy = 0; int end = Math.Min(clip.Frames, (k + 1) * block);
            for (int frame = k * block; frame < end; frame++)
                energy += clip.Samples[frame * 2] * clip.Samples[frame * 2] + clip.Samples[frame * 2 + 1] * clip.Samples[frame * 2 + 1];
            rms[k] = (float)Math.Sqrt(energy / (block * 2)); peak = Math.Max(peak, rms[k]);
        }
        float threshold = Math.Max(0.002f, peak * 0.01f); int first = -1, last = -1;
        for (int k = 0; k < count; k++) if (rms[k] > threshold) { if (first < 0) first = k; last = k; }
        if (first < 0) return clip;
        int start = Math.Max(0, first * block - 720), finish = Math.Min(clip.Frames, (last + 1) * block + 960);
        float[] samples = new float[(finish - start) * 2]; Array.Copy(clip.Samples, start * 2, samples, 0, samples.Length); return new PcmClip(samples);
    }
}

public static class AudioImporter
{
    // Pass argv directly to an owned decoder, never through a command shell.
    public static string Quote(string value)
    {
        StringBuilder text = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { text.Append('\\', slashes * 2 + 1); text.Append(ch); }
            else { text.Append('\\', slashes); text.Append(ch); }
            slashes = 0;
        }
        text.Append('\\', slashes * 2); text.Append('"'); return text.ToString();
    }
    public static string Import(string root, string source)
    {
        if (!File.Exists(source)) throw new Exception("语音文件不存在：" + source);
        string decoder = Path.Combine(root, ".tools", "imageio_ffmpeg", "binaries", "ffmpeg-win-x86_64-v7.1.exe");
        if (!File.Exists(decoder)) throw new Exception("缺少随程序附带的音频转换器，请保留完整播放器文件夹。");
        string relative = Path.Combine("custom-audio", Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(source) + ".wav");
        string output = Path.Combine(root, relative), directory = Path.GetDirectoryName(output);
        Directory.CreateDirectory(directory);
        try
        {
            ProcessStartInfo start = new ProcessStartInfo(decoder,
                "-nostdin -hide_banner -v error -y -i " + Quote(Path.GetFullPath(source)) +
                " -map 0:a:0 -vn -t 120.1 -ar 48000 -ac 2 -c:a pcm_s16le " + Quote(output));
            start.UseShellExecute = false; start.CreateNoWindow = true; start.RedirectStandardError = true;
            StringBuilder error = new StringBuilder();
            using (Process process = new Process())
            {
                process.StartInfo = start;
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data != null) lock (error) { if (error.Length < 4000) error.AppendLine(args.Data); } };
                process.Start(); process.BeginErrorReadLine();
                if (!process.WaitForExit(30000)) { process.Kill(); process.WaitForExit(); throw new Exception("转换超时，请尝试较短的本地音频文件。"); }
                process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception("音频转换失败：" + error.ToString());
            }
            PcmClip clip = PcmClip.Load(output);
            if (clip.Frames < 4800) throw new Exception("语音太短，请使用至少 0.1 秒的音频。");
            return relative;
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            if (Directory.Exists(directory)) Directory.Delete(directory); // Only this new, empty import directory.
            throw;
        }
    }
}

public sealed class KeyCaptureForm : Form
{
    public int CapturedKey;
    public KeyCaptureForm(string caption)
    {
        Text = caption; ClientSize = new Size(420, 210); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; KeyPreview = true;
        Label area = new Label(); area.Text = "按下一个键盘键\n或在此区域点击鼠标左/右/中/侧键\n\n当前支持单键，不支持组合键";
        area.TextAlign = ContentAlignment.MiddleCenter; area.BackColor = Color.AliceBlue; area.SetBounds(15, 15, 390, 135); Controls.Add(area);
        area.MouseDown += delegate(object sender, MouseEventArgs args)
        {
            int key = args.Button == MouseButtons.Left ? 1 : args.Button == MouseButtons.Right ? 2 : args.Button == MouseButtons.Middle ? 4 : args.Button == MouseButtons.XButton1 ? 5 : args.Button == MouseButtons.XButton2 ? 6 : 0;
            if (key > 0) Accept(key);
        };
        Button cancel = new Button(); cancel.Text = "取消"; cancel.SetBounds(290, 165, 115, 30); cancel.Click += delegate { DialogResult = DialogResult.Cancel; }; Controls.Add(cancel);
        KeyDown += delegate(object sender, KeyEventArgs args) { args.SuppressKeyPress = true; Accept((int)args.KeyCode); };
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    { Accept((int)(keyData & Keys.KeyCode)); return true; }
    void Accept(int key) { if (key > 0 && key < 255) { CapturedKey = key; DialogResult = DialogResult.OK; } }
    public static int Choose(IWin32Window owner, string caption, int previous)
    { using (KeyCaptureForm form = new KeyCaptureForm(caption)) return form.ShowDialog(owner) == DialogResult.OK ? form.CapturedKey : previous; }
}

public sealed class SettingsForm : Form
{
    readonly string root;
    readonly Settings draft;
    readonly DataGridView grid = new DataGridView();
    readonly Dictionary<string, string> pending = new Dictionary<string, string>();
    readonly List<Control> actions = new List<Control>();
    readonly Label message = new Label();
    readonly BackgroundWorker worker = new BackgroundWorker();
    readonly NumericUpDown volume = new NumericUpDown(), crossfade = new NumericUpDown(), timeout = new NumericUpDown();
    readonly CheckBox startup = new CheckBox();
    AudioBank preview;
    public Settings Result;
    public PreparedAudio Library;
    public SettingsForm(Settings settings, string root)
    {
        this.root = root; draft = settings.Copy();
        Text = "自定义按键和语音"; Name = "settings-window"; ClientSize = new Size(960, 640); MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Microsoft YaHei UI", 9); MaximizeBox = false; FormBorderStyle=FormBorderStyle.FixedDialog;
        Label hint = new Label(); hint.Text = "自由添加或删除语音条目。双击触发键 / 前置键可重新绑定；键位须与你在游戏中的设置一致。";
        hint.SetBounds(18, 15, 925, 27); Controls.Add(hint);
        grid.Name = "rules-grid"; grid.SetBounds(18, 48, 924, 330); grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AllowUserToResizeRows = false;
        grid.AutoGenerateColumns = false; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name="Enabled", HeaderText="启用", Width=48 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name="RuleName", HeaderText="名称", Width=145 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name="Key", HeaderText="触发 / 选择键", Width=120, ReadOnly=true });
        DataGridViewComboBoxColumn mode = new DataGridViewComboBoxColumn { Name="Mode", HeaderText="播放方式", Width=120 };
        mode.Items.AddRange("点按一次", "按住循环", "选择后点按"); grid.Columns.Add(mode);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name="Gate", HeaderText="前置键", Width=100, ReadOnly=true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name="Audio", HeaderText="语音文件", AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill, ReadOnly=true });
        grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs args) { if (args.RowIndex >= 0 && (args.ColumnIndex == 2 || args.ColumnIndex == 4)) SetKey(args.ColumnIndex == 4); };
        grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs args) { args.ThrowException = false; };
        grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        Controls.Add(grid);
        AddAction("新增语音", "rule-add", 18, 390, delegate { Sync(); SoundRule rule = new SoundRule { Id=Guid.NewGuid().ToString("N"), Name="新语音 " + (draft.Rules.Count + 1) }; draft.Rules.Add(rule); Fill(); Select(rule.Id); });
        AddAction("删除所选", "rule-delete", 142, 390, delegate { SoundRule rule=Selected(); if(rule==null)return; Sync(); draft.Rules.Remove(rule); pending.Remove(rule.Id); Fill(); });
        AddAction("设置触发键", "rule-key", 266, 390, delegate { SetKey(false); });
        AddAction("设置前置键", "rule-gate", 390, 390, delegate { SetKey(true); });
        AddAction("选择语音…", "rule-audio", 514, 390, ChooseAudio);
        AddAction("试听所选", "rule-preview", 638, 390, Preview);
        AddAction("恢复莫伊拉语音", "rule-preset", 762, 390, delegate { draft.Rules = new Settings().GetRules(); pending.Clear(); Fill(); });
        Label modes = new Label(); modes.Text = "点按一次：松开也播完　　按住循环：松开停止　　选择后点按：前置键 → 选择键；只播放一次";
        modes.SetBounds(18, 435, 924, 27); Controls.Add(modes);
        AddControlKey("暂停 / 恢复", 18, draft.PauseKey, delegate(int key) { draft.PauseKey=key; });
        AddControlKey("聊天暂停", 220, draft.ChatKey, delegate(int key) { draft.ChatKey=key; });
        AddControlKey("取消选择", 422, draft.CancelKey, delegate(int key) { draft.CancelKey=key; });
        startup.Text="启动时启用"; startup.Checked=draft.StartEnabled; startup.SetBounds(650, 470, 190, 28); Controls.Add(startup); actions.Add(startup);
        AddNumber("默认音量", volume, 18, 514, 0, 100, draft.Volume);
        AddNumber("循环淡化(ms)", crossfade, 235, 514, 5, 100, draft.LoopCrossfadeMs);
        AddNumber("选择超时(秒)", timeout, 476, 514, 0, 60, (decimal)draft.OrbSelectionSeconds); timeout.DecimalPlaces=1;
        Label indefinite = new Label(); indefinite.Text="0 = 一直等待选择"; indefinite.SetBounds(713, 516, 215, 25); Controls.Add(indefinite);
        message.SetBounds(18, 556, 700, 58); message.Text="支持 MP3、WAV、OGG、FLAC、M4A 等本地音频；每段 0.1–120 秒。\n保存后立即生效，自动保留上一次配置。"; Controls.Add(message);
        AddAction("保存并应用", "settings-save", 720, 580, Save);
        AddAction("取消", "settings-cancel", 844, 580, delegate { DialogResult=DialogResult.Cancel; });
        worker.WorkerReportsProgress=true; worker.ProgressChanged += delegate(object sender, ProgressChangedEventArgs args) { message.Text=(string)args.UserState; };
        FormClosing += delegate(object sender, FormClosingEventArgs args) { if(worker.IsBusy) { args.Cancel=true; message.Text="正在处理音频，请稍等。"; } };
        Fill();
    }
    void AddAction(string text, string name, int x, int y, Action action)
    { Button button=new Button(); button.Text=text; button.Name=name; button.SetBounds(x,y,116,34); button.Click+=delegate { try { action(); } catch(Exception ex) { message.Text=ex.Message; message.ForeColor=Color.Firebrick; } }; Controls.Add(button); actions.Add(button); }
    void AddControlKey(string caption, int x, int key, Action<int> change)
    {
        Label label=new Label(); label.Text=caption; label.SetBounds(x,474,87,25); Controls.Add(label);
        Button button=new Button(); button.Text=InputKeys.Name(key); button.SetBounds(x+88,468,100,32); actions.Add(button); Controls.Add(button);
        button.Click+=delegate { key=KeyCaptureForm.Choose(this,"设置"+caption,key); change(key); button.Text=InputKeys.Name(key); };
    }
    void AddNumber(string caption, NumericUpDown number, int x, int y, decimal min, decimal max, decimal value)
    { Label label=new Label(); label.Text=caption; label.SetBounds(x,y+3,110,25); Controls.Add(label); number.Minimum=min; number.Maximum=max; number.Value=value; number.SetBounds(x+115,y,85,28); Controls.Add(number); actions.Add(number); }
    void Sync()
    {
        grid.EndEdit();
        foreach(DataGridViewRow row in grid.Rows)
        {
            SoundRule rule=(SoundRule)row.Tag; rule.Enabled=Convert.ToBoolean(row.Cells[0].Value); rule.Name=Convert.ToString(row.Cells[1].Value).Trim();
            string mode=Convert.ToString(row.Cells[3].Value); rule.Mode=mode=="按住循环"?"hold":mode=="选择后点按"?"select":"once";
        }
    }
    void Fill()
    {
        grid.Rows.Clear();
        foreach(SoundRule rule in draft.Rules)
        {
            string audio; if(pending.TryGetValue(rule.Id,out audio)) audio=Path.GetFileName(audio)+"（待保存）";
            else audio=String.IsNullOrWhiteSpace(rule.AudioFile)?(Array.IndexOf(AudioBank.Names,rule.Id)>=0?"默认 · "+rule.Id+".mp3":"未选择"):Path.GetFileName(rule.AudioFile);
            int index=grid.Rows.Add(rule.Enabled,rule.Name,InputKeys.Name(rule.Key),InputKeys.ModeName(rule.Mode),rule.GateKey==0?"—":InputKeys.Name(rule.GateKey),audio);
            grid.Rows[index].Tag=rule;
        }
    }
    SoundRule Selected() { return grid.CurrentRow==null?null:(SoundRule)grid.CurrentRow.Tag; }
    void Select(string id)
    { foreach(DataGridViewRow row in grid.Rows) if(((SoundRule)row.Tag).Id==id) { grid.CurrentCell=row.Cells[1]; row.Selected=true; break; } }
    void SetKey(bool gate)
    {
        SoundRule rule=Selected(); if(rule==null)throw new Exception("请先选中一条语音。"); Sync();
        if(gate && rule.Mode!="select") throw new Exception("只有“选择后点按”需要前置键，请先修改播放方式。");
        int key=KeyCaptureForm.Choose(this,gate?"设置前置键":"设置触发 / 选择键",gate?rule.GateKey:rule.Key);
        if(gate)rule.GateKey=key;else rule.Key=key; Fill(); Select(rule.Id);
    }
    void ChooseAudio()
    {
        SoundRule rule=Selected(); if(rule==null)throw new Exception("请先选中一条语音。"); Sync();
        using(OpenFileDialog dialog=new OpenFileDialog())
        {
            dialog.Title="选择“"+rule.Name+"”的语音"; dialog.Filter="音频文件|*.mp3;*.wav;*.ogg;*.flac;*.m4a;*.aac;*.opus;*.wma|所有文件|*.*";
            if(dialog.ShowDialog(this)==DialogResult.OK) { pending[rule.Id]=dialog.FileName; Fill(); Select(rule.Id); }
        }
    }
    void Busy(bool value)
    { grid.Enabled=!value; foreach(Control action in actions)action.Enabled=!value; message.ForeColor=Color.DarkSlateGray; if(value && preview!=null)preview.Stop(); }
    void Run(Func<object> job, Action<object> done)
    {
        Busy(true); message.Text="正在处理语音…";
        DoWorkEventHandler work=null; RunWorkerCompletedEventHandler finish=null;
        work=delegate(object sender,DoWorkEventArgs args) { args.Result=job(); };
        finish=delegate(object sender,RunWorkerCompletedEventArgs args)
        {
            worker.DoWork-=work; worker.RunWorkerCompleted-=finish; Busy(false);
            if(args.Error!=null) { message.Text=args.Error.Message; message.ForeColor=Color.Firebrick; Fill(); return; }
            try { done(args.Result); } catch(Exception ex) { message.Text=ex.Message; message.ForeColor=Color.Firebrick; }
        };
        worker.DoWork+=work; worker.RunWorkerCompleted+=finish; worker.RunWorkerAsync();
    }
    void ImportPending(SoundRule rule)
    {
        string path; if(!pending.TryGetValue(rule.Id,out path))return;
        worker.ReportProgress(0,"正在导入："+rule.Name); rule.AudioFile=AudioImporter.Import(root,path); pending.Remove(rule.Id);
    }
    void Preview()
    {
        SoundRule rule=Selected(); if(rule==null)throw new Exception("请先选中一条语音。"); Sync();
        Run(delegate { ImportPending(rule); PreparedAudio library=new PreparedAudio(); library.Clips.Add(rule.Id,PcmClip.Load(AudioLibrary.FileFor(rule,draft,root))); return library; },
            delegate(object value) { if(preview!=null)preview.Dispose(); preview=new AudioBank((PreparedAudio)value,(int)crossfade.Value); preview.SetVolume((int)volume.Value); preview.Play(rule.Id); Fill(); Select(rule.Id); message.Text="正在试听："+rule.Name; });
    }
    void Save()
    {
        Sync(); draft.Volume=(int)volume.Value; draft.LoopCrossfadeMs=(int)crossfade.Value; draft.OrbSelectionSeconds=(double)timeout.Value; draft.StartEnabled=startup.Checked; draft.ValidateRules();
        Run(delegate { foreach(SoundRule rule in draft.Rules)ImportPending(rule); return AudioLibrary.Load(draft,root); },
            delegate(object value) { draft.Save(root); Result=draft; Library=(PreparedAudio)value; DialogResult=DialogResult.OK; });
    }
    protected override void Dispose(bool disposing)
    { if(disposing) { if(preview!=null)preview.Dispose(); worker.Dispose(); } base.Dispose(disposing); }
}

public static class CustomizationTests
{
    static void Invoke(Control control,string method)
    { control.GetType().GetMethod(method,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(control,method=="OnClick"?new object[]{EventArgs.Empty}:null); }
    static void Require(bool condition,string text) { if(!condition)throw new Exception("Customization test: "+text); }
    static HashSet<int> Down(params int[] keys) { return new HashSet<int>(keys); }
    static bool Invalid(Settings settings) { try { settings.ValidateRules();return false; } catch { return true; } }
    public static string Editor(string root,Settings settings)
    {
        string fixture=Path.Combine(root,"editor-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            Settings input=settings.Copy();input.CacheDirectory=Path.GetFullPath(Path.Combine(root,settings.CacheDirectory));
            using(SettingsForm form=new SettingsForm(input,fixture))
            {
                IntPtr handle=form.Handle;
                DataGridView grid=(DataGridView)form.Controls.Find("rules-grid",true)[0];int initial=grid.Rows.Count;
                Invoke(form.Controls.Find("rule-add",true)[0],"OnClick");Require(grid.Rows.Count==initial+1,"add button adds a real row");
                SoundRule added=(SoundRule)grid.CurrentRow.Tag;added.Key=49;added.AudioFile=Path.Combine(root,"audio-cache/left.wav");
                grid.CurrentRow.Cells[1].Value="编辑窗口保存测试";grid.CurrentRow.Cells[3].Value="按住循环";
                Invoke(form.Controls.Find("settings-save",true)[0],"OnClick");
                Stopwatch clock=Stopwatch.StartNew();
                while(form.Result==null && clock.ElapsedMilliseconds<10000) { Application.DoEvents();System.Threading.Thread.Sleep(10); }
                Require(form.Result!=null && form.Library.Clips.Count==initial+1,"editor save validates, loads audio and completes");
                Settings reload=Settings.Load(fixture);SoundRule saved=reload.Rules[reload.Rules.Count-1];
                Require(saved.Name=="编辑窗口保存测试" && saved.Mode=="hold" && saved.Key==49,"edited row survives application restart");
                using(AudioBank bank=new AudioBank(form.Library,reload.LoopCrossfadeMs))
                {
                    bank.SetVolume(0);bank.Play(saved.Id,true);System.Threading.Thread.Sleep(80);
                    Require(bank.Mode(saved.Id)=="playing","new row plays on real native output");
                    PreparedAudio empty=new PreparedAudio();bank.Reload(empty,50,0);Require(bank.ActiveNames().Length==0,"hot reload removes previous voices");
                }
            }
            using(SettingsForm form=new SettingsForm(input,fixture))
            {
                IntPtr handle=form.Handle;DataGridView grid=(DataGridView)form.Controls.Find("rules-grid",true)[0];
                Invoke(form.Controls.Find("rule-add",true)[0],"OnClick");Invoke(form.Controls.Find("rule-delete",true)[0],"OnClick");
                Require(grid.Rows.Count==input.Rules.Count,"delete button removes selected row");
            }
            return "PASS: editor add/delete, cell edits, background save/apply, config reload, arbitrary loop on real Windows audio, hot reload. All audio checks silent; real configuration unchanged.\n";
        }
        finally
        {
            foreach(string name in new string[]{"config.json","config.previous.json"}) { string path=Path.Combine(fixture,name);if(File.Exists(path))File.Delete(path); }
            Directory.Delete(fixture);
        }
    }
    public static string Run(string root)
    {
        string fixture=Path.Combine(root,"customization-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        List<string> imports=new List<string>();
        string original=Path.GetFullPath(Path.Combine(root,"../永雏塔菲语音/left.mp3")); byte[] originalBytes=File.ReadAllBytes(original);
        try
        {
            Settings legacy=new Settings(); legacy.Save(fixture); Settings migrated=Settings.Load(fixture);
            Require(migrated.Rules.Count==6 && migrated.Rules[3].Mode=="select","old config migrates to six preset rules");
            Settings custom=migrated.Copy(); custom.PauseKey=120;
            custom.Rules[0].Key=65; custom.Rules[3].Key=65; custom.Rules[3].GateKey=70; custom.Rules[4].GateKey=70;
            for(int i=0;i<4;i++) custom.Rules.Add(new SoundRule { Id="extra-"+i,Name="自定义语音 "+i,Key=49+i,AudioFile="audio-cache/Q.wav" });
            custom.ValidateRules(); Require(custom.GetRules().Count==10,"more than six rules are supported");
            TriggerEngine engine=new TriggerEngine(custom);engine.Step(true,false,Down(),0);
            engine.Step(true,false,Down(49),1); Require(engine.OneShots.Contains("extra-0"),"added keyboard binding fires its own audio");
            engine.Step(true,false,Down(49),2); Require(engine.OneShots.Count==0,"holding a tap binding does not repeat");
            engine.Step(true,false,Down(65),3); Require(engine.Loops.Contains("left"),"custom hold key starts");
            engine.Step(true,false,Down(),4);Require(engine.Loops.Count==0,"custom hold release stops");
            engine.Step(true,false,Down(70),5);Require(engine.OrbUntil>5,"custom prefix key waits for choice");
            engine.Step(true,false,Down(65),6);Require(engine.OneShots.Contains("E-left") && engine.Loops.Count==0,"custom two-step selection suppresses direct hold");
            engine.Step(true,false,Down(65),7);Require(engine.OneShots.Count==0 && engine.Loops.Count==0,"selection hold does not loop");
            engine.Step(true,false,Down(120),8);Require(!engine.Enabled,"custom pause key works");
            Require(Array.IndexOf(custom.Keys,49)>=0 && Array.IndexOf(custom.Keys,70)>=0 && Array.IndexOf(custom.Keys,69)<0,"input scope follows changed and added keys");
            Settings clash=custom.Copy();clash.Rules[6].Key=16;Require(Invalid(clash),"direct duplicate is rejected");
            clash.Rules[6].Enabled=false;Require(!Invalid(clash),"disabled rule can retain a conflicting key");
            clash=custom.Copy();clash.Rules[6].Key=120;Require(Invalid(clash),"control key conflicts are rejected");
            clash=custom.Copy();clash.Rules[4].Key=65;Require(Invalid(clash),"duplicate two-step selection is rejected");
            clash=custom.Copy();clash.Rules[6].Key=70;Require(Invalid(clash),"prefix/direct conflicts are rejected");
            custom.Save(fixture);Settings restored=Settings.Load(fixture);Require(restored.Rules.Count==10 && restored.Rules[0].Key==65 && restored.PauseKey==120,"names, arbitrary rules and keys survive save/reload");
            Require(File.Exists(Path.Combine(fixture,"config.previous.json")),"atomic save preserves previous config");
            custom.Save(fixture);Require(Settings.Load(fixture).Rules.Count==10,"repeated save replaces an existing backup safely");
            PreparedAudio built=AudioLibrary.Load(custom,root);Require(built.Clips.Count==10 && built.Loops.Count==2,"all ten custom rows load into the mixer");
            PcmMixer mixer=new PcmMixer(built.Clips,built.Loops,50);mixer.SetVolume(100);mixer.Play("extra-0",false);mixer.Render(new float[960],480);Require(mixer.Mode("extra-0")=="playing","arbitrary rule ID plays audio");
            string spaced=Path.Combine(fixture,"中文 空格 & 测试.mp3");File.WriteAllBytes(spaced,originalBytes);
            string imported=AudioImporter.Import(root,spaced);imports.Add(imported);PcmClip pcm=PcmClip.Load(Path.Combine(root,imported));
            Require(pcm.Frames>48000 && pcm.Frames<60000,"MP3 with Chinese, spaces and ampersand converts to valid PCM");
            custom.Rules[6].AudioFile=imported;PreparedAudio changed=AudioLibrary.Load(custom,root);Require(changed.Clips["extra-0"].Frames==pcm.Frames,"chosen voice replaces only its own row");
            imported=AudioImporter.Import(root,Path.Combine(root,"audio-cache/left.wav"));imports.Add(imported);
            Require(PcmClip.Load(Path.Combine(root,imported)).Frames==pcm.Frames,"WAV import keeps frame duration");
            string bad=Path.Combine(fixture,"invalid.wav");File.WriteAllText(bad,"not audio");bool rejected=false;
            try { AudioImporter.Import(root,bad); } catch { rejected=true; }Require(rejected,"bad audio rejected without changing settings");
            byte[] after=File.ReadAllBytes(original);Require(after.Length==originalBytes.Length,"original source size unchanged");
            for(int i=0;i<after.Length;i++)Require(after[i]==originalBytes[i],"original MP3 bytes unchanged");
            Settings empty=custom.Copy();empty.Rules.Clear();Require(!Invalid(empty) && AudioLibrary.Load(empty,root).Clips.Count==0,"empty sound list can be saved safely");
            return "PASS: legacy migration; ten rules; custom keyboard hold/tap/prefix/pause; duplicate detection; disabled rules; atomic settings round-trip; arbitrary IDs; local MP3/WAV import with Unicode and shell characters; corrupted audio rejection; empty list; originals unchanged. No game launched or inputs sent.\n";
        }
        finally
        {
            foreach(string relative in imports) { string path=Path.Combine(root,relative);if(File.Exists(path))File.Delete(path);Directory.Delete(Path.GetDirectoryName(path)); }
            // Only direct, known fixture files are removed; no recursive deletion.
            foreach(string name in new string[] { "config.json","config.previous.json","中文 空格 & 测试.mp3","invalid.wav" })
            { string path=Path.Combine(fixture,name);if(File.Exists(path))File.Delete(path); }
            Directory.Delete(fixture);
        }
    }
}
