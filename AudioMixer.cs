using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public sealed class PcmClip
{
    public readonly float[] Samples;
    public int Frames { get { return Samples.Length / 2; } }
    public PcmClip(float[] samples) { Samples = samples; }
    public static PcmClip Load(string path)
    {
        using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
        {
            if (new string(reader.ReadChars(4)) != "RIFF") throw new Exception("Invalid PCM cache: " + path);
            reader.ReadUInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") throw new Exception("Invalid WAV cache: " + path);
            bool formatOk = false; byte[] pcm = null;
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                string chunk = new string(reader.ReadChars(4)); uint size = reader.ReadUInt32();
                long next = reader.BaseStream.Position + size + (size & 1);
                if (next > reader.BaseStream.Length) throw new Exception("Truncated WAV cache: " + path);
                if (chunk == "fmt " && size >= 16)
                {
                    ushort format = reader.ReadUInt16(), channels = reader.ReadUInt16(); uint rate = reader.ReadUInt32();
                    reader.ReadUInt32(); ushort align = reader.ReadUInt16(), bits = reader.ReadUInt16();
                    formatOk = format == 1 && channels == 2 && rate == 48000 && align == 4 && bits == 16;
                }
                else if (chunk == "data")
                {
                    if (size > 48000 * 4 * 120) throw new Exception("Audio cache exceeds two minutes: " + path);
                    pcm = reader.ReadBytes((int)size);
                }
                reader.BaseStream.Position = next;
            }
            if (!formatOk || pcm == null || pcm.Length < 4 || pcm.Length % 4 != 0)
                throw new Exception("Cache must be 48 kHz stereo PCM16: " + path);
            float[] samples = new float[pcm.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[i * 2] | pcm[i * 2 + 1] << 8) / 32768f;
            return new PcmClip(samples);
        }
    }
}

// Pure sample mixer, independent of Windows output and game input.
public sealed class PcmMixer
{
    sealed class Voice
    {
        public string Name;
        public PcmClip Clip;
        public bool Loop;
        public int Frame, Age, FadeLeft = -1, Crossfade;
        public bool Done;
    }
    readonly object gate = new object();
    readonly Dictionary<string, PcmClip> clips;
    readonly Dictionary<string, PcmClip> loops;
    readonly List<Voice> voices = new List<Voice>();
    readonly Dictionary<string, int> positions = new Dictionary<string, int>();
    readonly int ramp = 384; // 8 ms onset/release smoothing.
    readonly int overlap;
    float volume = 0.7f, limiter = 1;
    public long Wraps { get; private set; }
    public long LimitedFrames { get; private set; }
    public PcmMixer(Dictionary<string, PcmClip> clips, Dictionary<string, PcmClip> loops, int crossfadeMs)
    { this.clips = clips; this.loops = loops; overlap = crossfadeMs * 48; }
    public void SetVolume(int percent) { lock (gate) volume = percent / 100f; }
    Voice NewVoice(string name, bool loop)
    {
        PcmClip clip = loop ? loops[name] : clips[name];
        return new Voice { Name = name, Clip = clip, Loop = loop, Crossfade = loop ? Math.Min(overlap, clip.Frames / 8) : 0 };
    }
    public void Play(string name, bool repeat)
    {
        lock (gate)
        {
            if (repeat)
            {
                foreach (Voice voice in voices) if (voice.Name == name && voice.Loop && voice.FadeLeft < 0) return;
            }
            // Preserve existing sounds, including repeat taps of the same ability.
            if (voices.Count >= 32) return;
            voices.Add(NewVoice(name, repeat)); positions[name] = 0;
        }
    }
    public void SetLoops(IEnumerable<string> desired)
    {
        HashSet<string> wanted = new HashSet<string>(desired);
        lock (gate)
        {
            foreach (Voice voice in voices)
                if (voice.Loop && voice.FadeLeft < 0 && !wanted.Contains(voice.Name)) voice.FadeLeft = ramp;
            foreach (string name in wanted) Play(name, true);
        }
    }
    public void Stop()
    {
        lock (gate) foreach (Voice voice in voices) if (voice.FadeLeft < 0) voice.FadeLeft = ramp;
    }
    public int Position(string name) { lock (gate) { int value; return positions.TryGetValue(name, out value) ? value * 1000 / 48000 : 0; } }
    public string Mode(string name)
    {
        lock (gate)
        {
            foreach (Voice voice in voices) if (voice.Name == name && !voice.Done && voice.FadeLeft < 0) return "playing";
            return "stopped";
        }
    }
    public string[] ActiveNames()
    {
        lock (gate)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (Voice voice in voices)
            {
                if (voice.Done || voice.FadeLeft >= 0) continue;
                if (!counts.ContainsKey(voice.Name)) counts[voice.Name] = 0;
                counts[voice.Name]++;
            }
            List<string> names = new List<string>();
            foreach (KeyValuePair<string, int> item in counts) names.Add(item.Key + (item.Value > 1 ? " ×" + item.Value : ""));
            return names.ToArray();
        }
    }
    public void Render(float[] output, int frames)
    {
        lock (gate)
        {
            Array.Clear(output, 0, frames * 2);
            for (int frame = 0; frame < frames; frame++)
            {
                float left = 0, right = 0;
                foreach (Voice voice in voices)
                {
                    if (voice.Done) continue;
                    float gain = Math.Min(1f, voice.Age / (float)ramp);
                    if (voice.FadeLeft >= 0) gain *= voice.FadeLeft / (float)ramp;
                    else if (!voice.Loop) gain *= Math.Min(1f, (voice.Clip.Frames - voice.Frame) / (float)ramp);
                    int index = voice.Frame * 2;
                    float a = voice.Clip.Samples[index], b = voice.Clip.Samples[index + 1];
                    if (voice.Loop && voice.Crossfade > 0 && voice.Frame >= voice.Clip.Frames - voice.Crossfade)
                    {
                        int head = voice.Frame - (voice.Clip.Frames - voice.Crossfade);
                        float blend = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (head + 0.5) / voice.Crossfade));
                        a = a * (1 - blend) + voice.Clip.Samples[head * 2] * blend;
                        b = b * (1 - blend) + voice.Clip.Samples[head * 2 + 1] * blend;
                    }
                    left += a * gain; right += b * gain;
                    if (voice.Age < ramp) voice.Age++;
                    voice.Frame++;
                    if (voice.FadeLeft > 0) voice.FadeLeft--;
                    if (voice.FadeLeft == 0) voice.Done = true;
                    if (voice.Frame >= voice.Clip.Frames)
                    {
                        if (voice.Loop) { voice.Frame = voice.Crossfade; Wraps++; }
                        else voice.Done = true;
                    }
                    positions[voice.Name] = voice.Frame;
                }
                left *= volume; right *= volume;
                float peak = Math.Max(Math.Abs(left), Math.Abs(right));
                float target = peak > 0.94f ? 0.94f / peak : 1f;
                if (target < limiter) { limiter = target; LimitedFrames++; }
                else limiter += (target - limiter) / 3840f; // 80 ms release, no hard clipping.
                output[frame * 2] = left * limiter;
                output[frame * 2 + 1] = right * limiter;
            }
            voices.RemoveAll(delegate(Voice voice) { return voice.Done; });
        }
    }
}

public static class WaveNative
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct Format { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    public struct Header { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [DllImport("winmm.dll")] public static extern uint waveOutOpen(out IntPtr device, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] public static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] public static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveOutClose(IntPtr device);
}

public sealed class AudioBank : IDisposable
{
    public static readonly string[] Names = { "left", "right", "SHIFT", "E-left", "E-right", "Q" };
    sealed class Buffer { public IntPtr Data, Header; public bool Prepared; }
    Dictionary<string, PcmClip> clips;
    Dictionary<string, string> labels;
    // Keep enough queued audio for Windows/game scheduling, without polling Sleep(2).
    readonly Buffer[] buffers = new Buffer[6];
    readonly AutoResetEvent completedEvent = new AutoResetEvent(false);
    readonly float[] mixed = new float[960];
    readonly byte[] bytes = new byte[1920];
    readonly uint headerSize = (uint)Marshal.SizeOf(typeof(WaveNative.Header));
    readonly int flagOffset = (int)Marshal.OffsetOf(typeof(WaveNative.Header), "Flags");
    IntPtr device;
    Thread thread;
    volatile bool running;
    volatile string outputError;
    bool disposed;
    long completed, underruns;
    public PcmMixer Mixer { get; private set; }
    public long CompletedBuffers { get { return Interlocked.Read(ref completed); } }
    public long Underruns { get { return Interlocked.Read(ref underruns); } }
    public AudioBank(string directory) : this(directory, 50) { }
    public AudioBank(string directory, int crossfadeMs) : this(AudioLibrary.Builtin(directory), crossfadeMs) { }
    public AudioBank(PreparedAudio library, int crossfadeMs)
    {
        clips = library.Clips; labels = library.Labels;
        Mixer = new PcmMixer(clips, library.Loops, crossfadeMs);
        try
        {
            WaveNative.Format format = new WaveNative.Format { Tag = 1, Channels = 2, Rate = 48000, BytesPerSecond = 192000, Align = 4, Bits = 16 };
            Check(WaveNative.waveOutOpen(out device, UInt32.MaxValue, ref format,
                completedEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000)); // CALLBACK_EVENT
            for (int i = 0; i < buffers.Length; i++)
            {
                Buffer buffer = new Buffer(); buffers[i] = buffer;
                buffer.Data = Marshal.AllocHGlobal(bytes.Length); buffer.Header = Marshal.AllocHGlobal((int)headerSize);
                WaveNative.Header header = new WaveNative.Header { Data = buffer.Data, Length = (uint)bytes.Length };
                Marshal.StructureToPtr(header, buffer.Header, false);
                Check(WaveNative.waveOutPrepareHeader(device, buffer.Header, headerSize)); buffer.Prepared = true;
                Queue(buffer);
            }
            running = true;
            thread = new Thread(Output); thread.IsBackground = true; thread.Name = "Local PCM audio mixer";
            thread.Priority = ThreadPriority.AboveNormal; thread.Start();
        }
        catch { Dispose(); throw; }
    }
    static void Check(uint result) { if (result != 0) throw new Exception("Windows 音频输出错误：" + result); }
    void CheckOutput() { if (outputError != null) throw new Exception(outputError); }
    void Queue(Buffer buffer)
    {
        Mixer.Render(mixed, 480);
        for (int i = 0; i < mixed.Length; i++)
        {
            short sample = (short)Math.Round(Math.Max(-1f, Math.Min(1f, mixed[i])) * 32767);
            bytes[i * 2] = (byte)(sample & 255); bytes[i * 2 + 1] = (byte)((sample >> 8) & 255);
        }
        Marshal.Copy(bytes, 0, buffer.Data, bytes.Length);
        Check(WaveNative.waveOutWrite(device, buffer.Header, headerSize));
    }
    void Output()
    {
        try
        {
            int next = 0;
            while (running)
            {
                completedEvent.WaitOne(100);
                if (!running) break;
                bool allDone = true;
                foreach (Buffer queued in buffers)
                    if ((Marshal.ReadInt32(queued.Header, flagOffset) & 1) == 0) { allDone = false; break; }
                if (allDone) Interlocked.Increment(ref underruns);
                // A notification can cover multiple completed blocks; refill every one in order.
                for (int count = 0; count < buffers.Length && running; count++)
                {
                    Buffer buffer = buffers[next];
                    if ((Marshal.ReadInt32(buffer.Header, flagOffset) & 1) == 0) break;
                    Interlocked.Increment(ref completed); Queue(buffer); next = (next + 1) % buffers.Length;
                }
            }
        }
        catch (Exception error) { outputError = error.Message; running = false; }
    }
    public void SetVolume(int percent) { Mixer.SetVolume(percent); }
    public void Play(string name) { Play(name, false); }
    public void Play(string name, bool repeat) { CheckOutput(); Mixer.Play(name, repeat); }
    public void SetLoops(IEnumerable<string> names) { CheckOutput(); Mixer.SetLoops(names); }
    public string[] ActiveNames() { CheckOutput(); return Mixer.ActiveNames(); }
    public string[] DisplayActiveNames()
    {
        string[] names = ActiveNames();
        for (int i = 0; i < names.Length; i++)
        {
            int suffix = names[i].IndexOf(" ×"); string id = suffix < 0 ? names[i] : names[i].Substring(0, suffix), label;
            if (labels.TryGetValue(id, out label)) names[i] = label + (suffix < 0 ? "" : names[i].Substring(suffix));
        }
        return names;
    }
    public void Reload(PreparedAudio library, int crossfadeMs, int volume)
    {
        CheckOutput(); PcmMixer replacement = new PcmMixer(library.Clips, library.Loops, crossfadeMs); replacement.SetVolume(volume);
        clips = library.Clips; labels = library.Labels; Mixer = replacement;
    }
    public void Stop() { Mixer.Stop(); }
    public int Length(string name) { return clips[name].Frames * 1000 / 48000; }
    public int Position(string name) { return Mixer.Position(name); }
    public string Mode(string name) { CheckOutput(); return Mixer.Mode(name); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        running = false;
        completedEvent.Set();
        if (thread != null) thread.Join();
        if (device == IntPtr.Zero) { completedEvent.Dispose(); return; }
        WaveNative.waveOutReset(device);
        foreach (Buffer buffer in buffers)
        {
            if (buffer == null) continue;
            if (buffer.Prepared && WaveNative.waveOutUnprepareHeader(device, buffer.Header, headerSize) != 0) continue;
            if (buffer.Header != IntPtr.Zero) Marshal.FreeHGlobal(buffer.Header);
            if (buffer.Data != IntPtr.Zero) Marshal.FreeHGlobal(buffer.Data);
        }
        WaveNative.waveOutClose(device); device = IntPtr.Zero;
        completedEvent.Dispose();
    }
}
