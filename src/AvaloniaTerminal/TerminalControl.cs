using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Threading;
using XTerm.Buffer;
using Point = Avalonia.Point;
using AvaloniaModifiers = Avalonia.Input.KeyModifiers;
using XKey = XTerm.Input.Key;
using XMouseButton = XTerm.Input.MouseButton;
using XMouseEventType = XTerm.Input.MouseEventType;
using XMouseTrackingMode = XTerm.Input.MouseTrackingMode;
using XTermModifiers = XTerm.Input.KeyModifiers;

namespace AvaloniaTerminal;

public partial class TerminalControl : Grid
{
    private static readonly TimeSpan SelectionAutoScrollInterval = TimeSpan.FromMilliseconds(80);
    private static readonly Brush[] FallbackXtermPalette = CreateFallbackXtermPalette();
    private const int MaxFormattedTextCacheEntries = 16384;

    private Size _consoleTextSize;

    private Typeface _typeface;

    private readonly TerminalSurface _surface;

    private readonly ScrollBar _verticalScrollBar;

    private readonly ColumnDefinition _scrollBarColumn;

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
    private readonly Dictionary<FormattedTextCacheKey, FormattedText> _formattedTextCache = [];
    private readonly Queue<FormattedTextCacheKey> _formattedTextCacheOrder = [];

    public TerminalControl()
    {
        Focusable = true;

        ColumnDefinitions =
        [
            new ColumnDefinition(1, GridUnitType.Star),
            new ColumnDefinition(GridLength.Auto),
        ];
        _scrollBarColumn = ColumnDefinitions[1];

        _surface = new TerminalSurface(this);
        SetColumn(_surface, 0);
        Children.Add(_surface);

        _verticalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            SmallChange = 1,
            Visibility = ScrollBarVisibility.Auto,
            AllowAutoHide = true,
        };
        SetColumn(_verticalScrollBar, 1);
        _verticalScrollBar.ValueChanged += OnVerticalScrollBarValueChanged;
        Children.Add(_verticalScrollBar);

        _selectionAutoScrollTimer = new DispatcherTimer
        {
            Interval = SelectionAutoScrollInterval,
        };
        _selectionAutoScrollTimer.Tick += OnSelectionAutoScrollTimerTick;

        ApplyResourceDefaults();
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

    internal bool CanRenderTextForTests => _canRenderText;

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
            await clipboard.SetTextAsync(SelectedText ?? string.Empty).ConfigureAwait(true);
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
            text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
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
            ClearFormattedTextCache();
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
                case Key.PageUp:
                    if (Model.Terminal.Engine.ApplicationCursorKeys)
                    {
                        SendGeneratedKey(XKey.PageUp);
                    }
                    else
                    {
                        Model.PageUp();
                    }
                    break;
                case Key.PageDown:
                    if (Model.Terminal.Engine.ApplicationCursorKeys)
                    {
                        SendGeneratedKey(XKey.PageDown);
                    }
                    else
                    {
                        Model.PageDown();
                    }
                    break;
                case Key.Insert:
                    break;
                default:
                    if (TrySendGeneratedKey(e.Key))
                    {
                        break;
                    }

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

        if (Model.IsMouseModeActive)
        {
            SendWheelAsMouseEvents(e.Delta, e.GetPosition(_surface));
        }
        else
        {
            Model.HandlePointerWheel(e.Delta);
        }

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

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        _surface.InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        _surface.InvalidateVisual();
    }

    public static Brush ConvertXtermColor(int xtermColor)
    {
        if (xtermColor < 0)
        {
            xtermColor = 0;
        }
        else if (xtermColor >= FallbackXtermPalette.Length)
        {
            xtermColor = FallbackXtermPalette.Length - 1;
        }

        if (TryGetApplicationResource(GetXtermColorResourceKey(xtermColor)) is Brush resourceBrush)
        {
            return resourceBrush;
        }

        return FallbackXtermPalette[xtermColor];
    }

    private static string GetXtermColorResourceKey(int xtermColor)
    {
        return $"AvaloniaTerminalColor{xtermColor}";
    }

    private static object? TryGetApplicationResource(string resourceKey)
    {
        var application = Application.Current;
        if (application == null)
        {
            return null;
        }

        if (application.Resources.ContainsKey(resourceKey))
        {
            return application.Resources[resourceKey];
        }

        if (application.TryFindResource(resourceKey, out var value))
        {
            return value;
        }

        return null;
    }

    private bool TryGetResourceValue(string resourceKey, out object? value)
    {
        if (Resources.ContainsKey(resourceKey))
        {
            value = Resources[resourceKey];
            return true;
        }

        if (Application.Current?.Resources.ContainsKey(resourceKey) == true)
        {
            value = Application.Current.Resources[resourceKey];
            return true;
        }

        if (this.TryFindResource(resourceKey, out value))
        {
            return true;
        }

        value = TryGetApplicationResource(resourceKey);
        return value != null;
    }

    private void ApplyResourceDefaults()
    {
        if (TryGetApplicationResource("AvaloniaTerminalFontFamily") is string fontFamily)
        {
            FontFamily = fontFamily;
        }

        if (TryGetApplicationResource("AvaloniaTerminalFontSize") is double fontSize)
        {
            FontSize = fontSize;
        }
        else if (TryGetApplicationResource("AvaloniaTerminalFontSize") is int fontSizeInt)
        {
            FontSize = fontSizeInt;
        }

        if (TryGetApplicationResource("AvaloniaTerminalCaretBrush") is IBrush caretBrush)
        {
            CaretBrush = caretBrush;
        }

        if (TryGetApplicationResource("AvaloniaTerminalSelectionBrush") is IBrush selectionBrush)
        {
            SelectionBrush = selectionBrush;
        }
    }

    private static Brush[] CreateFallbackXtermPalette()
    {
        Brush[] palette = new Brush[256];

        for (int i = 0; i < palette.Length; i++)
        {
            palette[i] = new SolidColorBrush(CreateFallbackXtermColor(i));
        }

        return palette;
    }

    private static Color CreateFallbackXtermColor(int index)
    {
        return index switch
        {
            0 => Color.FromRgb(0x00, 0x00, 0x00),
            1 => Color.FromRgb(0x80, 0x00, 0x00),
            2 => Color.FromRgb(0x00, 0x80, 0x00),
            3 => Color.FromRgb(0x80, 0x80, 0x00),
            4 => Color.FromRgb(0x00, 0x00, 0x80),
            5 => Color.FromRgb(0x80, 0x00, 0x80),
            6 => Color.FromRgb(0x00, 0x80, 0x80),
            7 => Color.FromRgb(0xC0, 0xC0, 0xC0),
            8 => Color.FromRgb(0x80, 0x80, 0x80),
            9 => Color.FromRgb(0xFF, 0x00, 0x00),
            10 => Color.FromRgb(0x00, 0xFF, 0x00),
            11 => Color.FromRgb(0xFF, 0xFF, 0x00),
            12 => Color.FromRgb(0x00, 0x00, 0xFF),
            13 => Color.FromRgb(0xFF, 0x00, 0xFF),
            14 => Color.FromRgb(0x00, 0xFF, 0xFF),
            15 => Color.FromRgb(0xFF, 0xFF, 0xFF),
            >= 16 and <= 231 => CreateCubeColor(index - 16),
            >= 232 and <= 255 => CreateGrayColor(index - 232),
            _ => Color.FromRgb(0x00, 0x00, 0x00),
        };
    }

    private static Color CreateCubeColor(int index)
    {
        int r = index / 36;
        int g = (index / 6) % 6;
        int b = index % 6;

        return Color.FromRgb(ToCubeComponent(r), ToCubeComponent(g), ToCubeComponent(b));
    }

    private static byte ToCubeComponent(int component)
    {
        return component == 0 ? (byte)0 : (byte)(55 + (component * 40));
    }

    private static Color CreateGrayColor(int index)
    {
        byte value = (byte)(8 + (index * 10));
        return Color.FromRgb(value, value, value);
    }

    private void CalculateTextSize()
    {
        if (TryCalculateTextMetrics(FontFamily, FontSize, out _typeface, out _consoleTextSize))
        {
            _canRenderText = true;
            return;
        }

        if (TryCalculateTextMetrics(Avalonia.Media.FontFamily.Default, FontSize, out _typeface, out _consoleTextSize))
        {
            _canRenderText = true;
            return;
        }

        _typeface = new Typeface(Avalonia.Media.FontFamily.Default);
        _consoleTextSize = new Size(Math.Max(FontSize * 0.6, 1), Math.Max(FontSize * 1.4, 1));
        _canRenderText = true;
    }

    private static bool TryCalculateTextMetrics(Avalonia.Media.FontFamily fontFamily, double fontSize, out Typeface typeface, out Size size)
    {
        try
        {
            typeface = new Typeface(fontFamily);
            if (!FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface))
            {
                size = default;
                return false;
            }
            var shaped = TextShaper.Current.ShapeText("a", new TextShaperOptions(glyphTypeface, fontSize));
            var run = new ShapedTextRun(shaped, new GenericTextRunProperties(typeface, fontSize));
            size = run.Size;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            typeface = default;
            size = default;
            return false;
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

    internal IBrush CaretBrushForTests => ResolveCaretBrush();

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

        if (!Model.Terminal.Selection.HasSelection)
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

        if (!Model.Terminal.Selection.HasSelection)
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
        if (Model == null || !Model.IsMouseModeActive || !ShouldSendTerminalMousePress(Model.Terminal.Engine.MouseTrackingMode))
        {
            return false;
        }

        if (!TryGetCellFromPoint(e.GetPosition(_surface), includeOutsideBounds: true, out var col, out var row))
        {
            return false;
        }

        var button = GetPressedButton(e.GetCurrentPoint(_surface).Properties);
        _activeTerminalMouseButton = button;
        _terminalMouseCaptured = true;
        e.Pointer.Capture(_surface);

        SendMouseSequence(
            ToMouseButton(button),
            XMouseEventType.Down,
            col,
            row,
            e.KeyModifiers);
        return true;
    }

    private bool TryHandleTerminalMouseMoved(PointerEventArgs e)
    {
        if (Model == null || !Model.IsMouseModeActive)
        {
            return false;
        }

        var mode = Model.Terminal.Engine.MouseTrackingMode;
        var props = e.GetCurrentPoint(_surface).Properties;
        var hasPressedButton = props.IsLeftButtonPressed || props.IsMiddleButtonPressed || props.IsRightButtonPressed;

        var shouldSendMotion = mode == XMouseTrackingMode.AnyEvent || (mode == XMouseTrackingMode.ButtonEvent && hasPressedButton);
        if (!shouldSendMotion)
        {
            return false;
        }

        if (!TryGetCellFromPoint(e.GetPosition(_surface), includeOutsideBounds: true, out var col, out var row))
        {
            return false;
        }

        var button = hasPressedButton ? GetPressedButton(props) : _activeTerminalMouseButton;
        SendMouseSequence(
            ToMouseButton(button),
            XMouseEventType.Drag,
            col,
            row,
            e.KeyModifiers);
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

        if (!ShouldSendTerminalMouseRelease(Model.Terminal.Engine.MouseTrackingMode))
        {
            _activeTerminalMouseButton = 0;
            return true;
        }

        var button = GetPointerButton(e);
        _activeTerminalMouseButton = 0;
        SendMouseSequence(
            ToMouseButton(button),
            XMouseEventType.Up,
            col,
            row,
            e.KeyModifiers);
        return true;
    }

    private static bool ShouldSendTerminalMousePress(XMouseTrackingMode mode)
    {
        return mode == XMouseTrackingMode.X10 || mode == XMouseTrackingMode.VT200 || mode == XMouseTrackingMode.ButtonEvent || mode == XMouseTrackingMode.AnyEvent;
    }

    private static bool ShouldSendTerminalMouseRelease(XMouseTrackingMode mode)
    {
        return mode == XMouseTrackingMode.VT200 || mode == XMouseTrackingMode.ButtonEvent || mode == XMouseTrackingMode.AnyEvent;
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
        _surface.InvalidateVisual();
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
                UpdateScrollBarLayout();
                return;
            }

            _verticalScrollBar.IsEnabled = Model.CanScroll;
            _verticalScrollBar.Minimum = 0;
            _verticalScrollBar.Maximum = Model.MaxScrollback;
            _verticalScrollBar.ViewportSize = Math.Max(Model.Terminal.Rows, 1);
            _verticalScrollBar.SmallChange = 1;
            _verticalScrollBar.LargeChange = Math.Max(Model.Terminal.Rows, 1);
            _verticalScrollBar.Value = Model.ScrollOffset;
            UpdateScrollBarLayout();
        }
        finally
        {
            _isUpdatingScrollBar = false;
        }
    }

    private void UpdateScrollBarLayout()
    {
        var isFullScreen = Model?.Terminal.IsAlternateBufferActive ?? false;

        _scrollBarColumn.Width = isFullScreen ? new GridLength(0) : GridLength.Auto;
        _verticalScrollBar.Visibility = isFullScreen ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        _verticalScrollBar.AllowAutoHide = !isFullScreen;
        InvalidateMeasure();
    }

    private void SendWheelAsMouseEvents(Vector delta, Point position)
    {
        if (Model == null || delta.Y == 0)
        {
            return;
        }

        if (!TryGetCellFromPoint(position, includeOutsideBounds: true, out var col, out var row))
        {
            return;
        }

        var repeats = CalculateScrollVelocity((int)Math.Ceiling(Math.Abs(delta.Y)));
        for (var i = 0; i < repeats; i++)
        {
            var button = delta.Y > 0 ? 4 : 5;
            SendMouseSequence(
                button == 4 ? XMouseButton.WheelUp : XMouseButton.WheelDown,
                button == 4 ? XMouseEventType.WheelUp : XMouseEventType.WheelDown,
                col,
                row,
                KeyModifiers.None);
        }
    }

    private void SendMouseSequence(XMouseButton button, XMouseEventType eventType, int col, int row, AvaloniaModifiers modifiers)
    {
        if (Model == null)
        {
            return;
        }

        var sequence = Model.Terminal.Engine.GenerateMouseEvent(
            button,
            col,
            row,
            eventType,
            ToXTermModifiers(modifiers));

        Model.Send(sequence);
    }

    private bool TrySendGeneratedKey(Key key)
    {
        if (Model == null || !TryMapKey(key, out var mappedKey, out var extraModifiers))
        {
            return false;
        }

        SendGeneratedKey(mappedKey, extraModifiers);
        return true;
    }

    private void SendGeneratedKey(XKey key, XTermModifiers extraModifiers = XTermModifiers.None)
    {
        if (Model == null)
        {
            return;
        }

        var sequence = Model.Terminal.Engine.GenerateKeyInput(key, extraModifiers);
        if (!string.IsNullOrEmpty(sequence))
        {
            Model.Send(sequence);
        }
    }

    private static bool TryMapKey(Key key, out XKey mappedKey, out XTermModifiers extraModifiers)
    {
        extraModifiers = XTermModifiers.None;

        switch (key)
        {
            case Key.Escape:
                mappedKey = XKey.Escape;
                return true;
            case Key.Space:
                mappedKey = XKey.Space;
                return true;
            case Key.Delete:
                mappedKey = XKey.Delete;
                return true;
            case Key.Back:
                mappedKey = XKey.Backspace;
                return true;
            case Key.Up:
                mappedKey = XKey.UpArrow;
                return true;
            case Key.Down:
                mappedKey = XKey.DownArrow;
                return true;
            case Key.Left:
                mappedKey = XKey.LeftArrow;
                return true;
            case Key.Right:
                mappedKey = XKey.RightArrow;
                return true;
            case Key.Home:
                mappedKey = XKey.Home;
                return true;
            case Key.End:
                mappedKey = XKey.End;
                return true;
            case Key.Tab:
                mappedKey = XKey.Tab;
                return true;
            case Key.OemBackTab:
                mappedKey = XKey.Tab;
                extraModifiers = XTermModifiers.Shift;
                return true;
            case Key.F1:
                mappedKey = XKey.F1;
                return true;
            case Key.F2:
                mappedKey = XKey.F2;
                return true;
            case Key.F3:
                mappedKey = XKey.F3;
                return true;
            case Key.F4:
                mappedKey = XKey.F4;
                return true;
            case Key.F5:
                mappedKey = XKey.F5;
                return true;
            case Key.F6:
                mappedKey = XKey.F6;
                return true;
            case Key.F7:
                mappedKey = XKey.F7;
                return true;
            case Key.F8:
                mappedKey = XKey.F8;
                return true;
            case Key.F9:
                mappedKey = XKey.F9;
                return true;
            case Key.F10:
                mappedKey = XKey.F10;
                return true;
            default:
                mappedKey = default;
                return false;
        }
    }

    private static XTermModifiers ToXTermModifiers(AvaloniaModifiers modifiers)
    {
        XTermModifiers result = XTermModifiers.None;

        if (modifiers.HasFlag(AvaloniaModifiers.Shift))
        {
            result |= XTermModifiers.Shift;
        }

        if (modifiers.HasFlag(AvaloniaModifiers.Alt))
        {
            result |= XTermModifiers.Alt;
        }

        if (modifiers.HasFlag(AvaloniaModifiers.Control))
        {
            result |= XTermModifiers.Control;
        }

        return result;
    }

    private static XMouseButton ToMouseButton(int button)
    {
        return button switch
        {
            0 => XMouseButton.Left,
            1 => XMouseButton.Middle,
            2 => XMouseButton.Right,
            _ => XMouseButton.Left,
        };
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
            context.FillRectangle(owner.ResolvePaletteBrush(0), rect);

            if (owner.Model == null)
            {
                return;
            }

            foreach (var row in owner.Model.ViewportRows)
            {
                foreach (var run in row.Runs)
                {
                    var runRect = new Rect(
                        owner._consoleTextSize.Width * run.StartColumn,
                        owner._consoleTextSize.Height * row.RowIndex,
                        owner._consoleTextSize.Width * run.CellWidth,
                        owner._consoleTextSize.Height + 1);
                    context.FillRectangle(owner.ResolveColorBrush(run.BackgroundColor, isForeground: false), runRect);

                    if (owner._canRenderText)
                    {
                        var formattedText = owner.GetOrCreateFormattedText(run);
                        context.DrawText(formattedText, new Point(owner._consoleTextSize.Width * run.StartColumn, owner._consoleTextSize.Height * row.RowIndex));
                    }
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
        if (Model == null || !Model.Terminal.Selection.HasSelection)
        {
            yield break;
        }

        for (var row = 0; row < Model.Terminal.Rows; row++)
        {
            int? runStart = null;

            for (var col = 0; col < Model.Terminal.Cols; col++)
            {
                var selected = Model.Terminal.Selection.IsCellSelected(col, row);
                if (selected && runStart is null)
                {
                    runStart = col;
                }
                else if (!selected && runStart is not null)
                {
                    yield return CreateSelectionRect(row, runStart.Value, col);
                    runStart = null;
                }
            }

            if (runStart is not null)
            {
                yield return CreateSelectionRect(row, runStart.Value, Model.Terminal.Cols);
            }
        }
    }

    private Rect CreateSelectionRect(int row, int colStart, int colEnd)
    {
        if (colEnd == colStart)
        {
            colEnd = Math.Min(colStart + 1, Model?.Terminal.Cols ?? colStart + 1);
        }

        return new Rect(
            (colStart * _consoleTextSize.Width) - 1,
            row * _consoleTextSize.Height,
            Math.Max(((colEnd - colStart) * _consoleTextSize.Width) + 2, _consoleTextSize.Width),
            _consoleTextSize.Height);
    }

    private IBrush ResolveSelectionBrush()
    {
        if (SelectionBrush is not null)
        {
            return SelectionBrush;
        }

        if (TryGetApplicationResource("AvaloniaTerminalSelectionBrush") is IBrush resourceBrush)
        {
            return resourceBrush;
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

        if (TryGetApplicationResource("AvaloniaTerminalCaretBrush") is IBrush resourceBrush)
        {
            return resourceBrush;
        }

        if (TryGetCaretColors(out var foregroundColor, out var backgroundColor))
        {
            return ColorsAreClose(foregroundColor, backgroundColor)
                ? CreateContrastingBrush(backgroundColor)
                : new SolidColorBrush(foregroundColor);
        }

        return ConvertXtermColor(15);
    }

    private bool TryGetCaretColors(out Color foreground, out Color background)
    {
        foreground = Colors.White;
        background = Colors.Black;

        if (Model == null || !TryGetCaretBufferCell(out var cell))
        {
            return false;
        }

        var attribute = cell.Attributes;
        var fg = attribute.GetFgColor();
        var bg = attribute.GetBgColor();
        if (attribute.IsInverse())
        {
            (fg, bg) = (bg, fg);
        }

        if (ResolveColorBrush(fg, isForeground: true) is not ISolidColorBrush foregroundBrush ||
            ResolveColorBrush(bg, isForeground: false) is not ISolidColorBrush backgroundBrush)
        {
            return false;
        }

        foreground = foregroundBrush.Color;
        background = backgroundBrush.Color;
        return true;
    }

    private bool TryGetCaretBufferCell(out BufferCell cell)
    {
        cell = BufferCell.Space;
        if (Model == null)
        {
            return false;
        }

        var buffer = Model.Terminal.Buffer;
        var absoluteRow = Math.Clamp(buffer.YDisp + Model.CaretRow, 0, Math.Max(buffer.Lines.Length - 1, 0));
        var line = absoluteRow < buffer.Lines.Length ? buffer.GetLine(absoluteRow) : null;
        if (line == null || Model.CaretColumn >= line.Length)
        {
            return false;
        }

        cell = line[Model.CaretColumn];
        return true;
    }

    private Brush ResolvePaletteBrush(int xtermColor)
    {
        xtermColor = Math.Clamp(xtermColor, 0, FallbackXtermPalette.Length - 1);

        if (TryGetResourceValue(GetXtermColorResourceKey(xtermColor), out var resourceValue) && resourceValue is Brush resourceBrush)
        {
            return resourceBrush;
        }

        return FallbackXtermPalette[xtermColor];
    }

    private Brush ResolveColorBrush(int color, bool isForeground)
    {
        if (color == 256 || color == 257)
        {
            return ResolvePaletteBrush(isForeground ? 15 : 0);
        }

        if (color is >= 0 and <= 255)
        {
            return ResolvePaletteBrush(color);
        }

        var red = (byte)((color >> 16) & 0xFF);
        var green = (byte)((color >> 8) & 0xFF);
        var blue = (byte)(color & 0xFF);
        return new SolidColorBrush(Color.FromRgb(red, green, blue));
    }

    private FormattedText GetOrCreateFormattedText(ViewportTextRun run)
    {
        var cacheKey = new FormattedTextCacheKey(
            run.Text,
            run.ForegroundColor,
            run.FontWeight,
            run.FontStyle,
            GetTextDecorationFlags(run.TextDecorations));

        if (_formattedTextCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        EvictFormattedTextCacheEntriesIfNeeded();

        var foregroundBrush = ResolveColorBrush(run.ForegroundColor, isForeground: true);
        var formattedText = new FormattedText(run.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, foregroundBrush);
        if (run.TextDecorations != null)
        {
            formattedText.SetTextDecorations(run.TextDecorations);
        }

        formattedText.SetFontWeight(run.FontWeight);
        formattedText.SetFontStyle(run.FontStyle);
        _formattedTextCache[cacheKey] = formattedText;
        _formattedTextCacheOrder.Enqueue(cacheKey);
        return formattedText;
    }

    private void EvictFormattedTextCacheEntriesIfNeeded()
    {
        while (_formattedTextCache.Count >= MaxFormattedTextCacheEntries && _formattedTextCacheOrder.Count > 0)
        {
            var oldestKey = _formattedTextCacheOrder.Dequeue();
            _formattedTextCache.Remove(oldestKey);
        }
    }

    private void ClearFormattedTextCache()
    {
        _formattedTextCache.Clear();
        _formattedTextCacheOrder.Clear();
    }

    private static TextDecorationFlags GetTextDecorationFlags(TextDecorationCollection? decorations)
    {
        if (decorations is null || decorations.Count == 0)
        {
            return TextDecorationFlags.None;
        }

        TextDecorationFlags flags = TextDecorationFlags.None;
        foreach (var decoration in decorations)
        {
            flags |= decoration.Location switch
            {
                TextDecorationLocation.Underline => TextDecorationFlags.Underline,
                TextDecorationLocation.Strikethrough => TextDecorationFlags.Strikethrough,
                TextDecorationLocation.Overline => TextDecorationFlags.Overline,
                TextDecorationLocation.Baseline => TextDecorationFlags.Baseline,
                _ => TextDecorationFlags.None,
            };
        }

        return flags;
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
    internal IBrush SelectionBrushForTests => ResolveSelectionBrush();
    internal IBrush ResolveXtermColorForTests(int xtermColor) => ResolvePaletteBrush(xtermColor);
}

internal readonly record struct FormattedTextCacheKey(
    string Text,
    int ForegroundColor,
    FontWeight FontWeight,
    FontStyle FontStyle,
    TextDecorationFlags TextDecorations);

[Flags]
internal enum TextDecorationFlags
{
    None = 0,
    Underline = 1 << 0,
    Strikethrough = 1 << 1,
    Overline = 1 << 2,
    Baseline = 1 << 3,
}
