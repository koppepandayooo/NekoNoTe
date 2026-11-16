using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace NekoNoTe.Recorder;

public partial class RecorderPanel : System.Windows.Controls.UserControl
{
    private static RecorderService Svc => RecorderService.Instance ?? new RecorderService();

    private bool _built, _loading = true;   // 画面を作っている間に Slider などが値を変えて保存してしまわないように
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(40) };

    // 開いている録音(float・チャンネルごとに交互)
    private string? _path;
    private float[] _samples = Array.Empty<float>();
    private int _channels = 2, _rate = 48000;

    private WaveOutEvent? _player;
    private SliceProvider? _playing;

    private static readonly WpfBrush RowBg = new SolidColorBrush(WpfColor.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private static readonly WpfBrush RowSel = new SolidColorBrush(WpfColor.FromArgb(0x2E, 0x60, 0xCD, 0xFF));
    private static readonly WpfBrush Dim = new SolidColorBrush(WpfColor.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

    public RecorderPanel()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Tick();
        // 機器の一覧や録音の一覧は重いので、タブを初めて開いた時に作る
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                if (!_built) Build();
                BuildList(); _tick.Start();
            }
            else { StopPlayback(); if (!Svc.IsRecording) _tick.Stop(); }
        };
    }

    private void Build()
    {
        _built = true;
        Svc.StatusChanged += () => Dispatcher.BeginInvoke(() => { UpdateStatus(); if (IsVisible) BuildList(); });
        Svc.ClipSaved += path => Dispatcher.BeginInvoke(() => { BuildList(); if (IsVisible && _path == null) Open(path); });
        Wave.SelectionChanged += _ => UpdateRange();
        LoadSettings();
    }

    // ---------- 設定 ----------

    private void LoadSettings()
    {
        _loading = true;
        var s = Svc.Settings;
        EnabledToggle.IsChecked = s.Enabled;
        SourceCombo.Items.Clear();
        var sources = RecorderService.Sources();
        foreach (var src in sources) SourceCombo.Items.Add(src);
        var cur = sources.FirstOrDefault(x => x.Loopback == s.Loopback && x.Device == s.Device)
                  ?? sources.FirstOrDefault(x => x.Loopback == s.Loopback && x.Device.StartsWith(s.Device, StringComparison.OrdinalIgnoreCase));
        if (cur != null) SourceCombo.SelectedItem = cur;
        else { SourceCombo.Items.Add($"{s.Device}(見つかりません)"); SourceCombo.SelectedIndex = SourceCombo.Items.Count - 1; }
        SecondsSlider.Value = s.Seconds;
        SecondsText.Text = $"{s.Seconds} 秒";
        KeyButton.Content = InputHook.Describe(s.ClipKeys);
        PreviewVolumeSlider.Value = s.PreviewVolume;
        PreviewVolumeText.Text = $"{s.PreviewVolume}%";
        _loading = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = Svc.IsRecording ? $"録音中 {Svc.RecordingTime:m\\:ss} · 止めると «録ったもの» に入ります" : Svc.Status;
        LiveDot.Fill = new SolidColorBrush(Svc.IsRecording ? WpfColor.FromRgb(0xFF, 0x6B, 0x6B) : Svc.IsListening ? WpfColor.FromRgb(0x4C, 0xD9, 0x64) : WpfColor.FromRgb(0x88, 0x88, 0x88));
        ClipButton.IsEnabled = RecordButton.IsEnabled = Svc.IsListening;
        RecordText.Text = Svc.IsRecording ? "録音停止" : "録音開始";
        RecordIcon.Symbol = Svc.IsRecording ? Wpf.Ui.Controls.SymbolRegular.Stop24 : Wpf.Ui.Controls.SymbolRegular.Record24;
        if (Svc.IsRecording) _tick.Start();
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        Svc.Settings.Enabled = EnabledToggle.IsChecked == true;
        Svc.Save();
        Svc.Apply();
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceCombo.SelectedItem is not RecordSource src) return;
        Svc.Settings.Device = src.Device;
        Svc.Settings.Loopback = src.Loopback;
        Svc.Save();
        Svc.Apply();
    }

    private void Seconds_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SecondsText == null) return;
        SecondsText.Text = $"{SecondsSlider.Value:0} 秒";
        if (_loading) return;
        Svc.Settings.Seconds = (int)SecondsSlider.Value;
        Svc.Save();
    }

    /// <summary>聴く時だけの音量。再生中に動かすとすぐ変わる</summary>
    private void PreviewVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Svc.Settings.PreviewVolume = (int)PreviewVolumeSlider.Value;
        PreviewVolumeText.Text = $"{Svc.Settings.PreviewVolume}%";
        if (_playing is { } p) p.Gain = Svc.Settings.PreviewVolume / 100f;
        Svc.Save();
    }

    private void Key_Click(object sender, RoutedEventArgs e)
    {
        KeyButton.Content = "キーを押して…";
        KeyButton.ToolTip = "Esc でやめる · BackSpace でなし";
        Svc.CaptureKeys(keys =>
        {
            if (keys != null)
            {
                Svc.Settings.ClipKeys = keys.ToList();
                Svc.Save();
                Svc.Apply();
            }
            KeyButton.Content = InputHook.Describe(Svc.Settings.ClipKeys);
        });
    }

    private void Clip_Click(object sender, RoutedEventArgs e)
    {
        var path = Svc.SaveClip();
        if (path != null) Open(path);
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (Svc.IsRecording) Svc.StopRecording(); else Svc.StartRecording();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(RecorderService.ClipsDir);
        try { Process.Start(new ProcessStartInfo(RecorderService.ClipsDir) { UseShellExecute = true }); } catch { }
    }

    // ---------- 録ったもの ----------

    private static TimeSpan Length(string path)
    {
        RecorderService.RepairHeader(path);
        try { using var r = new WaveFileReader(path); return r.TotalTime; }
        catch { return TimeSpan.Zero; }
    }

    private void BuildList()
    {
        ClipList.Children.Clear();
        var files = Directory.Exists(RecorderService.ClipsDir)
            ? new DirectoryInfo(RecorderService.ClipsDir).GetFiles("*.wav").OrderByDescending(f => f.CreationTime).ToList()
            : new List<FileInfo>();
        NoClipsText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var f in files)
        {
            var kind = f.Name.Contains("_cut") ? "切り抜き" : f.Name.Contains("_rec") ? "録音" : "さかのぼり";
            var row = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6),
                Background = string.Equals(f.FullName, _path, StringComparison.OrdinalIgnoreCase) ? RowSel : RowBg,
                Cursor = System.Windows.Input.Cursors.Hand, Tag = f.FullName,
                Child = new DockPanel
                {
                    Children =
                    {
                        Docked(new WpfTextBlock { Text = $"{Length(f.FullName).TotalSeconds:0.0} 秒", Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, Dock.Right),
                        new StackPanel
                        {
                            Children =
                            {
                                new WpfTextBlock { Text = f.CreationTime.ToString("M/d HH:mm:ss"), FontSize = 13, FontWeight = FontWeights.SemiBold },
                                new WpfTextBlock { Text = kind, Foreground = Dim, FontSize = 11 },
                            },
                        },
                    },
                },
            };
            row.MouseLeftButtonUp += (_, _) => Open(f.FullName);
            ClipList.Children.Add(row);
        }
    }

    private static UIElement Docked(UIElement e, Dock d) { DockPanel.SetDock(e, d); return e; }

    private void Open(string path)
    {
        StopPlayback();
        try
        {
            using var r = new AudioFileReader(path);
            _channels = r.WaveFormat.Channels;
            _rate = r.WaveFormat.SampleRate;
            var list = new List<float>((int)(r.Length / 4) + 16);
            var buf = new float[_rate * _channels];
            int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0) list.AddRange(buf.AsSpan(0, n).ToArray());
            _samples = list.ToArray();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Window.GetWindow(this), "開けませんでした: " + ex.Message, "録音");
            return;
        }
        _path = path;
        var mono = new float[_samples.Length / _channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float m = 0;
            for (int c = 0; c < _channels; c++) m = Math.Max(m, Math.Abs(_samples[i * _channels + c]));
            mono[i] = m;
        }
        Wave.SetAudio(mono);
        Editor.Visibility = Visibility.Visible;
        EditorEmpty.Visibility = Visibility.Collapsed;
        ClipTitle.Text = File.GetCreationTime(path).ToString("M/d HH:mm:ss");
        AddResultText.Visibility = Visibility.Collapsed;
        TotalTimeText.Text = $"{Frames / (double)_rate:0.00} 秒";
        StartTimeText.Text = "0.00";
        UpdateRange();
        foreach (Border b in ClipList.Children)
            b.Background = string.Equals((string)b.Tag, path, StringComparison.OrdinalIgnoreCase) ? RowSel : RowBg;
    }

    private int Frames => _samples.Length / Math.Max(1, _channels);
    private (int Start, int End) SelFrames => ((int)(Wave.SelStart * Frames), Math.Max((int)(Wave.SelStart * Frames) + 1, (int)(Wave.SelEnd * Frames)));

    private void UpdateRange()
    {
        var (a, b) = SelFrames;
        RangeText.Text = $"{a / (double)_rate:0.00} 秒 〜 {b / (double)_rate:0.00} 秒(長さ {(b - a) / (double)_rate:0.00} 秒)";
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => Wave.SetSelection(0, 1);

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        StopPlayback();
        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(_path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
        catch (Exception ex) { System.Windows.MessageBox.Show(Window.GetWindow(this), "消せませんでした: " + ex.Message, "録音"); return; }
        _path = null;
        _samples = Array.Empty<float>();
        Editor.Visibility = Visibility.Collapsed;
        EditorEmpty.Visibility = Visibility.Visible;
        BuildList();
    }

    // ---------- 聴く ----------

    /// <summary>配列の一部を流す(どこまで流したかを持つ)</summary>
    private sealed class SliceProvider : ISampleProvider
    {
        private readonly float[] _data;
        private readonly int _end;
        public int Pos;
        public volatile float Gain = 1;
        public SliceProvider(float[] data, int start, int end, WaveFormat fmt) { _data = data; Pos = start; _end = end; WaveFormat = fmt; }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Max(0, Math.Min(count, _end - Pos));
            // NAudio が渡す buffer は中身が byte[] のことがあり、Array.Copy だと例外になるので 1 つずつ入れる
            for (int i = 0; i < n; i++) buffer[offset + i] = _data[Pos + i] * Gain;
            Pos += n;
            return n;
        }
    }

    /// <summary>選んだ所を、仕上げ(音量・なめらか)をかけた状態で作る</summary>
    private float[] Render()
    {
        var (a, b) = SelFrames;
        var outp = new float[(b - a) * _channels];
        Array.Copy(_samples, a * _channels, outp, 0, outp.Length);
        if (NormalizeCheck.IsChecked == true)
        {
            float peak = 0;
            foreach (var v in outp) peak = Math.Max(peak, Math.Abs(v));
            if (peak > 0.0001f)
            {
                float g = Math.Min(0.9f / peak, 20f);   // 無音に近い所を大きくしすぎない
                for (int i = 0; i < outp.Length; i++) outp[i] *= g;
            }
        }
        if (FadeCheck.IsChecked == true)
        {
            int fade = Math.Min(_rate / 100 * 2, (b - a) / 4);   // 20ms
            for (int f = 0; f < fade; f++)
            {
                float k = f / (float)fade;
                for (int c = 0; c < _channels; c++)
                {
                    outp[f * _channels + c] *= k;
                    outp[outp.Length - (f + 1) * _channels + c] *= k;
                }
            }
        }
        return outp;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_player != null) { StopPlayback(); return; }
        if (_samples.Length == 0) return;
        var data = Render();
        _playing = new SliceProvider(data, 0, data.Length, WaveFormat.CreateIeeeFloatWaveFormat(_rate, _channels)) { Gain = Svc.Settings.PreviewVolume / 100f };
        _player = new WaveOutEvent { DesiredLatency = 120 };
        _player.Init(_playing);
        _player.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(StopPlayback);
        _player.Play();
        PlayIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Stop24;
        PlayText.Text = "止める";
        _tick.Start();
    }

    private void StopPlayback()
    {
        var p = _player;
        _player = null;
        _playing = null;
        if (p != null) { try { p.Stop(); } catch { } p.Dispose(); }
        Wave.Playhead = null;
        PlayIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Play24;
        PlayText.Text = "選んだ所を聴く";
    }

    private void Tick()
    {
        if (Svc.IsRecording) UpdateStatus();
        if (_playing is { } p && Frames > 0)
        {
            var (a, _) = SelFrames;
            Wave.Playhead = (a + p.Pos / (double)_channels) / Frames;
        }
    }

    // ---------- 切り抜いた所を保存する ----------

    private void WriteWav(string file)
    {
        var data = Render();
        using var w = new WaveFileWriter(file, new WaveFormat(_rate, 16, _channels));
        w.WriteSamples(data, 0, data.Length);
    }

    /// <summary>選んだ所を «録ったもの» に新しく入れる(元の録音はそのまま)</summary>
    private void SaveCut_Click(object sender, RoutedEventArgs e)
    {
        if (_samples.Length == 0) return;
        try
        {
            Directory.CreateDirectory(RecorderService.ClipsDir);
            var file = Path.Combine(RecorderService.ClipsDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_cut.wav");
            for (int i = 2; File.Exists(file); i++) file = Path.Combine(RecorderService.ClipsDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_cut_{i}.wav");
            WriteWav(file);
            BuildList();
            Open(file);
            AddResultText.Text = "切り抜いた所を «録ったもの» に入れました";
            AddResultText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AddResultText.Text = "保存できませんでした: " + ex.Message;
            AddResultText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>場所と名前を決めて WAV で書き出す</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_samples.Length == 0 || _path == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "書き出す",
            Filter = "WAV ファイル|*.wav",
            FileName = "録音 " + File.GetCreationTime(_path).ToString("MMdd_HHmmss") + ".wav",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            WriteWav(dialog.FileName);
            AddResultText.Text = $"«{Path.GetFileName(dialog.FileName)}» に書き出しました";
        }
        catch (Exception ex)
        {
            AddResultText.Text = "書き出せませんでした: " + ex.Message;
        }
        AddResultText.Visibility = Visibility.Visible;
    }
    public void Shutdown() => StopPlayback();
}
