using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using XtermSharp;
using Point = Avalonia.Point;

namespace AvaloniaTerminal;

public partial class TerminalControl : Grid
{
    private const double ScrollBarWidth = 16;
    private static readonly TimeSpan SelectionAutoScrollInterval = TimeSpan.FromMilliseconds(80);

    private Size _consoleTextSize;

    private Typeface _typeface;

    private readonly TerminalSurface _surface;

    private readonly ScrollBar _verticalScrollBar;

    private bool _canRenderText;

    private bool _hasFocus;

    private bool _isUpdatingScrollBar;

    private bool _didSelectionDrag;

    private bool _selectionPointerCaptured;

    private int _selectionClickCount;

    private int _selectionAutoScrollDelta;

    private Point _lastSelectionPointerPosition;

    private bool _terminalMouseCaptured;

    private int _activeTerminalMouseButton;

    private string _selectedText = string.Empty;

    private bool _hasSelection;

    private readonly DispatcherTimer _selectionAutoScrollTimer;

    public TerminalControl()
    {
        Focusable = true;

        ColumnDefinitions =
        [
            new ColumnDefinition(1, GridUnitType.Star),
            new ColumnDefinition(ScrollBarWidth, GridUnitType.Pixel),
        ];

        _surface = new TerminalSurface(this);
        SetColumn(_surface, 0);
        Children.Add(_surface);

        _verticalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            SmallChange = 1,
            Visibility = ScrollBarVisibility.Visible,
            AllowAutoHide = false,
        };
        SetColumn(_verticalScrollBar, 1);
        _verticalScrollBar.ValueChanged += OnVerticalScrollBarValueChanged;
        Children.Add(_verticalScrollBar);

        _selectionAutoScrollTimer = new DispatcherTimer
        {
            Interval = SelectionAutoScrollInterval,
        };
        _selectionAutoScrollTimer.Tick += OnSelectionAutoScrollTimerTick;

        CalculateTextSize();
        UpdateScrollBar();
    }

    public static readonly StyledProperty<TerminalControlModel?> ModelProperty = AvaloniaProperty.Register<TerminalControl, TerminalControlModel?>(nameof(Model));

    public TerminalControlModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public static readonly StyledProperty<string> FontFamilyProperty = AvaloniaProperty.Register<TerminalControl, string>(nameof(FontFamily), "Cascadia Mono");

    public string FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), 12);

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public static readonly StyledProperty<IBrush?> CaretBrushProperty = AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(CaretBrush));

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<TerminalControl, IBrush?>(nameof(SelectionBrush));

    public static readonly StyledProperty<RightClickAction> RightClickActionProperty =
        AvaloniaProperty.Register<TerminalControl, RightClickAction>(nameof(RightClickAction), RightClickAction.ContextMenu);

    public static readonly DirectProperty<TerminalControl, string> SelectedTextProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, string>(nameof(SelectedText), o => o.SelectedText);

    public static readonly DirectProperty<TerminalControl, bool> HasSelectionProperty =
        AvaloniaProperty.RegisterDirect<TerminalControl, bool>(nameof(HasSelection), o => o.HasSelection);

    public IBrush? CaretBrush
    {
        get => GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    public RightClickAction RightClickAction
    {
        get => GetValue(RightClickActionProperty);
        set => SetValue(RightClickActionProperty, value);
    }

    public string SelectedText
    {
        get => _selectedText;
        private set => SetAndRaise(SelectedTextProperty, ref _selectedText, value);
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetAndRaise(HasSelectionProperty, ref _hasSelection, value);
    }

    public bool IsMouseModeActive => Model?.IsMouseModeActive ?? false;

    public new event EventHandler<TerminalContextRequestedEventArgs>? ContextRequested;

    internal Func<Task<string?>>? ClipboardTextReaderOverride { get; set; }

    internal Func<string, Task>? ClipboardTextWriterOverride { get; set; }

    public void SelectAll()
    {
        Model?.SelectAll();
    }

    public string CopySelection()
    {
        return SelectedText;
    }

    public void Paste(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Model?.Send(text);
        }
    }

    public async Task CopySelectionAsync()
    {
        if (!HasSelection)
        {
            return;
        }

        if (ClipboardTextWriterOverride != null)
        {
            await ClipboardTextWriterOverride(SelectedText).ConfigureAwait(true);
            return;
        }

        var clipboard = ResolveClipboard();
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(SelectedText).ConfigureAwait(true);
        }
    }

    public async Task PasteFromClipboardAsync()
    {
        string? text;
        if (ClipboardTextReaderOverride != null)
        {
            text = await ClipboardTextReaderOverride().ConfigureAwait(true);
        }
        else
        {
            var clipboard = ResolveClipboard();
            if (clipboard == null)
            {
                return;
            }
#pragma warning disable CS0618
            text = await clipboard.GetTextAsync().ConfigureAwait(true);
#pragma warning restore CS0618
        }

        if (!string.IsNullOrEmpty(text))
        {
            Paste(text);
        }
    }

    public int Search(string text)
    {
        return Model?.Search(text) ?? 0;
    }

    public int SelectNextSearchResult()
    {
        return Model?.SelectNextSearchResult() ?? -1;
    }

    public int SelectPreviousSearchResult()
    {
        return Model?.SelectPreviousSearchResult() ?? -1;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ModelProperty)
        {
            if (change.OldValue is TerminalControlModel oldModel && oldModel.UpdateUI == RefreshFromModel)
            {
                oldModel.UpdateUI = null;
            }

            if (change.NewValue is TerminalControlModel newModel)
            {
                newModel.UpdateUI = RefreshFromModel;
            }

            SyncSelectionStateFromModel();
            UpdateScrollBar();
            ResizeModelToViewport();
            _surface.InvalidateVisual();
        }

        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            CalculateTextSize();
            ResizeModelToViewport();
            _surface.InvalidateVisual();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Tab)
        {
            e.Handled = true; // Prevent further processing of the Tab key
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (Model == null)
        {
            return;
        }

        Model.ClearSelection();

        if (e.KeyModifiers is KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.A:
                    Model.Send([0x01]);  // Ctrl+A
                    break;
                case Key.B:
                    Model.Send([0x02]);  // Ctrl+B
                    break;
                case Key.C:
                    Model.Send([0x03]);  // Ctrl+C
                    break;
                case Key.D:
                    Model.Send([0x04]);  // Ctrl+D
                    break;
                case Key.E:
                    Model.Send([0x05]);  // Ctrl+E
                    break;
                case Key.F:
                    Model.Send([0x06]);  // Ctrl+F
                    break;
                case Key.G:
                    Model.Send([0x07]);  // Ctrl+G
                    break;
                case Key.H:
                    Model.Send([0x08]);  // Ctrl+H
                    break;
                case Key.I:
                    Model.Send([0x09]);  // Ctrl+I (Tab)
                    break;
                case Key.J:
                    Model.Send([0x0A]);  // Ctrl+J (Line Feed)
                    break;
                case Key.K:
                    Model.Send([0x0B]);  // Ctrl+K
                    break;
                case Key.L:
                    Model.Send([0x0C]);  // Ctrl+L
                    break;
                case Key.M:
                    Model.Send([0x0D]);  // Ctrl+M (Carriage Return)
                    break;
                case Key.N:
                    Model.Send([0x0E]);  // Ctrl+N
                    break;
                case Key.O:
                    Model.Send([0x0F]);  // Ctrl+O
                    break;
                case Key.P:
                    Model.Send([0x10]);  // Ctrl+P
                    break;
                case Key.Q:
                    Model.Send([0x11]);  // Ctrl+Q
                    break;
                case Key.R:
                    Model.Send([0x12]);  // Ctrl+R
                    break;
                case Key.S:
                    Model.Send([0x13]);  // Ctrl+S
                    break;
                case Key.T:
                    Model.Send([0x14]);  // Ctrl+T
                    break;
                case Key.U:
                    Model.Send([0x15]);  // Ctrl+U
                    break;
                case Key.V:
                    //_ = dc1.Paste(]);
                    Model.Send([0x16]);  // Ctrl+V
                    break;
                case Key.W:
                    Model.Send([0x17]);  // Ctrl+W
                    break;
                case Key.X:
                    Model.Send([0x18]);  // Ctrl+X
                    break;
                case Key.Y:
                    Model.Send([0x19]);  // Ctrl+Y
                    break;
                case Key.Z:
                    Model.Send([0x1A]);  // Ctrl+Z
                    break;
                case Key.D1: // Ctrl+1
                    Model.Send([0x31]);  // ASCII '1'
                    break;
                case Key.D2: // Ctrl+2
                    Model.Send([0x32]);  // ASCII '2'
                    break;
                case Key.D3: // Ctrl+3
                    Model.Send([0x33]);  // ASCII '3'
                    break;
                case Key.D4: // Ctrl+4
                    Model.Send([0x34]);  // ASCII '4'
                    break;
                case Key.D5: // Ctrl+5
                    Model.Send([0x35]);  // ASCII '5'
                    break;
                case Key.D6: // Ctrl+6
                    Model.Send([0x36]);  // ASCII '6'
                    break;
                case Key.D7: // Ctrl+7
                    Model.Send([0x37]);  // ASCII '7'
                    break;
                case Key.D8: // Ctrl+8
                    Model.Send([0x38]);  // ASCII '8'
                    break;
                case Key.D9: // Ctrl+9
                    Model.Send([0x39]);  // ASCII '9'
                    break;
                case Key.D0: // Ctrl+0
                    Model.Send([0x30]);  // ASCII '0'
                    break;
                case Key.OemOpenBrackets: // Ctrl+[
                    Model.Send([0x1B]);
                    break;
                case Key.OemBackslash: // Ctrl+\
                    Model.Send([0x1C]);
                    break;
                case Key.OemCloseBrackets: // Ctrl+]
                    Model.Send([0x1D]);
                    break;
                case Key.Space: // Ctrl+Space
                    Model.Send([0x00]);
                    break;
                case Key.OemMinus: // Ctrl+_
                    Model.Send([0x1F]);
                    break;
                default:
                    if (!string.IsNullOrEmpty(e.KeySymbol))
                    {
                        Model.Send(e.KeySymbol);
                    }
                    break;
            }
        }
        if (e.KeyModifiers is KeyModifiers.Alt)
        {
            if (Model.OptionAsMetaKey)
            {
                Model.Send([0x1B]);
                if (!string.IsNullOrEmpty(e.KeySymbol))
                {
                    Model.Send(e.KeySymbol);
                }
            }
            else if (!string.IsNullOrEmpty(e.KeySymbol))
            {
                Model.Send(e.KeySymbol);
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Model.Send([0x1b]);
                    break;
                case Key.Space:
                    Model.Send([0x20]);
                    break;
                case Key.Delete:
                    Model.Send(EscapeSequences.CmdDelKey);
                    break;
                case Key.Back:
                    Model.Send([0x7f]);
                    break;
                case Key.Up:
                    Model.Send(Model.Terminal.ApplicationCursor ? EscapeSequences.MoveUpApp : EscapeSequences.MoveUpNormal);
                    break;
                case Key.Down:
                    Model.Send(Model.Terminal.ApplicationCursor ? EscapeSequences.MoveDownApp : EscapeSequences.MoveDownNormal);
                    break;
                case Key.Left:
                    Model.Send(Model.Terminal.ApplicationCursor ? EscapeSequences.MoveLeftApp : EscapeSequences.MoveLeftNormal);
                    break;
                case Key.Right:
                    Model.Send(Model.Terminal.ApplicationCursor ? EscapeSequences.MoveRightApp : EscapeSequences.MoveRightNormal);
                    break;
                case Key.PageUp:
                    if (Model.Terminal.ApplicationCursor)
                    {
                        Model.Send(EscapeSequences.CmdPageUp);
                    }
                    else
                    {
                        Model.PageUp();
                    }
                    break;
                case Key.PageDown:
                    if (Model.Terminal.ApplicationCursor)
                    {
                        Model.Send(EscapeSequences.CmdPageDown);
                    }
                    else
                    {
                        Model.PageDown();
                    }
                    break;
                case Key.Home:
                    Model.Send(Model.Terminal.ApplicationCursor ? EscapeSequences.MoveHomeApp : EscapeSequences.MoveHomeNormal);
                    break;
                case Key.End:
                    Model.Send(Model.Terminal.ApplicationCursor ? EscapeSequences.MoveEndApp : EscapeSequences.MoveEndNormal);
                    break;
                case Key.Insert:
                    break;
                case Key.F1:
                    Model.Send(EscapeSequences.CmdF[0]);
                    break;
                case Key.F2:
                    Model.Send(EscapeSequences.CmdF[1]);
                    break;
                case Key.F3:
                    Model.Send(EscapeSequences.CmdF[2]);
                    break;
                case Key.F4:
                    Model.Send(EscapeSequences.CmdF[3]);
                    break;
                case Key.F5:
                    Model.Send(EscapeSequences.CmdF[4]);
                    break;
                case Key.F6:
                    Model.Send(EscapeSequences.CmdF[5]);
                    break;
                case Key.F7:
                    Model.Send(EscapeSequences.CmdF[6]);
                    break;
                case Key.F8:
                    Model.Send(EscapeSequences.CmdF[7]);
                    break;
                case Key.F9:
                    Model.Send(EscapeSequences.CmdF[8]);
                    break;
                case Key.F10:
                    Model.Send(EscapeSequences.CmdF[9]);
                    break;
                case Key.OemBackTab:
                    Model.Send(EscapeSequences.CmdBackTab);
                    break;
                case Key.Tab:
                    Model.Send(EscapeSequences.CmdTab);
                    break;
                default:
                    if (!string.IsNullOrEmpty(e.KeySymbol))
                    {
                        Model.Send(e.KeySymbol);
                    }
                    break;
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (Model == null)
        {
            return;
        }

        Model.HandlePointerWheel(e.Delta);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(NavigationMethod.Pointer);

        if (Model == null)
        {
            return;
        }

        if (TryHandleTerminalMousePressed(e))
        {
            e.Handled = true;
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (HandleSelectionPressed(e.GetPosition(_surface), e.KeyModifiers, e.ClickCount))
        {
            _selectionClickCount = e.ClickCount;
            _selectionPointerCaptured = true;
            _lastSelectionPointerPosition = e.GetPosition(_surface);
            UpdateSelectionAutoScroll(e.GetPosition(_surface));
            e.Pointer.Capture(_surface);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (TryHandleTerminalMouseMoved(e))
        {
            e.Handled = true;
            return;
        }

        if (!_selectionPointerCaptured || Model == null)
        {
            return;
        }

        _lastSelectionPointerPosition = e.GetPosition(_surface);

        if (HandleSelectionMoved(e.GetPosition(_surface)))
        {
            UpdateSelectionAutoScroll(_lastSelectionPointerPosition);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (TryHandleTerminalMouseReleased(e))
        {
            e.Handled = true;
            return;
        }

        if (!IsMouseModeActive && e.InitialPressMouseButton == MouseButton.Right)
        {
            if (HandleRightClickAction(e.GetPosition(this)))
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            return;
        }

        if (!_selectionPointerCaptured || Model == null)
        {
            return;
        }

        _selectionPointerCaptured = false;
        StopSelectionAutoScroll();
        e.Pointer.Capture(null);

        if (HandleSelectionReleased(e.GetPosition(_surface), e.KeyModifiers, _selectionClickCount))
        {
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _selectionPointerCaptured = false;
        StopSelectionAutoScroll();
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        _surface.InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        _surface.InvalidateVisual();
    }

    public static Brush ConvertXtermColor(int xtermColor)
    {
        return Application.Current?.FindResource("AvaloniaTerminalColor" + xtermColor) as Brush ?? new SolidColorBrush(Colors.Transparent);
    }

    private void CalculateTextSize()
    {
        try
        {
            var myFont = Avalonia.Media.FontFamily.Parse(FontFamily) ?? throw new ArgumentException($"The resource {FontFamily} is not a FontFamily.");

            _typeface = new Typeface(myFont);
            var shaped = TextShaper.Current.ShapeText("a", new TextShaperOptions(_typeface.GlyphTypeface, FontSize));
            var run = new ShapedTextRun(shaped, new GenericTextRunProperties(_typeface, FontSize));

            _consoleTextSize = run.Size;
            _canRenderText = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            _typeface = new Typeface(Avalonia.Media.FontFamily.Default);
            _consoleTextSize = new Size(Math.Max(FontSize * 0.6, 1), Math.Max(FontSize * 1.4, 1));
            _canRenderText = false;
        }
    }

    private void RefreshFromModel()
    {
        SyncSelectionStateFromModel();
        UpdateScrollBar();
        _surface.InvalidateVisual();
    }

    internal bool IsCaretFocused => _hasFocus;

    internal bool HasVisibleCaret => TryGetCaretRect(out _);

    internal Rect CaretRect => TryGetCaretRect(out var rect) ? rect : default;

    internal Point GetCellCenter(int col, int row)
    {
        return new Point(
            (col * _consoleTextSize.Width) + (_consoleTextSize.Width / 2),
            (row * _consoleTextSize.Height) + (_consoleTextSize.Height / 2));
    }

    internal bool TryGetCellFromPointForTests(Point position, bool includeOutsideBounds, out int col, out int row)
    {
        return TryGetCellFromPoint(position, includeOutsideBounds, out col, out row);
    }

    internal bool HandleSelectionPressed(Point position, KeyModifiers modifiers, int clickCount)
    {
        if (!TryGetCellFromPoint(position, includeOutsideBounds: true, out var col, out var row) || Model == null)
        {
            return false;
        }

        _didSelectionDrag = false;

        if (clickCount >= 3)
        {
            Model.SelectRow(row);
            return true;
        }

        if (clickCount == 2)
        {
            Model.SelectWordOrExpression(row, col);
            return true;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            Model.ShiftExtendSelection(row, col);
            return true;
        }

        if (!Model.HasSelection)
        {
            Model.SetSoftSelectionStart(row, col);
        }

        return true;
    }

    internal bool HandleSelectionMoved(Point position)
    {
        if (!TryGetCellFromPoint(position, includeOutsideBounds: true, out var col, out var row) || Model == null)
        {
            return false;
        }

        if (!Model.SelectionService.Active)
        {
            Model.StartSelectionFromSoftStart();
            Model.DragExtendSelection(row, col);
        }
        else
        {
            Model.DragExtendSelection(row, col);
        }

        _didSelectionDrag = true;
        return true;
    }

    internal bool HandleSelectionReleased(Point position, KeyModifiers modifiers, int clickCount)
    {
        if (!TryGetCellFromPoint(position, includeOutsideBounds: true, out var col, out var row) || Model == null)
        {
            return false;
        }

        if (clickCount >= 2)
        {
            _didSelectionDrag = false;
            return true;
        }

        if (!Model.SelectionService.Active)
        {
            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                Model.ShiftExtendSelection(row, col);
            }
            else
            {
                Model.SetSoftSelectionStart(row, col);
            }
        }
        else if (!_didSelectionDrag)
        {
            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                Model.ShiftExtendSelection(row, col);
            }
            else
            {
                Model.ClearSelection();
                Model.SetSoftSelectionStart(row, col);
            }
        }

        _didSelectionDrag = false;
        return true;
    }

    private bool TryGetCaretRect(out Rect rect)
    {
        rect = default;

        if (Model == null || !Model.IsCaretVisible || _consoleTextSize.Width <= 0 || _consoleTextSize.Height <= 0)
        {
            return false;
        }

        rect = new Rect(
            Model.CaretColumn * _consoleTextSize.Width,
            Model.CaretRow * _consoleTextSize.Height,
            Math.Max(_consoleTextSize.Width, 1),
            Math.Max(_consoleTextSize.Height, 1));

        return true;
    }

    private bool TryHandleTerminalMousePressed(PointerPressedEventArgs e)
    {
        if (Model == null || !Model.IsMouseModeActive || !ShouldSendTerminalMousePress(Model.Terminal.MouseMode))
        {
            return false;
        }

        if (!TryGetCellFromPoint(e.GetPosition(_surface), includeOutsideBounds: true, out var col, out var row))
        {
            return false;
        }

        var button = GetPressedButton(e.GetCurrentPoint(_surface).Properties);
        var buttonFlags = Model.Terminal.EncodeMouseButton(
            button,
            release: false,
            shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            meta: e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            control: e.KeyModifiers.HasFlag(KeyModifiers.Control));

        _activeTerminalMouseButton = button;
        _terminalMouseCaptured = true;
        e.Pointer.Capture(_surface);

        Model.Terminal.SendEvent(buttonFlags, col, row);
        return true;
    }

    private bool TryHandleTerminalMouseMoved(PointerEventArgs e)
    {
        if (Model == null || !Model.IsMouseModeActive)
        {
            return false;
        }

        var mode = Model.Terminal.MouseMode;
        var props = e.GetCurrentPoint(_surface).Properties;
        var hasPressedButton = props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed;

        var shouldSendMotion = mode.SendMotionEvent() || (mode.SendButtonTracking() && hasPressedButton);
        if (!shouldSendMotion)
        {
            return false;
        }

        if (!TryGetCellFromPoint(e.GetPosition(_surface), includeOutsideBounds: true, out var col, out var row))
        {
            return false;
        }

        var button = hasPressedButton ? GetPressedButton(props) : _activeTerminalMouseButton;
        var buttonFlags = Model.Terminal.EncodeMouseButton(
            button,
            release: false,
            shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            meta: e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            control: e.KeyModifiers.HasFlag(KeyModifiers.Control));

        Model.Terminal.SendMouseMotion(buttonFlags, col, row);
        return true;
    }

    private bool TryHandleTerminalMouseReleased(PointerReleasedEventArgs e)
    {
        if (Model == null || !_terminalMouseCaptured || !Model.IsMouseModeActive)
        {
            return false;
        }

        _terminalMouseCaptured = false;
        e.Pointer.Capture(null);

        if (!TryGetCellFromPoint(e.GetPosition(_surface), includeOutsideBounds: true, out var col, out var row))
        {
            return false;
        }

        if (!ShouldSendTerminalMouseRelease(Model.Terminal.MouseMode))
        {
            _activeTerminalMouseButton = 0;
            return true;
        }

        var button = GetPointerButton(e);
        var buttonFlags = Model.Terminal.EncodeMouseButton(
            button,
            release: true,
            shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            meta: e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            control: e.KeyModifiers.HasFlag(KeyModifiers.Control));

        _activeTerminalMouseButton = 0;
        Model.Terminal.SendEvent(buttonFlags, col, row);
        return true;
    }

    private static bool ShouldSendTerminalMousePress(MouseMode mode)
    {
        return mode == MouseMode.X10 || mode == MouseMode.VT200 || mode == MouseMode.ButtonEventTracking || mode == MouseMode.AnyEvent;
    }

    private static bool ShouldSendTerminalMouseRelease(MouseMode mode)
    {
        return mode == MouseMode.VT200 || mode == MouseMode.ButtonEventTracking || mode == MouseMode.AnyEvent;
    }

    private static int GetPointerButton(PointerEventArgs e)
    {
        return e switch
        {
            PointerReleasedEventArgs released => MapMouseButton(released.InitialPressMouseButton),
            _ => 0,
        };
    }

    private static int GetPressedButton(PointerPointProperties properties)
    {
        if (properties.IsLeftButtonPressed)
        {
            return 0;
        }

        if (properties.IsMiddleButtonPressed)
        {
            return 1;
        }

        if (properties.IsRightButtonPressed)
        {
            return 2;
        }

        return 0;
    }

    private static int MapMouseButton(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => 0,
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            _ => 0,
        };
    }

    private void OnSelectionAutoScrollTimerTick(object? sender, EventArgs e)
    {
        ProcessSelectionAutoScroll();
    }

    private void UpdateSelectionAutoScroll(Point position)
    {
        _selectionAutoScrollDelta = CalculateSelectionAutoScrollDelta(position);

        if (_selectionAutoScrollDelta == 0)
        {
            StopSelectionAutoScroll();
            return;
        }

        if (!_selectionAutoScrollTimer.IsEnabled)
        {
            _selectionAutoScrollTimer.Start();
        }
    }

    private int CalculateSelectionAutoScrollDelta(Point position)
    {
        if (Model == null || !HasActiveSelectionDrag() || _consoleTextSize.Height <= 0)
        {
            return 0;
        }

        var rawRow = (int)Math.Floor(position.Y / _consoleTextSize.Height);
        if (rawRow < 0)
        {
            return CalculateScrollVelocity(-rawRow) * -1;
        }

        if (rawRow >= Model.Terminal.Rows)
        {
            return CalculateScrollVelocity(rawRow - Model.Terminal.Rows);
        }

        return 0;
    }

    private int CalculateScrollVelocity(int delta)
    {
        if (Model == null)
        {
            return 0;
        }

        if (delta > 9)
        {
            return Math.Max(Model.Terminal.Rows, 20);
        }

        if (delta > 5)
        {
            return 10;
        }

        if (delta > 1)
        {
            return 3;
        }

        return 1;
    }

    private bool HasActiveSelectionDrag()
    {
        return _selectionPointerCaptured && Model != null && !Model.IsMouseModeActive;
    }

    private void ProcessSelectionAutoScroll()
    {
        if (!HasActiveSelectionDrag() || _selectionAutoScrollDelta == 0 || Model == null)
        {
            StopSelectionAutoScroll();
            return;
        }

        Model.ScrollLines(_selectionAutoScrollDelta);
        HandleSelectionMoved(_lastSelectionPointerPosition);
    }

    private void StopSelectionAutoScroll()
    {
        _selectionAutoScrollDelta = 0;
        if (_selectionAutoScrollTimer.IsEnabled)
        {
            _selectionAutoScrollTimer.Stop();
        }
    }

    private Avalonia.Input.Platform.IClipboard? ResolveClipboard()
    {
        return TopLevel.GetTopLevel(this)?.Clipboard;
    }

    private void RaiseContextRequested(Point position)
    {
        ContextRequested?.Invoke(this, new TerminalContextRequestedEventArgs(position, SelectedText, HasSelection));
    }

    private bool HandleRightClickAction(Point position)
    {
        switch (RightClickAction)
        {
            case RightClickAction.None:
                return false;
            case RightClickAction.CopyOrPaste:
                if (HasSelection)
                {
                    _ = CopySelectionAsync();
                }
                else
                {
                    _ = PasteFromClipboardAsync();
                }

                return true;
            case RightClickAction.ContextMenu:
            default:
                RaiseContextRequested(position);
                return true;
        }
    }

    private bool TryGetCellFromPoint(Point position, bool includeOutsideBounds, out int col, out int row)
    {
        col = 0;
        row = 0;

        if (_consoleTextSize.Width <= 0 || _consoleTextSize.Height <= 0 || Model == null)
        {
            return false;
        }

        var rawCol = (int)Math.Floor(position.X / _consoleTextSize.Width);
        var rawRow = (int)Math.Floor(position.Y / _consoleTextSize.Height);

        if (!includeOutsideBounds &&
            (rawCol < 0 || rawCol >= Model.Terminal.Cols || rawRow < 0 || rawRow >= Model.Terminal.Rows))
        {
            return false;
        }

        col = Math.Clamp(rawCol, 0, Math.Max(Model.Terminal.Cols - 1, 0));
        row = Math.Clamp(rawRow, 0, Math.Max(Model.Terminal.Rows - 1, 0));
        return true;
    }

    private void ResizeModelToViewport()
    {
        if (Model == null)
        {
            return;
        }

        var viewport = _surface.Bounds;
        Model.Resize(viewport.Width, viewport.Height, _consoleTextSize.Width, _consoleTextSize.Height);
        UpdateScrollBar();
    }

    private void UpdateScrollBar()
    {
        _isUpdatingScrollBar = true;
        try
        {
            if (Model == null)
            {
                _verticalScrollBar.IsEnabled = false;
                _verticalScrollBar.Minimum = 0;
                _verticalScrollBar.Maximum = 0;
                _verticalScrollBar.ViewportSize = 1;
                _verticalScrollBar.LargeChange = 1;
                _verticalScrollBar.Value = 0;
                return;
            }

            _verticalScrollBar.IsEnabled = Model.CanScroll;
            _verticalScrollBar.Minimum = 0;
            _verticalScrollBar.Maximum = Model.MaxScrollback;
            _verticalScrollBar.ViewportSize = Math.Max(Model.Terminal.Rows, 1);
            _verticalScrollBar.SmallChange = 1;
            _verticalScrollBar.LargeChange = Math.Max(Model.Terminal.Rows, 1);
            _verticalScrollBar.Value = Model.ScrollOffset;
        }
        finally
        {
            _isUpdatingScrollBar = false;
        }
    }

    private void OnVerticalScrollBarValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingScrollBar || Model == null)
        {
            return;
        }

        Model.ScrollToYDisp((int)Math.Round(e.NewValue));
    }

    private sealed class TerminalSurface(TerminalControl owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
            context.FillRectangle(ConvertXtermColor(0), rect);

            if (owner.Model == null)
            {
                return;
            }

            foreach (var item in owner.Model.ConsoleText)
            {
                var cellRect = new Rect(
                    owner._consoleTextSize.Width * item.Key.x,
                    owner._consoleTextSize.Height * item.Key.y,
                    owner._consoleTextSize.Width + 1,
                    owner._consoleTextSize.Height + 1);
                context.FillRectangle(item.Value.Background, cellRect);

                if (owner._canRenderText)
                {
                    var formattedText = new FormattedText(item.Value.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, owner._typeface, owner.FontSize, item.Value.Foreground);
                    if (item.Value.TextDecorations != null)
                    {
                        formattedText.SetTextDecorations(item.Value.TextDecorations);
                    }

                    formattedText.SetFontWeight(item.Value.FontWeight);
                    formattedText.SetFontStyle(item.Value.FontStyle);

                    context.DrawText(formattedText, new Point(owner._consoleTextSize.Width * item.Key.x, owner._consoleTextSize.Height * item.Key.y));
                }
            }

            if (owner.Model.HasSelection)
            {
                foreach (var selectionRect in owner.GetSelectionRects())
                {
                    context.FillRectangle(owner.ResolveSelectionBrush(), selectionRect);
                }
            }

            if (!owner.TryGetCaretRect(out var caretRect))
            {
                return;
            }

            var caretBrush = owner.ResolveCaretBrush();
            if (owner._hasFocus)
            {
                var fillRect = new Rect(
                    caretRect.X - 1,
                    caretRect.Y,
                    caretRect.Width + 2,
                    caretRect.Height);
                context.FillRectangle(caretBrush, fillRect);
            }
            else
            {
                var strokeRect = new Rect(
                    caretRect.X + 1,
                    caretRect.Y + 1,
                    Math.Max(caretRect.Width - 2, 1),
                    Math.Max(caretRect.Height - 2, 1));
                context.DrawRectangle(null, new Pen(caretBrush, 1), strokeRect);
            }
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            owner.ResizeModelToViewport();
        }
    }

    private IEnumerable<Rect> GetSelectionRects()
    {
        if (Model == null || !Model.SelectionService.Active)
        {
            yield break;
        }

        var selection = Model.SelectionService;
        var start = selection.Start;
        var end = selection.End;

        if (start == end)
        {
            yield break;
        }

        if ((start.Y > end.Y) || (start.Y == end.Y && start.X > end.X))
        {
            (start, end) = (end, start);
        }

        var screenStartRow = start.Y - Model.Terminal.Buffer.YDisp;
        var screenEndRow = end.Y - Model.Terminal.Buffer.YDisp;

        for (var row = screenStartRow; row <= screenEndRow; row++)
        {
            if (row < 0 || row >= Model.Terminal.Rows)
            {
                continue;
            }

            var colStart = row == screenStartRow ? start.X : 0;
            var colEnd = row == screenEndRow ? end.X : Model.Terminal.Cols;

            if (colEnd == colStart)
            {
                colEnd = Math.Min(colStart + 1, Model.Terminal.Cols);
            }

            if (colEnd < colStart)
            {
                (colStart, colEnd) = (colEnd, colStart);
            }

            yield return new Rect(
                (colStart * _consoleTextSize.Width) - 1,
                row * _consoleTextSize.Height,
                Math.Max(((colEnd - colStart) * _consoleTextSize.Width) + 2, _consoleTextSize.Width),
                _consoleTextSize.Height);
        }
    }

    private IBrush ResolveSelectionBrush()
    {
        if (SelectionBrush is not null)
        {
            return SelectionBrush;
        }

        if (Application.Current?.FindResource("ThemeAccentBrush") is ISolidColorBrush accentBrush)
        {
            return new SolidColorBrush(accentBrush.Color, 0.35);
        }

        return new SolidColorBrush(Avalonia.Media.Color.FromArgb(96, 96, 160, 255));
    }

    private void SyncSelectionStateFromModel()
    {
        SelectedText = Model?.SelectedText ?? string.Empty;
        HasSelection = Model?.HasSelection ?? false;
    }

    private IBrush ResolveCaretBrush()
    {
        if (CaretBrush is not null)
        {
            return CaretBrush;
        }

        if (Model is not null &&
            Model.ConsoleText.TryGetValue((Model.CaretColumn, Model.CaretRow), out var caretCell))
        {
            if (caretCell.Foreground is ISolidColorBrush foreground &&
                caretCell.Background is ISolidColorBrush background)
            {
                return ColorsAreClose(foreground.Color, background.Color)
                    ? CreateContrastingBrush(background.Color)
                    : foreground;
            }

            if (caretCell.Foreground is not null)
            {
                return caretCell.Foreground;
            }
        }

        return ConvertXtermColor(15);
    }

    private static bool ColorsAreClose(Avalonia.Media.Color left, Avalonia.Media.Color right)
    {
        var red = Math.Abs(left.R - right.R);
        var green = Math.Abs(left.G - right.G);
        var blue = Math.Abs(left.B - right.B);
        return red + green + blue < 48;
    }

    private static SolidColorBrush CreateContrastingBrush(Avalonia.Media.Color background)
    {
        var luminance = (0.2126 * background.R) + (0.7152 * background.G) + (0.0722 * background.B);
        return luminance > 128
            ? new SolidColorBrush(Colors.Black)
            : new SolidColorBrush(Colors.White);
    }

    internal void ProcessSelectionAutoScrollForTests()
    {
        ProcessSelectionAutoScroll();
    }

    internal int SelectionAutoScrollDeltaForTests => _selectionAutoScrollDelta;
}
