using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace NekoNoTe.Recorder;

/// <summary>
/// 波形と、切り抜く範囲。範囲はドラッグで決める(端の縦線をつかむとその端だけ動く)。
/// 位置はどれも 0〜1(全体に対する割合)。
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private float[] _peaks = Array.Empty<float>();   // 細かい区切りごとの音の大きさ(0〜1)
    private int _drag;   // 0 = なし, 1 = 始まり, 2 = 終わり, 3 = 新しく選ぶ
    private double _anchor;

    public double SelStart { get; private set; }
    public double SelEnd { get; private set; } = 1;
    private double? _playhead;
    public double? Playhead { get => _playhead; set { _playhead = value; InvalidateVisual(); } }

    /// <summary>範囲が変わった。ドラッグを離した時は true</summary>
    public event Action<bool>? SelectionChanged;

    private static readonly WpfBrush Bg = new SolidColorBrush(WpfColor.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private static readonly WpfBrush SelBg = new SolidColorBrush(WpfColor.FromArgb(0x24, 0x60, 0xCD, 0xFF));
    private static readonly WpfBrush WaveIn = new SolidColorBrush(WpfColor.FromRgb(0x60, 0xCD, 0xFF));
    private static readonly WpfBrush WaveOut = new SolidColorBrush(WpfColor.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly WpfPen Handle = new(new SolidColorBrush(WpfColor.FromRgb(0x60, 0xCD, 0xFF)), 2);
    private static readonly WpfPen Head = new(System.Windows.Media.Brushes.White, 1.5);
    private static readonly WpfPen Mid = new(new SolidColorBrush(WpfColor.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);

    public WaveformView()
    {
        ClipToBounds = true;
        Cursor = System.Windows.Input.Cursors.IBeam;
        foreach (var b in new Freezable[] { Bg, SelBg, WaveIn, WaveOut, Handle, Head, Mid }) if (b.CanFreeze) b.Freeze();
    }

    /// <summary>音(モノラルにしたもの)を渡す</summary>
    public void SetAudio(float[] mono)
    {
        const int bucket = 64;
        _peaks = new float[(mono.Length + bucket - 1) / bucket];
        for (int i = 0; i < _peaks.Length; i++)
        {
            float m = 0;
            int end = Math.Min(mono.Length, (i + 1) * bucket);
            for (int j = i * bucket; j < end; j++) m = Math.Max(m, Math.Abs(mono[j]));
            _peaks[i] = m;
        }
        // 小さい音でも形が分かるよう、いちばん大きい所に合わせて見せる
        var max = _peaks.Length > 0 ? _peaks.Max() : 0;
        if (max > 0.0001f) for (int i = 0; i < _peaks.Length; i++) _peaks[i] /= max;
        SelStart = 0; SelEnd = 1; _playhead = null;
        InvalidateVisual();
    }

    public void SetSelection(double start, double end)
    {
        SelStart = Math.Clamp(Math.Min(start, end), 0, 1);
        SelEnd = Math.Clamp(Math.Max(start, end), 0, 1);
        InvalidateVisual();
        SelectionChanged?.Invoke(true);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(Bg, null, new Rect(0, 0, w, h), 8, 8);
        if (_peaks.Length == 0 || w < 2) return;
        double x0 = SelStart * w, x1 = SelEnd * w;
        dc.DrawRectangle(SelBg, null, new Rect(x0, 0, Math.Max(0, x1 - x0), h));
        dc.DrawLine(Mid, new WpfPoint(0, h / 2), new WpfPoint(w, h / 2));

        // 1ピクセルごとに、その間でいちばん大きい音の高さで縦線を描く
        var inGeo = new StreamGeometry();
        var outGeo = new StreamGeometry();
        using (var gin = inGeo.Open())
        using (var gout = outGeo.Open())
        {
            int cols = (int)w;
            for (int x = 0; x < cols; x++)
            {
                int a = (int)((long)x * _peaks.Length / cols), b = Math.Max(a + 1, (int)((long)(x + 1) * _peaks.Length / cols));
                float m = 0;
                for (int i = a; i < b && i < _peaks.Length; i++) m = Math.Max(m, _peaks[i]);
                double half = Math.Max(0.5, m * (h / 2 - 6));
                var g = x >= x0 && x <= x1 ? gin : gout;
                g.BeginFigure(new WpfPoint(x + 0.5, h / 2 - half), false, false);
                g.LineTo(new WpfPoint(x + 0.5, h / 2 + half), true, false);
            }
        }
        dc.DrawGeometry(null, new WpfPen(WaveOut, 1), outGeo);
        dc.DrawGeometry(null, new WpfPen(WaveIn, 1), inGeo);
        dc.DrawLine(Handle, new WpfPoint(x0, 0), new WpfPoint(x0, h));
        dc.DrawLine(Handle, new WpfPoint(x1, 0), new WpfPoint(x1, h));
        dc.DrawRectangle(Handle.Brush, null, new Rect(x0 - 4, 0, 8, 10));
        dc.DrawRectangle(Handle.Brush, null, new Rect(x1 - 4, h - 10, 8, 10));
        if (_playhead is { } p) dc.DrawLine(Head, new WpfPoint(p * w, 0), new WpfPoint(p * w, h));
    }

    private double Frac(System.Windows.Input.MouseEventArgs e) => Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_peaks.Length == 0) return;
        double x = e.GetPosition(this).X, w = ActualWidth;
        if (Math.Abs(x - SelStart * w) <= 7) _drag = 1;
        else if (Math.Abs(x - SelEnd * w) <= 7) _drag = 2;
        else { _drag = 3; _anchor = Frac(e); SelStart = SelEnd = _anchor; }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        double x = e.GetPosition(this).X, w = ActualWidth;
        if (_drag == 0)
        {
            Cursor = Math.Abs(x - SelStart * w) <= 7 || Math.Abs(x - SelEnd * w) <= 7 ? System.Windows.Input.Cursors.SizeWE : System.Windows.Input.Cursors.IBeam;
            return;
        }
        var f = Frac(e);
        if (_drag == 1) SelStart = Math.Min(f, SelEnd);
        else if (_drag == 2) SelEnd = Math.Max(f, SelStart);
        else { SelStart = Math.Min(_anchor, f); SelEnd = Math.Max(_anchor, f); }
        InvalidateVisual();
        SelectionChanged?.Invoke(false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == 0) return;
        _drag = 0;
        ReleaseMouseCapture();
        // クリックしただけ(幅がほとんどない)なら全体を選び直す
        if (SelEnd - SelStart < 0.002) { SelStart = 0; SelEnd = 1; }
        InvalidateVisual();
        SelectionChanged?.Invoke(true);
    }
}
