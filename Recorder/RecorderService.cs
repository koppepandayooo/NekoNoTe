using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace NekoNoTe.Recorder;

public sealed class RecorderSettings
{
    /// <summary>オン・オフは保存しない(NekoNoTe の起動時はいつもオフ)</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Enabled { get; set; }
    /// <summary>録る機器の名前。Loopback なら再生機器(そこから出ている音)、そうでなければ録音機器。空なら «いつもの再生機器»(PC の音)</summary>
    public string Device { get; set; } = "";
    public bool Loopback { get; set; } = true;
    /// <summary>キーを押した時に保存する «直前の何秒» (5〜30)</summary>
    public int Seconds { get; set; } = 15;
    public List<int> ClipKeys { get; set; } = new() { 0x12, 0x52 };   // Alt + R
    /// <summary>«選んだ所を聴く» の音量(0〜100)。聴く時だけで、保存する音には影響しない</summary>
    public int PreviewVolume { get; set; } = 50;
}

/// <summary>録る音の選択肢</summary>
public sealed record RecordSource(string Device, bool Loopback)
{
    public override string ToString() => Device == "" ? "PC の音(いつもの再生機器から出ている音)" : Loopback ? $"{Device} から出ている音" : Device;
}

/// <summary>
/// 録音の常駐部分。選んだ機器の音を、いつも直前 30 秒だけメモリに持っておき(さかのぼり録音)、
/// キーを押したらそのうち直前の数秒をファイルにする。ふつうの «開始・停止» の録音もできる。
/// «PC の音» を選ぶと、ゲームや通話などスピーカー・ヘッドホンから出ている音をまとめて録る。
/// </summary>
public sealed class RecorderService : IDisposable
{
    public static RecorderService? Instance { get; private set; }

    private const int MaxSeconds = 30;
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe");
    private static readonly string SettingsPath = Path.Combine(Dir, "recorder.json");
    public static readonly string ClipsDir = Path.Combine(Dir, "recordings");
    /// <summary>録音中のファイルはここに書き、止めた時に ClipsDir へ移す(途中のものが一覧に出ないように)</summary>
    private static readonly string TempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NekoNoTe", "rec-temp");

    public RecorderSettings Settings { get; }

    private IWaveIn? _capture;
    private WaveFormat? _format;
    private byte[] _ring = Array.Empty<byte>();
    private int _ringPos, _ringFilled;
    private DateTime _lastData;
    private readonly object _gate = new();
    private WaveFileWriter? _manual;
    private DateTime _manualStart;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(5) };

    private InputHook? _hook;
    private Action<IReadOnlyCollection<int>?>? _capturing;
    private RecorderToast? _toast;

    public string Status { get; private set; } = "";
    public bool IsListening => _capture != null;
    public bool IsRecording => _manual != null;
    public TimeSpan RecordingTime => IsRecording ? DateTime.Now - _manualStart : TimeSpan.Zero;

    public event Action? StatusChanged;
    /// <summary>オン・オフや録音中かどうかが変わった時(サイドバーの表示用。タブを開く前は作られていないので static)</summary>
    public static event Action? StateChanged;

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke();
        StateChanged?.Invoke();
    }
    /// <summary>新しいファイルができた(パス)</summary>
    public event Action<string>? ClipSaved;

    public RecorderService()
    {
        Instance = this;
        try { if (File.Exists(SettingsPath)) Settings = JsonSerializer.Deserialize<RecorderSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch { }
        Settings ??= new();
        // 途中で終わった録音の修復は、一覧を作る時に 1 つずつ行う
        // 前に録音の途中で終わった書きかけは捨てる
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        _retry.Tick += (_, _) => { if (_capture == null && Settings.Enabled) Start(); };
        Apply();
    }

    /// <summary>今録音しているファイル(なければ null)</summary>
    public string? RecordingPath => _manual?.Filename;

    /// <summary>
    /// 録音の途中で NekoNoTe が終わると、WAV の «長さ» が 0 のまま残って開けなくなる。
    /// 起動した時に、ファイルの大きさから長さを書き直す
    /// </summary>
    private static void RepairHeaders()
    {
        if (!Directory.Exists(ClipsDir)) return;
        foreach (var f in Directory.GetFiles(ClipsDir, "*.wav")) RepairHeader(f);
    }

    /// <summary>1つのファイルの長さを直す(開く前にも呼ぶ。起動した時に他のアプリが開いていて直せなかった時のため)</summary>
    public static void RepairHeader(string f)
    {
        if (string.Equals(f, Instance?.RecordingPath, StringComparison.OrdinalIgnoreCase)) return;
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                var br = new BinaryReader(fs);
                var bw = new BinaryWriter(fs);
                if (fs.Length < 44 || new string(br.ReadChars(4)) != "RIFF") return;
                uint riff = br.ReadUInt32();
                if (riff == fs.Length - 8) return;
                fs.Position = 12;
                while (fs.Position + 8 <= fs.Length)
                {
                    var id = new string(br.ReadChars(4));
                    long sizePos = fs.Position;
                    uint size = br.ReadUInt32();
                    if (id == "data")
                    {
                        fs.Position = sizePos;
                        bw.Write((uint)(fs.Length - sizePos - 4));
                        fs.Position = 4;
                        bw.Write((uint)(fs.Length - 8));
                        break;
                    }
                    fs.Position = sizePos + 4 + size + (size & 1);
                }
            }
            catch { }
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>設定を反映する(録る機器・オンオフ・キー)</summary>
    public void Apply()
    {
        Stop();
        if (Settings.Enabled) Start();
        else SetStatus("オフ(右上のスイッチで録り始めます)");
        UpdateHook();
    }

    // ---------- 録る機器 ----------

    public static List<RecordSource> Sources()
    {
        var list = new List<RecordSource> { new("", true) };   // いちばん上は «PC の音»
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) list.Add(new(d.FriendlyName, false));
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) list.Add(new(d.FriendlyName, true));
        }
        catch { }
        return list;
    }

    private static MMDevice? FindDevice(MMDeviceEnumerator en, string name, bool loopback)
    {
        if (name == "") return en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : null;
        var all = en.EnumerateAudioEndPoints(loopback ? DataFlow.Render : DataFlow.Capture, DeviceState.Active).ToList();
        return all.FirstOrDefault(d => d.FriendlyName == name)
            ?? all.FirstOrDefault(d => d.FriendlyName.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    private void Start()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = FindDevice(en, Settings.Device, Settings.Loopback);
            if (dev == null)
            {
                SetStatus($"«{(Settings.Device == "" ? "再生機器" : Settings.Device)}» が見つかりません(つながったら自動で録り始めます)");
                _retry.Start();
                return;
            }
            IWaveIn cap = Settings.Loopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev, true, 50);
            lock (_gate)
            {
                _format = cap.WaveFormat;
                int bytes = _format.AverageBytesPerSecond * MaxSeconds;
                _ring = new byte[bytes - bytes % _format.BlockAlign];
                _ringPos = _ringFilled = 0;
                _lastData = DateTime.UtcNow;
            }
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, e) =>
            {
                // 機器が抜かれた時など。少ししてから録り直す
                if (e.Exception == null) return;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_capture != cap) return;
                    Stop();
                    SetStatus("録音が止まりました: " + e.Exception.Message);
                    _retry.Start();
                });
            };
            cap.StartRecording();
            _capture = cap;
            _retry.Stop();
            SetStatus($"さかのぼり録音中 · {new RecordSource(Settings.Device, Settings.Loopback)}");
        }
        catch (Exception ex)
        {
            SetStatus("録音を始められませんでした: " + ex.Message);
            _retry.Start();
        }
    }

    private void Stop()
    {
        _retry.Stop();
        StopRecording();
        var cap = _capture;
        _capture = null;
        if (cap != null)
        {
            try { cap.StopRecording(); } catch { }
            cap.Dispose();
        }
    }

    private void SetStatus(string s)
    {
        Status = s;
        RaiseStatusChanged();
    }

    /// <summary>
    /// «スピーカーから出ている音» は、何も鳴っていない間は何も届かない。
    /// そのままだと «直前 15 秒» が昔の音になってしまうので、届かなかった間は無音で埋める
    /// </summary>
    private void FillGap(DateTime now, int incomingBytes)
    {
        if (_format == null) return;
        var incoming = TimeSpan.FromSeconds((double)incomingBytes / _format.AverageBytesPerSecond);
        var gap = now - _lastData - incoming;
        if (gap.TotalMilliseconds < 120) return;
        long bytes = (long)(gap.TotalSeconds * _format.AverageBytesPerSecond);
        bytes = Math.Min(bytes, _ring.Length);
        bytes -= bytes % _format.BlockAlign;
        var zero = new byte[Math.Min(bytes, 65536)];
        for (long left = bytes; left > 0; left -= zero.Length)
        {
            int n = (int)Math.Min(left, zero.Length);
            Write(zero, n);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (sender != _capture && _capture != null) return;
            var now = DateTime.UtcNow;
            FillGap(now, e.BytesRecorded);
            Write(e.Buffer, e.BytesRecorded);
            _lastData = now;
        }
    }

    /// <summary>リングバッファ(と、録音中ならファイル)に書く</summary>
    private void Write(byte[] data, int count)
    {
        _manual?.Write(data, 0, count);
        int off = 0;
        while (count > 0)
        {
            int n = Math.Min(count, _ring.Length - _ringPos);
            Buffer.BlockCopy(data, off, _ring, _ringPos, n);
            _ringPos = (_ringPos + n) % _ring.Length;
            _ringFilled = Math.Min(_ring.Length, _ringFilled + n);
            off += n; count -= n;
        }
    }

    private static string NewPath(string kind)
    {
        Directory.CreateDirectory(ClipsDir);
        var path = Path.Combine(ClipsDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{kind}.wav");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(ClipsDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{kind}_{i}.wav");
        return path;
    }

    /// <summary>直前の数秒をファイルにする</summary>
    public string? SaveClip()
    {
        string path;
        int seconds;
        lock (_gate)
        {
            if (_capture == null || _format == null || _ringFilled == 0) return null;
            FillGap(DateTime.UtcNow, 0);
            _lastData = DateTime.UtcNow;
            seconds = Math.Clamp(Settings.Seconds, 5, MaxSeconds);
            int want = Math.Min(_ringFilled, _format.AverageBytesPerSecond * seconds);
            want -= want % _format.BlockAlign;
            var data = new byte[want];
            int start = (_ringPos - want + _ring.Length) % _ring.Length;
            int first = Math.Min(want, _ring.Length - start);
            Buffer.BlockCopy(_ring, start, data, 0, first);
            Buffer.BlockCopy(_ring, 0, data, first, want - first);
            path = NewPath("clip");
            using var w = new WaveFileWriter(path, _format);
            w.Write(data, 0, data.Length);
        }
        ShowToast("保存しました", $"直前 {seconds} 秒 · «録音» タブで切り抜けます");
        ClipSaved?.Invoke(path);
        return path;
    }

    public void StartRecording()
    {
        lock (_gate)
        {
            if (_capture == null || _format == null || _manual != null) return;
            Directory.CreateDirectory(TempDir);
            _manual = new WaveFileWriter(Path.Combine(TempDir, "recording.wav"), _format);
            _manualStart = DateTime.Now;
        }
        RaiseStatusChanged();
    }

    public void StopRecording()
    {
        string? path = null;
        lock (_gate)
        {
            if (_manual == null) return;
            var temp = _manual.Filename;
            _manual.Dispose();
            _manual = null;
            // 止めた時に初めて «録ったもの» に入れる(名前は録り始めた時刻)
            Directory.CreateDirectory(ClipsDir);
            path = Path.Combine(ClipsDir, $"{_manualStart:yyyyMMdd_HHmmss}_rec.wav");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(ClipsDir, $"{_manualStart:yyyyMMdd_HHmmss}_rec_{i}.wav");
            try { File.Move(temp, path); }
            catch { path = null; }
        }
        RaiseStatusChanged();
        if (path != null) ClipSaved?.Invoke(path);
    }

    private void ShowToast(string title, string sub)
    {
        _toast ??= new RecorderToast();
        _toast.Show(title, sub);
    }

    // ---------- キー ----------

    private void UpdateHook()
    {
        bool want = (Settings.Enabled && Settings.ClipKeys.Count > 0) || _capturing != null;
        if (want && _hook == null)
        {
            _hook = new InputHook();
            _hook.KeyDown += (vk, down) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnKey(vk, down));
        }
        else if (!want && _hook != null)
        {
            _hook.Dispose();
            _hook = null;
        }
    }

    /// <summary>次に押したキー(組み合わせ)を受け取る。Esc でやめる・BackSpace で «なし»</summary>
    public void CaptureKeys(Action<IReadOnlyCollection<int>?> done)
    {
        _capturing = done;
        UpdateHook();
    }

    private void OnKey(int vk, IReadOnlyCollection<int> down)
    {
        if (_capturing is { } cap)
        {
            if (InputHook.IsModifier(vk)) return;
            _capturing = null;
            if (vk == 0x1B) cap(null);
            else if (vk == 0x08) cap(Array.Empty<int>());
            else cap(down.ToList());
            UpdateHook();
            return;
        }
        if (Settings.Enabled && InputHook.Matches(Settings.ClipKeys, vk, down)) SaveClip();
    }

    public void Dispose()
    {
        Stop();
        _hook?.Dispose();
        _toast?.Close();
        if (Instance == this) Instance = null;
    }
}
