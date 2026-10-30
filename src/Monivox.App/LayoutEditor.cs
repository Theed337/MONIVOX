using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Monivox.Core;

namespace Monivox.App;

/// <summary>
/// Freier Bildschirm-Editor: Bereiche aufziehen, verschieben, an Kanten/Ecken ziehen. Rastet an
/// Monitorrändern und Kanten anderer Bereiche ein, rechnet pixelgenau mit der echten Auflösung.
/// Lücken sind erlaubt (bleiben schwarz), Überlappungen werden rot markiert.
/// </summary>
public sealed class LayoutEditor : FrameworkElement
{
    [Flags]
    private enum Edge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, Move = 16 }

    private readonly List<NormRect> _regions = [];
    private readonly List<string> _names = [];
    private int _selected = -1;
    private int _direct = -1;
    private Edge _drag;
    private Point _dragStart;
    private NormRect _dragOrigin;
    private bool _creating;

    public int PhysicalWidth { get; set; } = 3840;
    public int PhysicalHeight { get; set; } = 2160;

    public event Action? Changed;
    public event Action? SelectionChanged;

    public LayoutEditor()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 240;
    }

    public IReadOnlyList<NormRect> Regions => _regions;
    public IReadOnlyList<string> Names => _names;

    /// <summary>Index des Direktbereichs (echtes Monitorbild, keine Latenz, DRM) oder -1.</summary>
    public int DirectIndex
    {
        get => _direct;
        set { _direct = value; InvalidateVisual(); Changed?.Invoke(); }
    }

    public int SelectedIndex
    {
        get => _selected;
        set { _selected = value; InvalidateVisual(); SelectionChanged?.Invoke(); }
    }

    public void Load(Layout layout)
    {
        _regions.Clear();
        _regions.AddRange(layout.Regions);
        _names.Clear();
        for (int i = 0; i < _regions.Count; i++) _names.Add(layout.RegionName(i));
        _direct = layout.HasDirect ? layout.DirectIndex : -1;
        SelectedIndex = _regions.Count > 0 ? 0 : -1;
        InvalidateVisual();
    }

    public Layout ToLayout(string name) => new(name, _regions) { Names = _names.ToList(), DirectIndex = _direct };

    public void SetRegion(int index, NormRect rect)
    {
        _regions[index] = Clamp(rect);
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void SetName(int index, string name)
    {
        _names[index] = name;
        InvalidateVisual();
        Changed?.Invoke();
    }

    public void AddRegion(NormRect rect)
    {
        if (_regions.Count >= Layout.MaxRegions) return;
        _regions.Add(Clamp(rect));
        _names.Add($"Bereich {_regions.Count}");
        SelectedIndex = _regions.Count - 1;
        Changed?.Invoke();
    }

    public void RemoveSelected()
    {
        if (_selected < 0) return;
        _regions.RemoveAt(_selected);
        _names.RemoveAt(_selected);
        if (_direct == _selected) _direct = -1;
        else if (_direct > _selected) _direct--;
        SelectedIndex = Math.Min(_selected, _regions.Count - 1);
        Changed?.Invoke();
    }

    /// <summary>Teilt den gewählten Bereich in zwei Hälften.</summary>
    public void SplitSelected(bool vertical)
    {
        if (_selected < 0 || _regions.Count >= Layout.MaxRegions) return;
        var r = _regions[_selected];
        NormRect a, b;
        if (vertical)
        {
            double half = SnapX(r.X + r.Width / 2) - r.X;
            a = r with { Width = half };
            b = new NormRect(r.X + half, r.Y, r.Width - half, r.Height);
        }
        else
        {
            double half = SnapY(r.Y + r.Height / 2) - r.Y;
            a = r with { Height = half };
            b = new NormRect(r.X, r.Y + half, r.Width, r.Height - half);
        }
        _regions[_selected] = a;
        _regions.Insert(_selected + 1, b);
        if (_direct > _selected) _direct++;
        _names.Insert(_selected + 1, $"Bereich {_regions.Count}");
        SelectedIndex = _selected + 1;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------- Geometrie

    private Rect MonitorRect()
    {
        double pad = 12;
        double w = Math.Max(1, ActualWidth - 2 * pad), h = Math.Max(1, ActualHeight - 2 * pad);
        double aspect = (double)PhysicalWidth / PhysicalHeight;
        double mw = w, mh = w / aspect;
        if (mh > h) { mh = h; mw = h * aspect; }
        return new Rect(pad + (w - mw) / 2, pad + (h - mh) / 2, mw, mh);
    }

    private Rect ToScreen(NormRect r)
    {
        var m = MonitorRect();
        return new Rect(m.X + r.X * m.Width, m.Y + r.Y * m.Height, r.Width * m.Width, r.Height * m.Height);
    }

    private Point ToNorm(Point p)
    {
        var m = MonitorRect();
        return new Point((p.X - m.X) / m.Width, (p.Y - m.Y) / m.Height);
    }

    // Auf ganze Pixel der echten Auflösung runden
    private double SnapX(double x) => Math.Round(Math.Clamp(x, 0, 1) * PhysicalWidth) / PhysicalWidth;
    private double SnapY(double y) => Math.Round(Math.Clamp(y, 0, 1) * PhysicalHeight) / PhysicalHeight;

    private NormRect Clamp(NormRect r)
    {
        double minW = (double)Layout.MinRegionWidth / PhysicalWidth, minH = (double)Layout.MinRegionHeight / PhysicalHeight;
        double w = Math.Clamp(r.Width, minW, 1), h = Math.Clamp(r.Height, minH, 1);
        double x = Math.Clamp(r.X, 0, 1 - w), y = Math.Clamp(r.Y, 0, 1 - h);
        double sx = SnapX(x), sy = SnapY(y);
        return new NormRect(sx, sy, SnapX(x + w) - sx, SnapY(y + h) - sy);
    }

    /// <summary>Rastet einen Wert an Monitorrand oder Kanten anderer Bereiche ein (8 Bildschirmpixel Toleranz).</summary>
    private double Magnet(double value, bool horizontal, int ignore)
    {
        var m = MonitorRect();
        double tol = 8 / (horizontal ? m.Width : m.Height);
        var candidates = new List<double> { 0, 1, 0.5 };
        for (int i = 0; i < _regions.Count; i++)
        {
            if (i == ignore) continue;
            var r = _regions[i];
            if (horizontal) { candidates.Add(r.X); candidates.Add(r.Right); }
            else { candidates.Add(r.Y); candidates.Add(r.Bottom); }
        }
        var best = candidates.OrderBy(c => Math.Abs(c - value)).First();
        return Math.Abs(best - value) <= tol ? best : value;
    }

    private Edge HitTestEdges(Rect r, Point p)
    {
        const double t = 7;
        if (!new Rect(r.X - t, r.Y - t, r.Width + 2 * t, r.Height + 2 * t).Contains(p)) return Edge.None;
        Edge e = Edge.None;
        if (Math.Abs(p.X - r.Left) <= t) e |= Edge.Left;
        else if (Math.Abs(p.X - r.Right) <= t) e |= Edge.Right;
        if (Math.Abs(p.Y - r.Top) <= t) e |= Edge.Top;
        else if (Math.Abs(p.Y - r.Bottom) <= t) e |= Edge.Bottom;
        if (e == Edge.None && r.Contains(p)) e = Edge.Move;
        return e;
    }

    private (int Index, Edge Edge) HitTest(Point p)
    {
        // Ausgewählter Bereich hat Vorrang (liegt oben)
        if (_selected >= 0)
        {
            var e = HitTestEdges(ToScreen(_regions[_selected]), p);
            if (e != Edge.None) return (_selected, e);
        }
        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            var e = HitTestEdges(ToScreen(_regions[i]), p);
            if (e != Edge.None) return (i, e);
        }
        return (-1, Edge.None);
    }

    // ------------------------------------------------------------- Maus

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        var (index, edge) = HitTest(p);
        _dragStart = p;
        if (index >= 0)
        {
            SelectedIndex = index;
            _drag = edge;
            _dragOrigin = _regions[index];
        }
        else if (MonitorRect().Contains(p) && _regions.Count < Layout.MaxRegions)
        {
            // Neuen Bereich aufziehen
            var n = ToNorm(p);
            _regions.Add(new NormRect(SnapX(n.X), SnapY(n.Y), 0, 0));
            _names.Add($"Bereich {_regions.Count}");
            _selected = _regions.Count - 1;
            _dragOrigin = _regions[_selected];
            _drag = Edge.Right | Edge.Bottom;
            _creating = true;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag == Edge.None || !IsMouseCaptured)
        {
            Cursor = HitTest(p).Edge switch
            {
                Edge.Left or Edge.Right => Cursors.SizeWE,
                Edge.Top or Edge.Bottom => Cursors.SizeNS,
                Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
                Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
                Edge.Move => Cursors.SizeAll,
                _ => MonitorRect().Contains(p) ? Cursors.Cross : Cursors.Arrow,
            };
            return;
        }

        var m = MonitorRect();
        double dx = (p.X - _dragStart.X) / m.Width, dy = (p.Y - _dragStart.Y) / m.Height;
        var o = _dragOrigin;
        double left = o.X, top = o.Y, right = o.Right, bottom = o.Bottom;

        if (_drag == Edge.Move)
        {
            double nl = Magnet(left + dx, true, _selected);
            double nr = Magnet(right + dx, true, _selected);
            double shiftX = Math.Abs(nl - (left + dx)) <= Math.Abs(nr - (right + dx)) ? nl - left : nr - right;
            double nt = Magnet(top + dy, false, _selected);
            double nb = Magnet(bottom + dy, false, _selected);
            double shiftY = Math.Abs(nt - (top + dy)) <= Math.Abs(nb - (bottom + dy)) ? nt - top : nb - bottom;
            _regions[_selected] = Clamp(new NormRect(left + shiftX, top + shiftY, o.Width, o.Height));
        }
        else
        {
            if (_drag.HasFlag(Edge.Left)) left = Magnet(left + dx, true, _selected);
            if (_drag.HasFlag(Edge.Right)) right = Magnet(right + dx, true, _selected);
            if (_drag.HasFlag(Edge.Top)) top = Magnet(top + dy, false, _selected);
            if (_drag.HasFlag(Edge.Bottom)) bottom = Magnet(bottom + dy, false, _selected);
            if (right < left) (left, right) = (right, left);
            if (bottom < top) (top, bottom) = (bottom, top);
            _regions[_selected] = Clamp(new NormRect(left, top, right - left, bottom - top));
        }
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        if (_creating)
        {
            var r = _regions[_selected];
            // Nur geklickt statt gezogen -> Standardgröße
            if (Math.Abs(e.GetPosition(this).X - _dragStart.X) < 6 && Math.Abs(e.GetPosition(this).Y - _dragStart.Y) < 6)
                _regions[_selected] = Clamp(new NormRect(r.X, r.Y, 0.25, 0.25));
            _creating = false;
            SelectionChanged?.Invoke();
        }
        _drag = Edge.None;
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; }
    }

    // ------------------------------------------------------------- Zeichnen

    protected override void OnRender(DrawingContext dc)
    {
        var res = Application.Current.Resources;
        var text = (Brush)res["Text"];
        var accent = ((SolidColorBrush)res["Accent"]).Color;
        var danger = ((SolidColorBrush)res["Danger"]).Color;
        var m = MonitorRect();

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(16, 16, 20)), new Pen((Brush)res["Stroke"], 2), m, 6, 6);

        var overlaps = new HashSet<int>();
        for (int i = 0; i < _regions.Count; i++)
            for (int j = i + 1; j < _regions.Count; j++)
                if (_regions[i].Overlaps(_regions[j], 1e-6)) { overlaps.Add(i); overlaps.Add(j); }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < _regions.Count; i++)
        {
            var r = ToScreen(_regions[i]);
            bool sel = i == _selected;
            bool direct = i == _direct;
            var baseColor = overlaps.Contains(i) ? danger : direct ? Color.FromRgb(0x22, 0xC5, 0x5E) : accent;
            var fill = new SolidColorBrush(Color.FromArgb(sel ? (byte)120 : (byte)70, baseColor.R, baseColor.G, baseColor.B));
            var pen = new Pen(new SolidColorBrush(baseColor), sel ? 2.5 : 1.2);
            dc.DrawRoundedRectangle(fill, pen, new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2)), 4, 4);

            int pw = (int)Math.Round(_regions[i].Width * PhysicalWidth), ph = (int)Math.Round(_regions[i].Height * PhysicalHeight);
            string kind = direct ? "★ DIREKT – keine Latenz, DRM ✓" : "virtuell";
            var label = new FormattedText($"{i + 1}  {_names[i]}\n{pw} × {ph} · {kind}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, dpi) { MaxTextWidth = Math.Max(10, r.Width - 12), MaxTextHeight = Math.Max(10, r.Height - 8) };
            dc.DrawText(label, new Point(r.X + 8, r.Y + 6));

            if (sel)
            {
                foreach (var c in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                    dc.DrawEllipse(Brushes.White, pen, c, 4, 4);
            }
        }

        if (_regions.Count == 0)
        {
            var hint = new FormattedText("Ziehe mit der Maus Bereiche auf – oder wähle oben eine Vorlage.",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, text, dpi);
            dc.DrawText(hint, new Point(m.X + (m.Width - hint.Width) / 2, m.Y + (m.Height - hint.Height) / 2));
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 340 : Math.Max(MinHeight, availableSize.Height));
}
