using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Muro.Core.Schedule;
using Windows.UI;

namespace Muro.App;

/// A 24-hour day laid out left to right, one coloured block per wallpaper. Drag the edge of a block to
/// change its hours; an edge shared with the next block takes that block edge along, and a block cannot
/// be squeezed below 15 minutes or pushed over another (the rules live in Automation.MovingEdge, which is
/// unit tested). Gaps are shown, because a gap keeps whatever was playing.
internal sealed class DayTimeline : Canvas
{
    private const double RulerHeight = 22, BlockTop = 28, BlockHeight = 62, HandleWidth = 9;
    private const int SnapMinutes = 5;
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 74, 144, 217), Color.FromArgb(255, 156, 108, 216), Color.FromArgb(255, 226, 133, 74),
        Color.FromArgb(255, 82, 176, 120), Color.FromArgb(255, 214, 92, 120), Color.FromArgb(255, 200, 170, 60),
        Color.FromArgb(255, 70, 178, 190), Color.FromArgb(255, 150, 150, 160),
    ];

    private List<Step> _steps = [];
    private readonly List<Element> _elements = [];
    private readonly List<Rectangle> _gapShapes = [];
    private readonly List<(TextBlock Label, double Hour)> _ruler = [];
    private Rectangle? _now;
    private (Step Step, ClockEdge Edge, double StartX, List<Step> Snapshot)? _drag;

    private sealed class Element
    {
        public required string StepId;
        public required Border Block;
        public required Border Tail;      // second half of a window that runs past midnight
        public required Rectangle Left, Right;
    }

    public DayTimeline()
    {
        Height = BlockTop + BlockHeight + 8;
        SizeChanged += (_, _) => Relayout();
    }

    public Func<string, string> NameOf { get; set; } = id => id;
    public string? SelectedId { get; private set; }
    public event Action<List<Step>>? StepsChanged;
    public event Action<string?>? SelectionChanged;

    public List<Step> Steps
    {
        get => _steps;
        set { _steps = value.ToList(); Rebuild(); }
    }

    public void Select(string? id)
    {
        SelectedId = id;
        Relayout();
    }

    // ---- Building and laying out

    private void Rebuild()
    {
        Children.Clear();
        _elements.Clear();
        _gapShapes.Clear();
        _ruler.Clear();

        for (var hour = 0; hour <= 24; hour += 3)
        {
            var label = new TextBlock { Text = hour == 24 ? "24" : hour.ToString("00"), FontSize = 11, Opacity = 0.65 };
            Children.Add(label);
            _ruler.Add((label, hour));
        }
        // The gaps sit under the blocks.
        for (var i = 0; i < 4; i++)
        {
            var gap = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), Visibility = Visibility.Collapsed, RadiusX = 4, RadiusY = 4 };
            Children.Add(gap);
            _gapShapes.Add(gap);
        }
        foreach (var step in _steps) MakeElement(step);
        // Every handle above every block, or the next block covers the edge it shares with this one.
        foreach (var element in _elements)
        {
            Children.Add(element.Left);
            Children.Add(element.Right);
        }
        _now = new Rectangle { Width = 2, Height = BlockHeight + 8, Fill = new SolidColorBrush(Colors.White), Opacity = 0.9, IsHitTestVisible = false };
        Children.Add(_now);
        Relayout();
    }

    private Element MakeElement(Step step)
    {
        Border Block(bool tail)
        {
            var block = new Border { CornerRadius = new CornerRadius(6), Child = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Colors.White), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 6, 8, 4), TextWrapping = TextWrapping.Wrap } };
            block.Tapped += (_, _) => { SelectedId = step.Id; Relayout(); SelectionChanged?.Invoke(step.Id); };
            return block;
        }
        Rectangle Handle(ClockEdge edge)
        {
            var handle = new Rectangle { Width = HandleWidth, Fill = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), RadiusX = 3, RadiusY = 3 };
            handle.PointerPressed += (s, e) => BeginDrag(step.Id, edge, e);
            handle.PointerMoved += (s, e) => Drag(e);
            handle.PointerReleased += (s, e) => EndDrag((UIElement)s, e);
            handle.PointerCanceled += (s, e) => EndDrag((UIElement)s, e);
            return handle;
        }
        var element = new Element { StepId = step.Id, Block = Block(false), Tail = Block(true), Left = Handle(ClockEdge.Start), Right = Handle(ClockEdge.End) };
        Children.Add(element.Block);
        Children.Add(element.Tail);
        _elements.Add(element);
        return element;
    }

    private double Pixels(double minutes) => minutes * ActualWidth / 1440.0;

    private void Relayout()
    {
        if (ActualWidth <= 0) return;
        foreach (var (label, hour) in _ruler)
        {
            SetLeft(label, Math.Clamp(Pixels(hour * 60) - (hour == 24 ? 14 : hour == 0 ? 0 : 6), 0, ActualWidth - 14));
            SetTop(label, 0);
        }

        foreach (var element in _elements)
        {
            var step = _steps.FirstOrDefault(s => s.Id == element.StepId);
            if (step is null) continue;
            var allDay = Automation.IsAllDay(step);
            // A window that ends at 24:00 is stored as ending at 00:00. That is the end of the day, not a wrap.
            var endMinute = step.End == 0 ? 1440 : step.End;
            var wraps = !allDay && step.End != 0 && step.End < step.Start;
            var color = Palette[Math.Max(0, _steps.IndexOf(step)) % Palette.Length];
            var selected = step.Id == SelectedId;
            var text = NameOf(step.WallpaperID) + "\n" + Automation.ClockLabel(step.Start) + " - " + Automation.ClockLabel(step.End);
            if (allDay) text = NameOf(step.WallpaperID) + "\nall day";

            Place(element.Block, allDay ? 0 : step.Start, allDay ? 1440 : wraps ? 1440 : endMinute);
            Place(element.Tail, 0, wraps ? step.End : 0);
            element.Tail.Visibility = wraps ? Visibility.Visible : Visibility.Collapsed;
            foreach (var block in new[] { element.Block, element.Tail })
            {
                block.Background = new SolidColorBrush(color);
                block.BorderBrush = new SolidColorBrush(selected ? Colors.White : Color.FromArgb(0, 0, 0, 0));
                block.BorderThickness = new Thickness(selected ? 2 : 0);
                ToolTipService.SetToolTip(block, text.Replace("\n", "  "));
                ((TextBlock)block.Child).Text = text;
            }
            ((TextBlock)element.Tail.Child).Text = "(continued)";

            // An all-day block has no edge to drag.
            element.Left.Visibility = element.Right.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
            if (!allDay)
            {
                PlaceHandle(element.Left, Pixels(step.Start));
                PlaceHandle(element.Right, Pixels(wraps ? step.End : endMinute));
            }
        }

        var gaps = new Automation { Mode = AutomationMode.Clock, Steps = _steps }.UncoveredWindows();
        var shape = 0;
        foreach (var (start, end) in gaps)
        {
            var spans = end > start ? new[] { (start, end) } : new[] { (start, 1440), (0, end) };
            foreach (var (a, b) in spans)
            {
                if (shape >= _gapShapes.Count || b <= a) continue;
                var rect = _gapShapes[shape++];
                rect.Visibility = Visibility.Visible;
                SetLeft(rect, Pixels(a));
                SetTop(rect, BlockTop);
                rect.Width = Math.Max(0, Pixels(b - a));
                rect.Height = BlockHeight;
            }
        }
        for (; shape < _gapShapes.Count; shape++) _gapShapes[shape].Visibility = Visibility.Collapsed;

        if (_now is not null)
        {
            var now = DateTime.Now;
            SetLeft(_now, Pixels(now.Hour * 60 + now.Minute) - 1);
            SetTop(_now, BlockTop - 4);
        }
    }

    private void Place(Border block, double fromMinute, double toMinute)
    {
        SetLeft(block, Pixels(fromMinute));
        SetTop(block, BlockTop);
        block.Width = Math.Max(0, Pixels(toMinute - fromMinute));
        block.Height = BlockHeight;
    }

    private void PlaceHandle(Rectangle handle, double x)
    {
        SetLeft(handle, Math.Clamp(x - HandleWidth / 2, 0, Math.Max(0, ActualWidth - HandleWidth)));
        SetTop(handle, BlockTop + 8);
        handle.Height = BlockHeight - 16;
    }

    // ---- Dragging an edge

    private void BeginDrag(string stepId, ClockEdge edge, PointerRoutedEventArgs e)
    {
        var step = _steps.FirstOrDefault(s => s.Id == stepId);
        if (step is null) return;
        _drag = (step, edge, e.GetCurrentPoint(this).Position.X, _steps.ToList());
        ((UIElement)e.OriginalSource).CapturePointer(e.Pointer);
        SelectedId = stepId;
        SelectionChanged?.Invoke(stepId);
        e.Handled = true;
    }

    private void Drag(PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || ActualWidth <= 0) return;
        var minutes = (e.GetCurrentPoint(this).Position.X - drag.StartX) / ActualWidth * 1440.0;
        var snapped = (int)Math.Round(minutes / SnapMinutes) * SnapMinutes;
        // Always from where the drag started, so repeated moves never accumulate rounding.
        _steps = Automation.MovingEdge(drag.Edge, drag.Step.Id, snapped, drag.Snapshot);
        Relayout();
        e.Handled = true;
    }

    private void EndDrag(UIElement handle, PointerRoutedEventArgs e)
    {
        if (_drag is null) return;
        _drag = null;
        handle.ReleasePointerCapture(e.Pointer);
        StepsChanged?.Invoke(_steps.ToList());
        e.Handled = true;
    }
}
