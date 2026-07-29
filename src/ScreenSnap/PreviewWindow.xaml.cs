using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ScreenSnap.Native;
using Point = System.Windows.Point;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using ColorConverter = System.Windows.Media.ColorConverter;
using WpfShape = System.Windows.Shapes.Shape;
using WpfPath = System.Windows.Shapes.Path;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace ScreenSnap;

public partial class PreviewWindow : Window
{
    private readonly Bitmap _bitmap;
    private readonly string _savedPath;

    private enum Tool { Select, Arrow, Rectangle, Text }
    private enum Manip { None, Resize, Rotate }

    private Tool _tool = Tool.Select;

    private MediaBrush _color = MediaBrushes.Red;
    private double _thickness = 3;
    private double _fontSize = 20;
    private FontFamily _fontFamily = new("Segoe UI");

    private readonly List<UIElement> _annotations = new();
    private readonly Dictionary<WpfPath, (Point from, Point to)> _arrowPoints = new();
    private readonly List<ToggleButton> _toolButtons;

    private bool _updatingToggles;
    private bool _syncingProps;
    private bool _dirty;

    // Drawing a new shape.
    private bool _drawing;
    private Point _start;
    private WpfShape? _active;

    // Selection + move.
    private UIElement? _selected;
    private bool _movingBody;
    private Point _lastMove;

    // Resize / rotate via handles.
    private Manip _manip = Manip.None;
    private Matrix _m0;
    private Point _anchorLocal;
    private Point _cornerLocal;
    private Point _rotCenter;
    private double _rotStartAngle;

    // Selection overlay.
    private readonly System.Windows.Shapes.Polygon _outline;
    private readonly WpfRectangle[] _cornerHandles = new WpfRectangle[4];
    private readonly System.Windows.Shapes.Ellipse _rotateHandle;
    private readonly List<UIElement> _overlay = new();

    private TextBox? _activeText;

    private const double HitTolerance = 6;
    private const double HandleSize = 9;

    public PreviewWindow(Bitmap bitmap, string savedPath)
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = ((App)System.Windows.Application.Current).CurrentIconSource();
        _bitmap = bitmap;
        _savedPath = savedPath;
        PreviewImage.Source = ToBitmapSource(bitmap);
        SavedPathText.Text = $"Saved to: {savedPath}";
        SavedPathText.ToolTip = savedPath;

        var accent = (MediaBrush)System.Windows.Application.Current.Resources["Brush.Accent"];

        _outline = new System.Windows.Shapes.Polygon
        {
            Stroke = accent,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = MediaBrushes.Transparent,
            IsHitTestVisible = false,
        };
        AddOverlay(_outline);

        var cursors = new[] { Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW };
        for (int i = 0; i < 4; i++)
        {
            var handle = new WpfRectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = MediaBrushes.White,
                Stroke = accent,
                StrokeThickness = 1,
                Cursor = cursors[i],
                Tag = i,
            };
            handle.MouseLeftButtonDown += Handle_MouseDown;
            handle.MouseMove += Handle_MouseMove;
            handle.MouseLeftButtonUp += Handle_MouseUp;
            _cornerHandles[i] = handle;
            AddOverlay(handle);
        }

        _rotateHandle = new System.Windows.Shapes.Ellipse
        {
            Width = HandleSize + 2,
            Height = HandleSize + 2,
            Fill = accent,
            Stroke = MediaBrushes.White,
            StrokeThickness = 1,
            Cursor = Cursors.Hand,
        };
        _rotateHandle.MouseLeftButtonDown += Handle_MouseDown;
        _rotateHandle.MouseMove += Handle_MouseMove;
        _rotateHandle.MouseLeftButtonUp += Handle_MouseUp;
        AddOverlay(_rotateHandle);

        _toolButtons = new List<ToggleButton> { SelectTool, ArrowTool, RectTool, TextTool };
        BuildSwatches();
        BuildFonts();

        ThicknessCombo.SelectedIndex = 2;
        TextSizeCombo.SelectedIndex = 2;
        SelectTool.IsChecked = true;
        SetOverlayVisible(false);
        UpdatePropertyPanels();
    }

    private void AddOverlay(UIElement element)
    {
        Panel.SetZIndex(element, 10000);
        AnnotationCanvas.Children.Add(element);
        _overlay.Add(element);
    }

    public static PreviewWindow FromFile(string path)
    {
        using var loaded = new Bitmap(path);
        return new PreviewWindow(new Bitmap(loaded), path);
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // --- Toolbar / palette ---

    private void BuildSwatches()
    {
        string[] hexes = { "#E81123", "#FFB900", "#16C60C", "#0078D4", "#000000", "#FFFFFF" };

        foreach (var hex in hexes)
        {
            var color = (MediaColor)ColorConverter.ConvertFromString(hex)!;
            var inner = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(color),
            };
            var ring = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderBrush = MediaBrushes.Transparent,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Child = inner,
            };
            ring.MouseLeftButtonDown += Swatch_Click;
            SwatchPanel.Children.Add(ring);
        }

        SelectSwatchByBrush(_color);
    }

    private void BuildFonts()
    {
        string[] fonts = { "Segoe UI", "Arial", "Calibri", "Times New Roman", "Georgia", "Verdana", "Consolas", "Comic Sans MS", "Impact", "Courier New" };
        foreach (var name in fonts)
        {
            FontCombo.Items.Add(new ComboBoxItem { Content = name, FontFamily = new FontFamily(name) });
        }
        FontCombo.SelectedIndex = 0;
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        var ring = (Border)sender;
        var brush = (MediaBrush)((Border)ring.Child).Background;
        HighlightSwatch(ring);

        if (_syncingProps)
        {
            return;
        }

        _color = brush;
        if (_selected is WpfShape shape)
        {
            shape.Stroke = brush;
            _dirty = true;
        }
        else if (_selected is TextBlock text)
        {
            text.Foreground = brush;
            _dirty = true;
        }
    }

    private void SelectSwatchByBrush(MediaBrush brush)
    {
        foreach (Border ring in SwatchPanel.Children)
        {
            var inner = (Border)ring.Child;
            if (brush is SolidColorBrush a && ((SolidColorBrush)inner.Background).Color == a.Color)
            {
                HighlightSwatch(ring);
                return;
            }
        }

        HighlightSwatch(null);
    }

    private void HighlightSwatch(Border? selected)
    {
        foreach (Border ring in SwatchPanel.Children)
        {
            ring.BorderBrush = ReferenceEquals(ring, selected) ? MediaBrushes.White : MediaBrushes.Transparent;
        }
    }

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles)
        {
            return;
        }

        var chosen = (ToggleButton)sender;

        _updatingToggles = true;
        foreach (var other in _toolButtons)
        {
            if (!ReferenceEquals(other, chosen))
            {
                other.IsChecked = false;
            }
        }
        _updatingToggles = false;

        _tool = (string)chosen.Tag switch
        {
            "Arrow" => Tool.Arrow,
            "Rectangle" => Tool.Rectangle,
            "Text" => Tool.Text,
            _ => Tool.Select,
        };

        CommitActiveText();
        if (_tool != Tool.Select)
        {
            Select(null);
        }
        UpdatePropertyPanels();
    }

    private void Tool_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingToggles)
        {
            return;
        }

        if (_toolButtons.All(b => b.IsChecked != true))
        {
            _updatingToggles = true;
            SelectTool.IsChecked = true;
            _updatingToggles = false;
            _tool = Tool.Select;
            UpdatePropertyPanels();
        }
    }

    private void UpdatePropertyPanels()
    {
        bool textCtx = _selected is TextBlock || (_selected is null && _tool == Tool.Text);
        bool shapeCtx = _selected is WpfShape || (_selected is null && _tool is Tool.Arrow or Tool.Rectangle);

        ShapeGroup.Visibility = shapeCtx ? Visibility.Visible : Visibility.Collapsed;
        TextGroup.Visibility = textCtx ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Thickness_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThicknessCombo.SelectedItem is not ComboBoxItem item
            || !double.TryParse((string)item.Content, out var value) || _syncingProps)
        {
            return;
        }

        _thickness = value;
        if (_selected is WpfPath path && _arrowPoints.TryGetValue(path, out var pts))
        {
            path.StrokeThickness = value;
            path.Data = BuildArrow(pts.from, pts.to, value);
            _dirty = true;
            UpdateSelectionOverlay();
        }
        else if (_selected is WpfShape shape)
        {
            shape.StrokeThickness = value;
            _dirty = true;
        }
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FontCombo.SelectedItem is not ComboBoxItem item || _syncingProps)
        {
            return;
        }

        _fontFamily = new FontFamily((string)item.Content);
        if (_selected is TextBlock text)
        {
            text.FontFamily = _fontFamily;
            _dirty = true;
            UpdateSelectionOverlay();
        }
    }

    private void TextSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TextSizeCombo.SelectedItem is not ComboBoxItem item
            || !double.TryParse((string)item.Content, out var value) || _syncingProps)
        {
            return;
        }

        _fontSize = value;
        if (_selected is TextBlock text)
        {
            text.FontSize = value;
            _dirty = true;
            UpdateSelectionOverlay();
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        CommitActiveText();
        if (_annotations.Count == 0)
        {
            return;
        }

        var last = _annotations[^1];
        if (ReferenceEquals(last, _selected))
        {
            Select(null);
        }
        RemoveAnnotation(last);
        _dirty = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selected is not null && _activeText is null)
        {
            DeleteSelected();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    private void DeleteSelected()
    {
        if (_selected is null)
        {
            return;
        }

        var target = _selected;
        Select(null);
        RemoveAnnotation(target);
        _dirty = true;
    }

    private void RemoveAnnotation(UIElement element)
    {
        _annotations.Remove(element);
        AnnotationCanvas.Children.Remove(element);
        if (element is WpfPath path)
        {
            _arrowPoints.Remove(path);
        }
    }

    private void AddAnnotation(UIElement element)
    {
        element.RenderTransform = new MatrixTransform(Matrix.Identity);
        _annotations.Add(element);
        AnnotationCanvas.Children.Add(element);
        _dirty = true;
    }

    // --- Mouse: draw new, or select / move existing ---

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(AnnotationCanvas);

        // Clicking on / near an existing object grabs it (any tool).
        var hit = HitAnnotation(p);
        if (hit is not null)
        {
            Select(hit);
            _movingBody = true;
            _lastMove = p;
            AnnotationCanvas.CaptureMouse();
            return;
        }

        if (_tool == Tool.Select)
        {
            Select(null);
            return;
        }

        if (_tool == Tool.Text)
        {
            BeginText(p);
            return;
        }

        _start = p;
        _drawing = true;

        if (_tool == Tool.Rectangle)
        {
            var rect = new WpfRectangle
            {
                Stroke = _color,
                StrokeThickness = _thickness,
                Fill = MediaBrushes.Transparent,
            };
            AddAnnotation(rect);
            _active = rect;
        }
        else
        {
            var path = new WpfPath
            {
                Stroke = _color,
                StrokeThickness = _thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            AddAnnotation(path);
            _active = path;
        }

        AnnotationCanvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(AnnotationCanvas);

        if (_movingBody && _selected is not null)
        {
            var m = ElemMatrix(_selected);
            m.Translate(p.X - _lastMove.X, p.Y - _lastMove.Y);
            SetElemMatrix(_selected, m);
            _lastMove = p;
            UpdateSelectionOverlay();
            _dirty = true;
            return;
        }

        if (!_drawing || _active is null)
        {
            return;
        }

        if (_active is WpfRectangle rect)
        {
            double x = Math.Min(_start.X, p.X);
            double y = Math.Min(_start.Y, p.Y);
            rect.Width = Math.Abs(p.X - _start.X);
            rect.Height = Math.Abs(p.Y - _start.Y);
            SetElemMatrix(rect, new Matrix(1, 0, 0, 1, x, y));
        }
        else if (_active is WpfPath path)
        {
            path.Data = BuildArrow(_start, p, _thickness);
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_movingBody)
        {
            _movingBody = false;
            AnnotationCanvas.ReleaseMouseCapture();
            return;
        }

        if (!_drawing)
        {
            return;
        }

        _drawing = false;
        AnnotationCanvas.ReleaseMouseCapture();

        if (_active is null)
        {
            return;
        }

        var end = e.GetPosition(AnnotationCanvas);
        bool tiny = _active is WpfRectangle rect
            ? rect.Width < 3 || rect.Height < 3
            : (end - _start).Length < 5;

        if (tiny)
        {
            RemoveAnnotation(_active);
        }
        else if (_active is WpfPath path)
        {
            _arrowPoints[path] = (_start, end);
        }

        _active = null;
    }

    private UIElement? HitAnnotation(Point p)
    {
        UIElement? found = null;
        var region = new EllipseGeometry(p, HitTolerance, HitTolerance);
        VisualTreeHelper.HitTest(
            AnnotationCanvas,
            null,
            result =>
            {
                var el = FindAnnotationAncestor(result.VisualHit);
                if (el is not null)
                {
                    found = el;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            },
            new GeometryHitTestParameters(region));
        return found;
    }

    private UIElement? FindAnnotationAncestor(DependencyObject? obj)
    {
        while (obj is not null)
        {
            if (obj is UIElement el && _annotations.Contains(el))
            {
                return el;
            }
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    // --- Selection + resize/rotate handles ---

    private void Select(UIElement? element)
    {
        _selected = element;
        if (element is not null)
        {
            SyncPropsToSelection(element);
        }
        UpdateSelectionOverlay();
        UpdatePropertyPanels();
    }

    private static Matrix ElemMatrix(UIElement el) => ((MatrixTransform)el.RenderTransform).Matrix;

    private static void SetElemMatrix(UIElement el, Matrix m) => ((MatrixTransform)el.RenderTransform).Matrix = m;

    private static Rect LocalBounds(UIElement el)
        => el is WpfPath p && p.Data is not null ? p.Data.Bounds : new Rect(new Point(0, 0), el.RenderSize);

    private static Point[] LocalCorners(UIElement el)
    {
        var b = LocalBounds(el);
        return new[]
        {
            new Point(b.Left, b.Top),
            new Point(b.Right, b.Top),
            new Point(b.Right, b.Bottom),
            new Point(b.Left, b.Bottom),
        };
    }

    private void UpdateSelectionOverlay()
    {
        if (_selected is null)
        {
            SetOverlayVisible(false);
            return;
        }

        var m = ElemMatrix(_selected);
        var local = LocalCorners(_selected);
        var c0 = m.Transform(local[0]);
        var c1 = m.Transform(local[1]);
        var c2 = m.Transform(local[2]);
        var c3 = m.Transform(local[3]);

        _outline.Points = new PointCollection { c0, c1, c2, c3 };

        PlaceHandle(_cornerHandles[0], c0);
        PlaceHandle(_cornerHandles[1], c1);
        PlaceHandle(_cornerHandles[2], c2);
        PlaceHandle(_cornerHandles[3], c3);

        var lb = LocalBounds(_selected);
        var center = m.Transform(new Point((lb.Left + lb.Right) / 2, (lb.Top + lb.Bottom) / 2));
        var topMid = new Point((c0.X + c1.X) / 2, (c0.Y + c1.Y) / 2);
        var dir = topMid - center;
        double dist = dir.Length;
        if (dist > 0.01)
        {
            dir /= dist;
        }
        var rotatePos = topMid + dir * 22;
        PlaceHandle(_rotateHandle, rotatePos);

        SetOverlayVisible(true);
    }

    private static void PlaceHandle(FrameworkElement handle, Point center)
    {
        Canvas.SetLeft(handle, center.X - handle.Width / 2);
        Canvas.SetTop(handle, center.Y - handle.Height / 2);
    }

    private void SetOverlayVisible(bool visible)
    {
        foreach (var el in _overlay)
        {
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var handle = (FrameworkElement)sender;
        _m0 = ElemMatrix(_selected);
        var local = LocalCorners(_selected);

        if (ReferenceEquals(handle, _rotateHandle))
        {
            _manip = Manip.Rotate;
            var lb = LocalBounds(_selected);
            _rotCenter = _m0.Transform(new Point((lb.Left + lb.Right) / 2, (lb.Top + lb.Bottom) / 2));
            var mp = e.GetPosition(AnnotationCanvas);
            _rotStartAngle = Math.Atan2(mp.Y - _rotCenter.Y, mp.X - _rotCenter.X);
        }
        else
        {
            _manip = Manip.Resize;
            int i = (int)handle.Tag;
            _cornerLocal = local[i];
            _anchorLocal = local[(i + 2) % 4];
        }

        handle.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_manip == Manip.None || _selected is null)
        {
            return;
        }

        var p = e.GetPosition(AnnotationCanvas);
        if (_manip == Manip.Resize)
        {
            ApplyResize(p);
        }
        else
        {
            ApplyRotate(p);
        }

        UpdateSelectionOverlay();
        _dirty = true;
        e.Handled = true;
    }

    private void Handle_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_manip == Manip.None)
        {
            return;
        }

        _manip = Manip.None;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ApplyResize(Point mouseCanvas)
    {
        var inv = _m0;
        if (!inv.HasInverse)
        {
            return;
        }
        inv.Invert();
        var ml = inv.Transform(mouseCanvas);

        double dx = _cornerLocal.X - _anchorLocal.X;
        double dy = _cornerLocal.Y - _anchorLocal.Y;
        double sx = Math.Abs(dx) < 0.001 ? 1 : (ml.X - _anchorLocal.X) / dx;
        double sy = Math.Abs(dy) < 0.001 ? 1 : (ml.Y - _anchorLocal.Y) / dy;
        sx = Math.Max(0.05, sx);
        sy = Math.Max(0.05, sy);

        // Hold Shift to keep the aspect ratio (uniform scale).
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            double uniform = Math.Max(sx, sy);
            sx = uniform;
            sy = uniform;
        }

        var s = Matrix.Identity;
        s.ScaleAt(sx, sy, _anchorLocal.X, _anchorLocal.Y);
        SetElemMatrix(_selected!, Matrix.Multiply(s, _m0));
    }

    private void ApplyRotate(Point mouseCanvas)
    {
        double angle = Math.Atan2(mouseCanvas.Y - _rotCenter.Y, mouseCanvas.X - _rotCenter.X);
        double deltaDeg = (angle - _rotStartAngle) * 180 / Math.PI;

        var r = Matrix.Identity;
        r.RotateAt(deltaDeg, _rotCenter.X, _rotCenter.Y);
        SetElemMatrix(_selected!, Matrix.Multiply(_m0, r));
    }

    private void SyncPropsToSelection(UIElement element)
    {
        _syncingProps = true;

        if (element is WpfShape shape)
        {
            SelectSwatchByBrush(shape.Stroke);
            SelectComboByValue(ThicknessCombo, shape.StrokeThickness);
        }
        else if (element is TextBlock text)
        {
            SelectSwatchByBrush(text.Foreground);
            SelectComboByValue(TextSizeCombo, text.FontSize);
            SelectFont(text.FontFamily);
        }

        _syncingProps = false;
    }

    private static void SelectComboByValue(ComboBox combo, double value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (double.TryParse((string)item.Content, out var v) && Math.Abs(v - value) < 0.01)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void SelectFont(FontFamily family)
    {
        foreach (ComboBoxItem item in FontCombo.Items)
        {
            if ((string)item.Content == family.Source)
            {
                FontCombo.SelectedItem = item;
                return;
            }
        }
    }

    private static Geometry BuildArrow(Point from, Point to, double thickness)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.LineTo(to, true, false);

            double angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
            double len = Math.Min(Math.Max(12, thickness * 4), (to - from).Length * 0.5);
            const double spread = 0.45;

            var h1 = new Point(to.X - len * Math.Cos(angle - spread), to.Y - len * Math.Sin(angle - spread));
            var h2 = new Point(to.X - len * Math.Cos(angle + spread), to.Y - len * Math.Sin(angle + spread));

            ctx.BeginFigure(to, false, false);
            ctx.LineTo(h1, true, false);
            ctx.BeginFigure(to, false, false);
            ctx.LineTo(h2, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    // --- Text ---

    private void BeginText(Point p)
    {
        CommitActiveText();

        var box = new TextBox
        {
            MinWidth = 40,
            Background = MediaBrushes.Transparent,
            Foreground = _color,
            CaretBrush = _color,
            BorderBrush = (MediaBrush)System.Windows.Application.Current.Resources["Brush.Accent"],
            BorderThickness = new Thickness(1),
            FontSize = _fontSize,
            FontWeight = FontWeights.Bold,
            FontFamily = _fontFamily,
        };
        Canvas.SetLeft(box, p.X);
        Canvas.SetTop(box, p.Y);
        AnnotationCanvas.Children.Add(box);

        _activeText = box;
        box.Focus();
        box.LostKeyboardFocus += (_, _) => CommitActiveText();
        box.KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Enter)
            {
                CommitActiveText();
                ev.Handled = true;
            }
            else if (ev.Key == Key.Escape)
            {
                CancelActiveText();
                ev.Handled = true;
            }
        };
    }

    private void CancelActiveText()
    {
        if (_activeText is null)
        {
            return;
        }

        AnnotationCanvas.Children.Remove(_activeText);
        _activeText = null;
    }

    private void CommitActiveText()
    {
        if (_activeText is null)
        {
            return;
        }

        var box = _activeText;
        _activeText = null;

        double x = Canvas.GetLeft(box);
        double y = Canvas.GetTop(box);
        string text = box.Text;
        AnnotationCanvas.Children.Remove(box);

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var label = new TextBlock
        {
            Text = text,
            Foreground = box.Foreground,
            FontSize = box.FontSize,
            FontWeight = FontWeights.Bold,
            FontFamily = box.FontFamily,
        };
        AddAnnotation(label);
        SetElemMatrix(label, new Matrix(1, 0, 0, 1, x + 3, y + 2));
    }

    // --- Output ---

    private BitmapSource RenderEdited()
    {
        CommitActiveText();

        SetOverlayVisible(false); // never bake the selection handles in
        EditorSurface.UpdateLayout();

        double w = EditorSurface.ActualWidth;
        double h = EditorSurface.ActualHeight;
        BitmapSource result;
        if (w < 1 || h < 1)
        {
            result = (BitmapSource)PreviewImage.Source;
        }
        else
        {
            double dpi = 96.0 * (_bitmap.Width / w);
            var target = new RenderTargetBitmap(_bitmap.Width, _bitmap.Height, dpi, dpi, PixelFormats.Pbgra32);
            target.Render(EditorSurface);
            target.Freeze();
            result = target;
        }

        if (_selected is not null)
        {
            UpdateSelectionOverlay();
        }
        return result;
    }

    private static void WritePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private bool SaveToOriginal()
    {
        try
        {
            WritePng(RenderEdited(), _savedPath);
            _dirty = false;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save:\n{ex.Message}", "ScreenSnap", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private bool SaveCopy()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = Path.GetFileName(_savedPath),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        WritePng(RenderEdited(), dialog.FileName);
        _dirty = false;
        return true;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
        => Clipboard.SetImage(RenderEdited());

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_savedPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_savedPath}\"") { UseShellExecute = true });
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveCopy();

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitActiveText();

        if (_dirty)
        {
            switch (AskSaveChanges())
            {
                case SaveChoice.Save:
                    if (!SaveToOriginal()) e.Cancel = true;
                    break;
                case SaveChoice.SaveCopy:
                    if (!SaveCopy()) e.Cancel = true;
                    break;
                case SaveChoice.Discard:
                    break;
                default:
                    e.Cancel = true;
                    break;
            }
        }

        base.OnClosing(e);
    }

    private enum SaveChoice { Save, SaveCopy, Discard, Cancel }

    private SaveChoice AskSaveChanges()
    {
        var choice = SaveChoice.Cancel;

        var dialog = new Window
        {
            Title = "Save changes?",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = (MediaBrush)System.Windows.Application.Current.Resources["Brush.App"],
            Foreground = (MediaBrush)System.Windows.Application.Current.Resources["Brush.TextPrimary"],
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
        };

        var message = new TextBlock
        {
            Text = "You've edited this capture. Save your changes?",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(MakeChoiceButton("Save", "AccentButton", () => { choice = SaveChoice.Save; dialog.Close(); }));
        buttons.Children.Add(MakeChoiceButton("Save a copy…", "SubtleButton", () => { choice = SaveChoice.SaveCopy; dialog.Close(); }));
        buttons.Children.Add(MakeChoiceButton("Don't save", "SubtleButton", () => { choice = SaveChoice.Discard; dialog.Close(); }));
        buttons.Children.Add(MakeChoiceButton("Cancel", "SubtleButton", () => { choice = SaveChoice.Cancel; dialog.Close(); }));

        var panel = new StackPanel();
        panel.Children.Add(message);
        panel.Children.Add(buttons);
        dialog.Content = new Border { Padding = new Thickness(20), Child = panel };

        DwmHelper.ApplyPowerToysChrome(dialog);
        dialog.ShowDialog();
        return choice;
    }

    private static Button MakeChoiceButton(string text, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)System.Windows.Application.Current.Resources[styleKey],
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
