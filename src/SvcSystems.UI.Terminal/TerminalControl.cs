using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using XTerm.Buffer;
using XTerm.Common;
using XTerm.Input;
using XTerm.Options;
using Key = Avalonia.Input.Key;
using KeyModifiers = Avalonia.Input.KeyModifiers;
using MouseButton = Avalonia.Input.MouseButton;
using XKey = XTerm.Input.Key;
using XMouseButton = XTerm.Input.MouseButton;
using XTermModifiers = XTerm.Input.KeyModifiers;

namespace SvcSystems.UI.Terminal;

public partial class TerminalControl : Grid
{
    private static readonly TimeSpan SelectionAutoScrollInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan DefaultRedrawInterval = TimeSpan.FromSeconds(1d / 30);
    private static readonly Brush[] FallbackXtermPalette = CreateFallbackXtermPalette();
    // Standard cursor handles are immutable platform resources; sharing them avoids allocating a
    // disposable cursor for every OSC 22 update and keeps their lifetime independent of one view.
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor HorizontalResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor VerticalResizeCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor AllResizeCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor NorthEastSouthWestResizeCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor NorthWestSouthEastResizeCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor SouthEastResizeCursor = new(StandardCursorType.BottomRightCorner);
    private static readonly Cursor SouthWestResizeCursor = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor NoCursor = new(StandardCursorType.No);
    private static readonly Cursor ProgressCursor = new(StandardCursorType.AppStarting);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor WaitCursor = new(StandardCursorType.Wait);
    private static readonly Cursor HelpCursor = new(StandardCursorType.Help);
    private static readonly Cursor CopyCursor = new(StandardCursorType.DragCopy);
    private const int MaxFormattedTextCacheEntries = 16384;

    private Size _consoleTextSize;

    private Typeface _typeface;

    private readonly TerminalSurface _surface;

    private readonly ControlRedrawGate _redrawGate;

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
    private readonly DispatcherTimer _imageAnimationTimer;
    private readonly Dictionary<FormattedTextCacheKey, FormattedText> _formattedTextCache = [];
    private readonly Queue<FormattedTextCacheKey> _formattedTextCacheOrder = [];
    private readonly Dictionary<PressedKeyId, PressedKey> _pressedKeys = [];
    private readonly SixelBitmapCache _sixelBitmaps = new();
    private readonly TerminalImageBitmapCache _imageBitmaps = new();
    private TopLevel? _scalingTopLevel;
    private bool _canRenderSixels = true;
    private bool _canRenderImages = true;
    private long _lastImageAnimationTimestamp;
    private Task _clipboardWriteQueue = Task.CompletedTask;
    private Task _clipboardReadQueue = Task.CompletedTask;
    private Cursor? _cursorBeforeTerminalPointerShape;
    private bool _hasTerminalPointerShape;

    // Some backends raise TextInput even for a handled KeyDown, so the text of the last key
    // already sent is held until the next key to keep it from being sent twice.
    private string? _textOfSentKey;

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
        _redrawGate = new ControlRedrawGate(_surface, RedrawInterval);
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

        _imageAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _imageAnimationTimer.Tick += OnImageAnimationTimerTick;

        CalculateTextSize();
        UpdateScrollBar();
    }

    public static readonly StyledProperty<TerminalControlModel?> ModelProperty = AvaloniaProperty.Register<TerminalControl, TerminalControlModel?>(nameof(Model));

    public TerminalControlModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public static readonly StyledProperty<FontFamily> FontFamilyProperty = AvaloniaProperty.Register<TerminalControl, FontFamily>(nameof(FontFamily), new FontFamily("Cascadia Mono"));

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<TerminalControl, double>(nameof(FontSize), 12);

    /// <summary>Avalonia property for configuring this control's model-driven redraw interval.</summary>
    public static readonly StyledProperty<TimeSpan> RedrawIntervalProperty = AvaloniaProperty.Register<TerminalControl, TimeSpan>(
        nameof(RedrawInterval),
        DefaultRedrawInterval,
        validate: static value => value >= TimeSpan.Zero);

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>Gets or sets the minimum time between model-driven redraws. Set to zero to remove the interval limit.</summary>
    /// <exception cref="ArgumentException">The value is negative.</exception>
    public TimeSpan RedrawInterval
    {
        get => GetValue(RedrawIntervalProperty);
        set => SetValue(RedrawIntervalProperty, value);
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

    internal Size ConsoleTextSizeForTests => _consoleTextSize;

    internal int SixelBitmapUploadCountForTests => _sixelBitmaps.UploadCount;

    internal long SixelBitmapBytesForTests => _sixelBitmaps.CachedPixelBytes;

    internal int TerminalImageBitmapUploadCountForTests => _imageBitmaps.UploadCount;

    internal long TerminalImageBitmapBytesForTests => _imageBitmaps.CachedPixelBytes;

    internal bool IsImageAnimationTimerRunningForTests => _imageAnimationTimer.IsEnabled;

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
            Model?.Terminal.Engine.Paste(text);
        }
    }

    /// <summary>Raised when the running terminal application requests a notification.</summary>
    public event EventHandler<TerminalNotificationEventArgs>? NotificationRequested;

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
        if (ClipboardTextReaderOverride != null)
        {
            string? text = await ClipboardTextReaderOverride().ConfigureAwait(true);
            if (!string.IsNullOrEmpty(text))
            {
                Paste(text);
            }

            return;
        }

        IClipboard? clipboard = ResolveClipboard();
        if (clipboard is not null && Model is { } model)
        {
            long maximumClipboardBytes = Math.Max(1, model.Terminal.Engine.Options.MaxClipboardBytes);
            XTerm.TerminalPaste? paste = await ReadClipboardPasteAsync(clipboard, maximumClipboardBytes).ConfigureAwait(true);
            if (paste is not null)
            {
                model.Terminal.Engine.Paste(paste);
            }
        }
    }

    private static async Task<XTerm.TerminalPaste?> ReadClipboardPasteAsync(IClipboard clipboard, long maximumClipboardBytes)
    {
        IAsyncDataTransfer? transfer = await clipboard.TryGetDataAsync().ConfigureAwait(true);
        if (transfer is null)
        {
            string? text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
            return text is null ? null : CreateTextPaste(text);
        }

        using (transfer)
        {
            Dictionary<string, byte[]> formats = new(StringComparer.Ordinal);
            long totalBytes = 0;
            IReadOnlyList<IAsyncDataTransferItem> items = transfer.Items;
            for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                IAsyncDataTransferItem item = items[itemIndex];
                IReadOnlyList<DataFormat> itemFormats = item.Formats;
                for (int formatIndex = 0; formatIndex < itemFormats.Count; formatIndex++)
                {
                    DataFormat format = itemFormats[formatIndex];
                    string? mimeType = GetClipboardMimeType(format);
                    if (mimeType is null || formats.ContainsKey(mimeType))
                    {
                        continue;
                    }

                    object? value = await item.TryGetRawAsync(format).ConfigureAwait(true);
                    byte[]? data = GetClipboardBytes(value, mimeType, maximumClipboardBytes - totalBytes);
                    if (data is not null && data.LongLength <= maximumClipboardBytes - totalBytes)
                    {
                        formats.Add(mimeType, data);
                        totalBytes += data.LongLength;
                    }
                }
            }

            if (!formats.ContainsKey("text/plain") && await clipboard.TryGetTextAsync().ConfigureAwait(true) is { } plainText)
            {
                int byteCount = Encoding.UTF8.GetByteCount(plainText);
                if (byteCount <= maximumClipboardBytes - totalBytes)
                {
                    formats.Add("text/plain", Encoding.UTF8.GetBytes(plainText));
                }
            }

            if (formats.Count == 0)
            {
                return null;
            }

            string[] mimeTypes = formats.Keys.ToArray();
            return new XTerm.TerminalPaste(mimeTypes, mimeType => formats.GetValueOrDefault(mimeType));
        }
    }

    private static XTerm.TerminalPaste CreateTextPaste(string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        return new XTerm.TerminalPaste(["text/plain"], _ => data);
    }

    private static string? GetClipboardMimeType(DataFormat format)
    {
        if (format == DataFormat.Text)
        {
            return "text/plain";
        }

        if (format == DataFormat.Bitmap)
        {
            return "image/png";
        }

        if (format.Kind != DataFormatKind.Platform)
        {
            return null;
        }

        return format.Identifier switch
        {
            "public.utf8-plain-text" or "text/plain;charset=utf-8" => "text/plain",
            "public.html" => "text/html",
            "public.rtf" => "text/rtf",
            "public.png" => "image/png",
            _ when format.Identifier.Contains('/', StringComparison.Ordinal) => format.Identifier,
            _ => null,
        };
    }

    private static byte[]? GetClipboardBytes(object? value, string mimeType, long remainingBytes)
    {
        if (value is byte[] bytes && bytes.LongLength <= remainingBytes)
        {
            return bytes;
        }

        if (value is string text && Encoding.UTF8.GetByteCount(text) <= remainingBytes)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        if (value is Bitmap bitmap && string.Equals(mimeType, "image/png", StringComparison.Ordinal))
        {
            long pixelBytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * sizeof(uint);
            if (pixelBytes > remainingBytes)
            {
                return null;
            }

            using MemoryStream stream = new();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.Length <= remainingBytes ? stream.ToArray() : null;
        }

        return null;
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
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);

        if (change.Property == ModelProperty)
        {
            _sixelBitmaps.Clear();
            _imageBitmaps.Clear();
            _canRenderSixels = true;
            _canRenderImages = true;
            StopImageAnimationTimer();

            if (change.OldValue is TerminalControlModel oldModel && oldModel.UpdateUI == RefreshFromModel)
            {
                oldModel.UpdateUI = null;
            }

            if (change.OldValue is TerminalControlModel oldClipboardModel)
            {
                oldClipboardModel.Terminal.Engine.ClipboardWriteRequested -= OnTerminalClipboardWriteRequested;
                oldClipboardModel.Terminal.Engine.ClipboardReadRequested -= OnTerminalClipboardReadRequested;
                oldClipboardModel.Terminal.Engine.PointerShapeChanged -= OnTerminalPointerShapeChanged;
                oldClipboardModel.Terminal.Engine.NotificationReceived -= OnTerminalNotificationReceived;
                oldClipboardModel.Terminal.Engine.OscReceived -= OnTerminalOscReceived;
            }

            ClearTerminalPointerShape();

            if (change.NewValue is TerminalControlModel newModel)
            {
                newModel.UpdateUI = RefreshFromModel;
                newModel.Terminal.Engine.ClipboardWriteRequested += OnTerminalClipboardWriteRequested;
                newModel.Terminal.Engine.ClipboardReadRequested += OnTerminalClipboardReadRequested;
                newModel.Terminal.Engine.PointerShapeChanged += OnTerminalPointerShapeChanged;
                newModel.Terminal.Engine.NotificationReceived += OnTerminalNotificationReceived;
                newModel.Terminal.Engine.OscReceived += OnTerminalOscReceived;
            }

            _pressedKeys.Clear();
            _textOfSentKey = null;
            SyncSelectionStateFromModel();
            SyncTerminalCellSize();
            UpdateScrollBar();
            ResizeModelToViewport();
            UpdateImageAnimationTimerState();
            _surface.InvalidateVisual();
        }

        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            CalculateTextSize();
            ClearFormattedTextCache();
            ResizeModelToViewport();
            _surface.InvalidateVisual();
        }

        if (change.Property == RedrawIntervalProperty)
        {
            _redrawGate.SetMinimumInterval((TimeSpan)change.NewValue!);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scalingTopLevel = TopLevel.GetTopLevel(this);
        if (_scalingTopLevel is not null)
        {
            _scalingTopLevel.ScalingChanged += OnScalingChanged;
        }

        _canRenderSixels = true;
            _redrawGate.Resume();
        RefreshControlStateFromModel();
        SyncTerminalCellSize();
        UpdateImageAnimationTimerState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scalingTopLevel is not null)
        {
            _scalingTopLevel.ScalingChanged -= OnScalingChanged;
            _scalingTopLevel = null;
        }

        _redrawGate.Suspend();
        StopImageAnimationTimer();
        _sixelBitmaps.Clear();
        _imageBitmaps.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        SyncTerminalCellSize();
        ResizeModelToViewport();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        _textOfSentKey = null;

        if (Model == null)
        {
            return;
        }

        Model.ClearSelection();

        if (TrySendKittyKey(e, isRelease: false, out var keyText))
        {
            MarkKeySent(e, keyText);
            return;
        }

        // The protocol leaves Meta to the host, and the legacy encoder would drop it and send
        // the bare key.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) && Model.Terminal.Engine.KittyKeyboardActive)
        {
            return;
        }

        bool handled = false;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (TrySendGeneratedKey(e.Key, e.KeyModifiers))
            {
                handled = true;
            }
            else if (TrySendControlCharacter(e.Key, out var controlCharacter))
            {
                Model.Send([controlCharacter]);
                handled = true;
            }
            else
            {
                switch (e.Key)
                {
                    default:
                        if (!string.IsNullOrEmpty(e.KeySymbol))
                        {
                            Model.Send(e.KeySymbol);
                            handled = true;
                        }
                        break;
                }
            }
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (TrySendGeneratedKey(e.Key, e.KeyModifiers))
            {
                handled = true;
            }
            else if (!string.IsNullOrEmpty(e.KeySymbol))
            {
                if (Model.OptionAsMetaKey)
                {
                    Model.Send($"\u001b{e.KeySymbol}");
                }
                else
                {
                    Model.Send(e.KeySymbol);
                }
                handled = true;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Enter:
                    handled = true;
                    Model.Send([0x0D]);
                    break;
                case Key.PageUp:
                    handled = true;
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
                    handled = true;
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
                    if (TrySendGeneratedKey(e.Key, e.KeyModifiers))
                    {
                        handled = true;
                    }
                    break;
                default:
                    if (TrySendGeneratedKey(e.Key, e.KeyModifiers))
                    {
                        handled = true;
                        break;
                    }
                    break;
            }
        }

        if (handled)
        {
            MarkKeySent(e);
        }
    }

    // Text input carries the key's symbol, or the letter or digit the key was translated to when
    // the backend gives it none.
    private void MarkKeySent(KeyEventArgs e, string? keyText = null)
    {
        e.Handled = true;
        _textOfSentKey = string.IsNullOrEmpty(e.KeySymbol) ? keyText : e.KeySymbol;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyUp(e);

        if (TrySendKittyKey(e, isRelease: true, out _))
        {
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnTextInput(e);

        if (Model == null || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        // Text that differs from the key, such as a composed or IME character, still goes through.
        if (string.Equals(e.Text, _textOfSentKey, StringComparison.Ordinal))
        {
            _textOfSentKey = null;
            e.Handled = true;
            return;
        }

        bool hasPrintableCharacter = false;
        foreach (char character in e.Text)
        {
            if (!char.IsControl(character))
            {
                hasPrintableCharacter = true;
                break;
            }
        }

        if (!hasPrintableCharacter)
        {
            return;
        }

        Model.Send(e.Text);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
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
        ArgumentNullException.ThrowIfNull(e);
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
        ArgumentNullException.ThrowIfNull(e);
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
        ArgumentNullException.ThrowIfNull(e);
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
        SendFocusEvent(isFocused: true);
        _surface.InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _hasFocus = false;
        _textOfSentKey = null;
        ReleasePressedKeys();
        SendFocusEvent(isFocused: false);
        _surface.InvalidateVisual();
    }

    private void SendFocusEvent(bool isFocused)
    {
        if (Model is not { } model)
        {
            return;
        }

        string input = model.Terminal.Engine.GenerateFocusEvent(isFocused);
        if (input.Length > 0)
        {
            model.Send(input);
        }
    }

    private static bool IsClipboardTargetSupported(string target)
    {
        return string.Equals(target, "c", StringComparison.Ordinal) ||
            string.Equals(target, "s", StringComparison.Ordinal);
    }

    private void OnTerminalPointerShapeChanged(object? sender, XTerm.Events.TerminalEvents.PointerShapeEventArgs e)
    {
        string? shape = e.Shape;
        Dispatcher.UIThread.Post(() => ApplyTerminalPointerShape(shape));
    }

    private void ApplyTerminalPointerShape(string? shape)
    {
        if (shape is null)
        {
            ClearTerminalPointerShape();
            return;
        }

        Cursor? cursor = GetPointerCursor(shape);
        if (cursor is null)
        {
            return;
        }

        if (!_hasTerminalPointerShape)
        {
            _cursorBeforeTerminalPointerShape = Cursor;
            _hasTerminalPointerShape = true;
        }

        SetCurrentValue(CursorProperty, cursor);
    }

    private void ClearTerminalPointerShape()
    {
        if (!_hasTerminalPointerShape)
        {
            return;
        }

        SetCurrentValue(CursorProperty, _cursorBeforeTerminalPointerShape);
        _cursorBeforeTerminalPointerShape = null;
        _hasTerminalPointerShape = false;
    }

    private static Cursor? GetPointerCursor(string shape)
    {
        return shape switch
        {
            "alias" or "copy" => CopyCursor,
            "all-scroll" or "move" => AllResizeCursor,
            "auto" or "default" => Cursor.Default,
            "cell" or "crosshair" => CrossCursor,
            "col-resize" or "e-resize" or "ew-resize" or "w-resize" => HorizontalResizeCursor,
            "context-menu" or "help" => HelpCursor,
            "grab" or "grabbing" or "pointer" => HandCursor,
            "n-resize" or "ns-resize" or "row-resize" or "s-resize" => VerticalResizeCursor,
            "ne-resize" => NorthEastSouthWestResizeCursor,
            "nw-resize" => NorthWestSouthEastResizeCursor,
            "se-resize" => SouthEastResizeCursor,
            "sw-resize" => SouthWestResizeCursor,
            "no-drop" or "not-allowed" => NoCursor,
            "progress" => ProgressCursor,
            "text" or "vertical-text" => TextCursor,
            "wait" => WaitCursor,
            _ => null,
        };
    }

    private void OnTerminalNotificationReceived(object? sender, XTerm.Events.TerminalEvents.NotificationEventArgs e)
    {
        TerminalNotificationEventArgs notification = new(e.Identifier, e.Title, e.Body, e.Urgency, e.Icon);
        Dispatcher.UIThread.Post(() => NotificationRequested?.Invoke(this, notification));
    }

    private void OnTerminalOscReceived(object? sender, XTerm.Events.TerminalEvents.OscReceivedEventArgs e)
    {
        TerminalControlModel? model = Model;
        if (model is null || !TerminalFontCoverageService.TryCreateResponse(e, FontFamily, out string response))
        {
            return;
        }

        model.Send(response);
    }

    private void OnTerminalClipboardWriteRequested(object? sender, XTerm.Events.TerminalEvents.ClipboardWriteEventArgs e)
    {
        if (Model?.Terminal.Engine.Options.ClipboardWriteEnabled != true || !IsClipboardTargetSupported(e.Target))
        {
            return;
        }

        string? text = null;
        for (int i = 0; i < e.Formats.Count; i++)
        {
            if (string.Equals(e.Formats[i].MimeType, "text/plain", StringComparison.Ordinal))
            {
                text = Encoding.UTF8.GetString(e.Formats[i].Data);
                break;
            }
        }

        if (text is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => QueueClipboardWrite(text));
    }

    private void QueueClipboardWrite(string text)
    {
        _clipboardWriteQueue = _clipboardWriteQueue.ContinueWith(
            _ => WriteClipboardTextAsync(text),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
    }

    private async Task WriteClipboardTextAsync(string text)
    {
        try
        {
            if (ClipboardTextWriterOverride is { } writer)
            {
                await writer(text).ConfigureAwait(true);
                return;
            }

            IClipboard? clipboard = ResolveClipboard();
            if (clipboard is null)
            {
                return;
            }

            if (text.Length == 0)
            {
                await clipboard.ClearAsync().ConfigureAwait(true);
            }
            else
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (IsExpectedClipboardFailure(exception))
        {
            // The terminal protocol has no error response for a failed host clipboard write.
        }
    }

    private void OnTerminalClipboardReadRequested(object? sender, XTerm.Events.TerminalEvents.ClipboardReadEventArgs e)
    {
        if (Model?.Terminal.Engine.Options.ClipboardReadEnabled != true || !IsClipboardTargetSupported(e.Target))
        {
            return;
        }

        if (string.Equals(e.MimeType, ".", StringComparison.Ordinal))
        {
            e.Text = "text/plain";
            return;
        }

        if (e.MimeType.Length > 0 && !string.Equals(e.MimeType, "text/plain", StringComparison.Ordinal))
        {
            return;
        }

        e.Defer();
        Dispatcher.UIThread.Post(() => QueueClipboardRead(e));
    }

    private void QueueClipboardRead(XTerm.Events.TerminalEvents.ClipboardReadEventArgs request)
    {
        _clipboardReadQueue = _clipboardReadQueue.ContinueWith(
            _ => RespondToClipboardReadAsync(request),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
    }

    private async Task RespondToClipboardReadAsync(XTerm.Events.TerminalEvents.ClipboardReadEventArgs request)
    {
        string? text = null;
        try
        {
            if (ClipboardTextReaderOverride is { } reader)
            {
                text = await reader().ConfigureAwait(true);
            }
            else if (ResolveClipboard() is { } clipboard)
            {
                text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (IsExpectedClipboardFailure(exception))
        {
            // An unavailable or locked clipboard is answered as an empty read.
        }

        request.Respond(text);
    }

    private static bool IsExpectedClipboardFailure(Exception exception)
    {
        return exception is InvalidOperationException or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException or
            OperationCanceledException or
            System.Runtime.InteropServices.ExternalException;
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
        return $"SvcSystems.UI.TerminalColor{xtermColor}";
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
            SyncTerminalCellSize();
            return;
        }

        if (TryCalculateTextMetrics(Avalonia.Media.FontFamily.Default, FontSize, out _typeface, out _consoleTextSize))
        {
            _canRenderText = true;
            SyncTerminalCellSize();
            return;
        }

        _typeface = new Typeface(Avalonia.Media.FontFamily.Default);
        _consoleTextSize = new Size(Math.Max(FontSize * 0.6, 1), Math.Max(FontSize * 1.4, 1));
        _canRenderText = true;
        SyncTerminalCellSize();
    }

    private void SyncTerminalCellSize()
    {
        if (Model is null || _consoleTextSize.Width <= 0 || _consoleTextSize.Height <= 0)
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max((int)Math.Round(_consoleTextSize.Width * scaling), 1);
        var height = Math.Max((int)Math.Round(_consoleTextSize.Height * scaling), 1);
        if (Model.Terminal.Options.CellWidthPixels != width || Model.Terminal.Options.CellHeightPixels != height)
        {
            Model.Terminal.SetCellSizePixels(width, height);
        }
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
            using var run = new ShapedTextRun(shaped, new GenericTextRunProperties(typeface, fontSize));
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
        _redrawGate.Request(RefreshControlStateFromModel);
    }

    private void RefreshControlStateFromModel()
    {
        SyncSelectionStateFromModel();
        UpdateScrollBar();
        UpdateImageAnimationTimerState();
    }

    private void UpdateImageAnimationTimerState()
    {
        bool shouldRun = VisualRoot is not null && (Model?.Terminal.Engine.HasRunningAnimations() ?? false);
        if (shouldRun == _imageAnimationTimer.IsEnabled)
        {
            return;
        }

        if (shouldRun)
        {
            _lastImageAnimationTimestamp = Stopwatch.GetTimestamp();
            _imageAnimationTimer.Start();
        }
        else
        {
            StopImageAnimationTimer();
        }
    }

    private void StopImageAnimationTimer()
    {
        if (_imageAnimationTimer.IsEnabled)
        {
            _imageAnimationTimer.Stop();
        }
    }

    private void OnImageAnimationTimerTick(object? sender, EventArgs e)
    {
        if (Model is not { } model)
        {
            StopImageAnimationTimer();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastImageAnimationTimestamp, now);
        _lastImageAnimationTimestamp = now;

        if (model.Terminal.Engine.AdvanceAnimations(elapsed))
        {
            _redrawGate.Request();
        }

        UpdateImageAnimationTimerState();
    }

    internal bool IsCaretFocused => _hasFocus;

    internal bool HasVisibleCaret => TryGetCaretRect(out _);

    internal Rect CaretRect => TryGetCaretRect(out var rect) ? rect : default;

    internal IBrush CaretBrushForTests => ResolveCaretBrush();

    internal IReadOnlyList<Rect> SelectionRectsForTests => [.. GetSelectionRects()];

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
            MouseEventType.Down,
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

        var shouldSendMotion = mode == MouseTrackingMode.AnyEvent || (mode == MouseTrackingMode.ButtonEvent && hasPressedButton);
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
            MouseEventType.Drag,
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
            MouseEventType.Up,
            col,
            row,
            e.KeyModifiers);
        return true;
    }

    private static bool ShouldSendTerminalMousePress(MouseTrackingMode mode)
    {
        return mode == MouseTrackingMode.X10 || mode == MouseTrackingMode.VT200 || mode == MouseTrackingMode.ButtonEvent || mode == MouseTrackingMode.AnyEvent;
    }

    private static bool ShouldSendTerminalMouseRelease(MouseTrackingMode mode)
    {
        return mode == MouseTrackingMode.VT200 || mode == MouseTrackingMode.ButtonEvent || mode == MouseTrackingMode.AnyEvent;
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
                    var selectedText = CopySelection();
                    Model?.ClearSelection();
                    _ = CopyTextToClipboardAsync(selectedText);
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

    private async Task CopyTextToClipboardAsync(string text)
    {
        if (ClipboardTextWriterOverride != null)
        {
            await ClipboardTextWriterOverride(text).ConfigureAwait(true);
            return;
        }

        var clipboard = ResolveClipboard();
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
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
                button == 4 ? MouseEventType.WheelUp : MouseEventType.WheelDown,
                col,
                row,
                KeyModifiers.None);
        }
    }

    private void SendMouseSequence(XMouseButton button, MouseEventType eventType, int col, int row, KeyModifiers modifiers)
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

    // Encodes a key under the kitty keyboard protocol while the application has it switched on.
    // A false return leaves the key to the legacy path, or to text input when it only types.
    // The key text is the key as the protocol names it.
    private bool TrySendKittyKey(KeyEventArgs e, bool isRelease, out string? keyText)
    {
        keyText = null;

        if (Model == null)
        {
            return false;
        }

        var keyId = PressedKeyId.From(e);
        var engine = Model.Terminal.Engine;

        if (!engine.KittyKeyboardActive)
        {
            _pressedKeys.Remove(keyId);
            return false;
        }

        var keyEvent = KeyEventTranslator.Translate(e, Model.OptionAsMetaKey);
        KittyKeyboardEventType eventType;

        if (isRelease)
        {
            // Only a key that went down under the protocol is released under it. The release
            // often carries no symbol, so it reports the key as it was pressed.
            if (!_pressedKeys.Remove(keyId, out var pressed))
            {
                return false;
            }

            keyEvent.Key = pressed.Key;
            keyEvent.Code = pressed.Code;
            eventType = KittyKeyboardEventType.Release;
        }
        else
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || ScrollsViewport(e))
            {
                _pressedKeys.Remove(keyId);
                return false;
            }

            eventType = _pressedKeys.ContainsKey(keyId) ? KittyKeyboardEventType.Repeat : KittyKeyboardEventType.Press;
            _pressedKeys[keyId] = new PressedKey(keyEvent.Key, keyEvent.Code);
        }

        var sequence = engine.GenerateKittyKeyInput(keyEvent, eventType);
        if (string.IsNullOrEmpty(sequence) || IsLeftToTextInput(e, sequence))
        {
            return false;
        }

        Model.Send(sequence);
        keyText = keyEvent.Key;
        return true;
    }

    // The key-up of a key held when the focus moves goes to whatever took the focus, so the
    // keys still down are released here or the application never sees them go up.
    private void ReleasePressedKeys()
    {
        if (_pressedKeys.Count == 0)
        {
            return;
        }

        PressedKey[] pressedKeys = [.. _pressedKeys.Values];
        _pressedKeys.Clear();

        if (Model is not { } model || !model.Terminal.Engine.KittyKeyboardActive)
        {
            return;
        }

        foreach (var pressed in pressedKeys)
        {
            var keyEvent = new KeyEvent { Key = pressed.Key, Code = pressed.Code };
            var sequence = model.Terminal.Engine.GenerateKittyKeyInput(keyEvent, KittyKeyboardEventType.Release);
            if (!string.IsNullOrEmpty(sequence))
            {
                model.Send(sequence);
            }
        }
    }

    private bool ScrollsViewport(KeyEventArgs e)
    {
        const KeyModifiers sentToTheApplication = KeyModifiers.Control | KeyModifiers.Alt;

        return e.Key is Key.PageUp or Key.PageDown
            && (e.KeyModifiers & sentToTheApplication) == KeyModifiers.None
            && Model?.Terminal.Engine.ApplicationCursorKeys == false;
    }

    // Text reaches the application through text input, which is also how composed and
    // dead-key characters arrive, so a key that only types is not sent from here as well.
    private static bool IsLeftToTextInput(KeyEventArgs e, string sequence)
    {
        const KeyModifiers sentFromKeyDown = KeyModifiers.Control | KeyModifiers.Alt;

        return (e.KeyModifiers & sentFromKeyDown) == KeyModifiers.None
            && sequence.Length == 1
            && !char.IsControl(sequence[0]);
    }

    private bool TrySendGeneratedKey(Key key, KeyModifiers modifiers)
    {
        if (Model == null || !TryMapKey(key, out var mappedKey, out var extraModifiers))
        {
            return false;
        }

        var filteredModifiers = modifiers & (KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control);
        SendGeneratedKey(mappedKey, extraModifiers | ToXTermModifiers(filteredModifiers));
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
            case Key.Insert:
                mappedKey = XKey.Insert;
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

    private static bool TrySendControlCharacter(Key key, out byte controlCharacter)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            controlCharacter = (byte)(key - Key.A + 1);
            return true;
        }

        switch (key)
        {
            case Key.Space:
            case Key.D2:
                controlCharacter = 0x00;
                return true;
            case Key.D3:
            case Key.OemOpenBrackets:
                controlCharacter = 0x1B;
                return true;
            case Key.D4:
            case Key.OemBackslash:
                controlCharacter = 0x1C;
                return true;
            case Key.D5:
            case Key.OemCloseBrackets:
                controlCharacter = 0x1D;
                return true;
            case Key.D6:
                controlCharacter = 0x1E;
                return true;
            case Key.D7:
            case Key.OemMinus:
                controlCharacter = 0x1F;
                return true;
            case Key.D8:
                controlCharacter = 0x7F;
                return true;
            default:
                controlCharacter = 0;
                return false;
        }
    }

    private static XTermModifiers ToXTermModifiers(KeyModifiers modifiers)
    {
        XTermModifiers result = XTermModifiers.None;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= XTermModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= XTermModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Control))
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
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The graphics bitmap caches own and dispose their bitmaps when content changes or leaves the viewport.")]
        public override void Render(DrawingContext context)
        {
            var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
            context.FillRectangle(owner.ResolvePaletteBrush(0), rect);

            if (owner.Model == null)
            {
                owner._sixelBitmaps.Clear();
                owner._imageBitmaps.Clear();
                return;
            }

            owner.SyncTerminalCellSize();
            owner._sixelBitmaps.BeginFrame();
            owner._imageBitmaps.BeginFrame();
            try
            {
                foreach (var row in owner.Model.ViewportRows)
                {
                    foreach (var run in row.Runs)
                    {
                        DrawRunBackground(context, row, run);
                    }
                }

                DrawSizedTextBlocks(context, owner.Model.ViewportRows, drawBackground: true);
                DrawOffscreenSizedTextBlocks(context, drawBackground: true);

                DrawKittyImages(context, rect, owner.Model.ViewportRows, drawBehindText: true);

                foreach (var row in owner.Model.ViewportRows)
                {
                    foreach (var run in row.Runs)
                    {
                        if (!owner._canRenderText || run.Invisible || !run.Sizing.IsDefault)
                        {
                            continue;
                        }

                        var formattedText = owner.GetOrCreateFormattedText(run);
                        context.DrawText(formattedText, new Point(
                            owner._consoleTextSize.Width * run.StartColumn,
                            owner._consoleTextSize.Height * row.RowIndex));
                        owner.DrawTerminalDecorations(context, run, row.RowIndex);
                    }
                }

                DrawSizedTextBlocks(context, owner.Model.ViewportRows, drawBackground: false);
                DrawOffscreenSizedTextBlocks(context, drawBackground: false);

                if (owner._canRenderSixels)
                {
                    using (context.PushClip(rect))
                    {
                        foreach (var row in owner.Model.ViewportRows)
                        {
                            if (row.Sixel is not { } strip ||
                                !SixelDrawPlan.TryCreate(
                                    strip,
                                    row.SixelColumns,
                                    row.RowIndex,
                                    owner._consoleTextSize.Width,
                                    owner._consoleTextSize.Height,
                                    owner._scalingTopLevel?.RenderScaling ?? 1,
                                    rect,
                                    out var source,
                                    out var destination))
                            {
                                continue;
                            }

                            var bitmap = owner._sixelBitmaps.GetOrCreate(strip, row.SixelColumns);
                            if (bitmap is null)
                            {
                                continue;
                            }

                            try
                            {
                                context.DrawImage(bitmap, source, destination);
                            }
                            catch (Exception exception) when (exception is NotImplementedException or NotSupportedException)
                            {
                                owner._canRenderSixels = false;
                                owner._sixelBitmaps.Clear();
                                break;
                            }
                        }
                    }
                }

                DrawKittyImages(context, rect, owner.Model.ViewportRows, drawBehindText: false);

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
            finally
            {
                owner._sixelBitmaps.EndFrame();
                owner._imageBitmaps.EndFrame();
            }
        }

        private void DrawRunBackground(DrawingContext context, ViewportRow row, ViewportTextRun run)
        {
            if (!run.Sizing.IsDefault)
            {
                return;
            }

            int start = run.StartColumn;
            int end = start + run.CellWidth;
            bool hasOverlappingBehindImage = false;
            for (int index = 0; index < row.BehindImageCount; index++)
            {
                ViewportImagePlacement image = row.Images[index];
                if (image.Placement.ZIndex < 0 && image.Placement.Column < end && image.Placement.EndColumn > start)
                {
                    hasOverlappingBehindImage = true;
                    break;
                }
            }

            if (!hasOverlappingBehindImage)
            {
                FillBackground(context, row, run, start, run.CellWidth);
                return;
            }

            int segmentStart = start;

            for (int column = start; column < end; column++)
            {
                if (!HasBehindImageAt(row, column))
                {
                    continue;
                }

                if (segmentStart < column)
                {
                    FillBackground(context, row, run, segmentStart, column - segmentStart);
                }

                segmentStart = column + 1;
            }

            if (segmentStart < end)
            {
                FillBackground(context, row, run, segmentStart, end - segmentStart);
            }
        }

        private void DrawSizedTextBlocks(
            DrawingContext context,
            IReadOnlyList<ViewportRow> rows,
            bool drawBackground)
        {
            foreach (ViewportRow row in rows)
            {
                foreach (ViewportTextRun run in row.Runs)
                {
                    if (run.Sizing.IsDefault)
                    {
                        continue;
                    }

                    if (drawBackground)
                    {
                        DrawSizedTextBackground(context, run, row.RowIndex);
                    }
                    else
                    {
                        DrawSizedText(context, run, row.RowIndex);
                    }
                }
            }
        }

        private void DrawOffscreenSizedTextBlocks(DrawingContext context, bool drawBackground)
        {
            TerminalControlModel? model = owner.Model;
            if (model is null)
            {
                return;
            }

            TerminalBuffer buffer = model.Terminal.Buffer;
            if (!buffer.HasMultiRowSizedRuns)
            {
                return;
            }

            int viewportStart = buffer.YDisp;
            for (int rowsAbove = 1; rowsAbove < TextSizing.MaxScale; rowsAbove++)
            {
                int anchorRow = viewportStart - rowsAbove;
                if (anchorRow < 0)
                {
                    break;
                }

                BufferLine? line = anchorRow < buffer.Lines.Length ? buffer.GetLine(anchorRow) : null;
                if (line is null || !line.HasSizedRuns)
                {
                    continue;
                }

                foreach (LineSizedRun sizedRun in line.SizedRuns)
                {
                    if (sizedRun.Rows <= rowsAbove ||
                        !TerminalControlModel.TryCreateSizedTextRun(line, sizedRun, model.Terminal.Cols, out ViewportTextRun run))
                    {
                        continue;
                    }

                    int rowIndex = -rowsAbove;
                    if (drawBackground)
                    {
                        DrawSizedTextBackground(context, run, rowIndex);
                    }
                    else
                    {
                        DrawSizedText(context, run, rowIndex);
                    }
                }
            }
        }

        private void DrawSizedTextBackground(DrawingContext context, ViewportTextRun run, int rowIndex)
        {
            Rect box = GetSizedTextBlockBox(run, rowIndex);
            if (box.Width > 0 && box.Height > 0)
            {
                context.FillRectangle(owner.ResolveColorBrush(run.BackgroundColor), box);
            }
        }

        private void DrawSizedText(DrawingContext context, ViewportTextRun run, int rowIndex)
        {
            if (!owner._canRenderText || run.Invisible || string.IsNullOrWhiteSpace(run.Text))
            {
                return;
            }

            TextSizing sizing = run.Sizing;
            double fraction = sizing.Numerator > 0 && sizing.Denominator > 0
                ? (double)sizing.Numerator / sizing.Denominator
                : 1;
            double magnification = sizing.Scale * fraction;
            if (magnification <= 0)
            {
                return;
            }

            FormattedText formattedText = owner.GetOrCreateFormattedText(run);
            Rect box = GetSizedTextBlockBox(run, rowIndex);
            if (box.Width <= 0 || box.Height <= 0)
            {
                return;
            }

            double drawnWidth = formattedText.Width * magnification;
            double drawnHeight = owner._consoleTextSize.Height * magnification;
            double x = sizing.HorizontalAlignment switch
            {
                TextSizeHorizontalAlignment.Right => box.Right - drawnWidth,
                TextSizeHorizontalAlignment.Center => box.X + (box.Width - drawnWidth) / 2,
                _ => box.X,
            };
            double y = sizing.VerticalAlignment switch
            {
                TextSizeVerticalAlignment.Bottom => box.Bottom - drawnHeight,
                TextSizeVerticalAlignment.Center => box.Y + (box.Height - drawnHeight) / 2,
                _ => box.Y,
            };

            using (context.PushClip(box))
            {
                Matrix transform = Matrix.CreateTranslation(-x, -y) *
                    Matrix.CreateScale(magnification, magnification) *
                    Matrix.CreateTranslation(x, y);
                using (context.PushTransform(transform))
                {
                    context.DrawText(formattedText, new Point(x, y));
                    owner.DrawTerminalDecorations(context, run, rowIndex);
                }
            }
        }

        private Rect GetSizedTextBlockBox(ViewportTextRun run, int rowIndex)
        {
            return new Rect(
                owner._consoleTextSize.Width * run.StartColumn,
                owner._consoleTextSize.Height * rowIndex,
                owner._consoleTextSize.Width * run.CellWidth,
                owner._consoleTextSize.Height * run.Sizing.Scale);
        }

        private static bool HasBehindImageAt(ViewportRow row, int column)
        {
            for (int index = 0; index < row.BehindImageCount; index++)
            {
                if (row.Images[index].Placement.Covers(column))
                {
                    return true;
                }
            }

            return false;
        }

        private void FillBackground(DrawingContext context, ViewportRow row, ViewportTextRun run, int startColumn, int columns)
        {
            var runRect = new Rect(
                owner._consoleTextSize.Width * startColumn,
                owner._consoleTextSize.Height * row.RowIndex,
                owner._consoleTextSize.Width * columns,
                owner._consoleTextSize.Height + 1);
            context.FillRectangle(owner.ResolveColorBrush(run.BackgroundColor), runRect);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The image bitmap cache retains and disposes each returned bitmap after it leaves the viewport.")]
        private void DrawKittyImages(
            DrawingContext context,
            Rect viewport,
            IReadOnlyList<ViewportRow> rows,
            bool drawBehindText)
        {
            if (!owner._canRenderImages)
            {
                return;
            }

            bool hasDraws = false;
            foreach (ViewportRow row in rows)
            {
                if (drawBehindText ? row.BehindImageCount > 0 : row.Images.Count > row.BehindImageCount)
                {
                    hasDraws = true;
                    break;
                }
            }

            if (!hasDraws)
            {
                return;
            }

            using (context.PushClip(viewport))
            {
                foreach (ViewportRow row in rows)
                {
                    int start = drawBehindText ? 0 : row.BehindImageCount;
                    int end = drawBehindText ? row.BehindImageCount : row.Images.Count;
                    for (int index = start; index < end; index++)
                    {
                        ViewportImagePlacement image = row.Images[index];
                        if (!TerminalImageDrawPlan.TryCreate(
                                image.Image,
                                image.Placement,
                                row.RowIndex,
                                owner._consoleTextSize.Width,
                                owner._consoleTextSize.Height,
                                owner._scalingTopLevel?.RenderScaling ?? 1,
                                viewport,
                                out Rect source,
                                out Rect destination))
                        {
                            continue;
                        }

                        Bitmap? bitmap = owner._imageBitmaps.GetOrCreate(image.Image);
                        if (bitmap is null)
                        {
                            continue;
                        }

                        try
                        {
                            context.DrawImage(bitmap, source, destination);
                        }
                        catch (Exception exception) when (exception is NotImplementedException or NotSupportedException)
                        {
                            owner._canRenderImages = false;
                            owner._imageBitmaps.Clear();
                            return;
                        }
                    }
                }
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

        if (TryGetApplicationResource("SvcSystems.UI.TerminalSelectionBrush") is IBrush resourceBrush)
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

        if (TryGetApplicationResource("SvcSystems.UI.TerminalCaretBrush") is IBrush resourceBrush)
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

        if (ResolveColorBrush(fg) is not ISolidColorBrush foregroundBrush ||
            ResolveColorBrush(bg) is not ISolidColorBrush backgroundBrush)
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

    private Brush ResolveColorBrush(int color)
    {
        return ResolveColorBrush(color, isRgb: false);
    }

    private Brush ResolveColorBrush(int color, bool isRgb)
    {
        if (isRgb)
        {
            byte redComponent = (byte)((color >> 16) & 0xFF);
            byte greenComponent = (byte)((color >> 8) & 0xFF);
            byte blueComponent = (byte)(color & 0xFF);
            return new SolidColorBrush(Color.FromRgb(redComponent, greenComponent, blueComponent));
        }

        if (color == 256)
        {
            return ResolvePaletteBrush(15);
        }

        if (color == 257)
        {
            return ResolvePaletteBrush(0);
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
            run.BackgroundColor,
            run.FontWeight,
            run.FontStyle,
            run.UnderlineStyle,
            run.UnderlineColor,
            run.UnderlineColorIsRgb,
            run.Overline,
            run.Dim,
            run.Invisible,
            run.Strikethrough);

        if (_formattedTextCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        EvictFormattedTextCacheEntriesIfNeeded();

        var foregroundBrush = ResolveTextBrush(run);
        var formattedText = new FormattedText(run.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, foregroundBrush);
        TextDecorationCollection? decorations = CreateTextDecorations(run);
        if (decorations is not null)
        {
            formattedText.SetTextDecorations(decorations);
        }

        formattedText.SetFontWeight(run.FontWeight);
        formattedText.SetFontStyle(run.FontStyle);
        _formattedTextCache[cacheKey] = formattedText;
        _formattedTextCacheOrder.Enqueue(cacheKey);
        return formattedText;
    }

    private IBrush ResolveTextBrush(ViewportTextRun run)
    {
        if (run.Invisible)
        {
            return ResolveColorBrush(run.BackgroundColor);
        }

        IBrush brush = ResolveColorBrush(run.ForegroundColor);
        if (!run.Dim)
        {
            return brush;
        }

        if (brush is ISolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color, solid.Opacity * 0.5);
        }

        return brush;
    }

    private static TextDecorationCollection? CreateTextDecorations(ViewportTextRun run)
    {
        TextDecorationCollection? decorations = null;
        if (run.Strikethrough)
        {
            decorations = new TextDecorationCollection(TextDecorations.Strikethrough);
        }

        return decorations;
    }

    private void DrawTerminalDecorations(DrawingContext context, ViewportTextRun run, int rowIndex)
    {
        if (run.Invisible || run.CellWidth <= 0 || (!run.Overline && run.UnderlineStyle == UnderlineStyle.None))
        {
            return;
        }

        double width = _consoleTextSize.Width * run.CellWidth;
        if (width <= 0)
        {
            return;
        }

        double x = _consoleTextSize.Width * run.StartColumn;
        double right = x + width;
        double top = _consoleTextSize.Height * rowIndex;
        double bottom = top + _consoleTextSize.Height;
        double thickness = Math.Max(1, _consoleTextSize.Height / 16);
        IBrush underlineBrush = run.UnderlineColor >= 0
            ? ResolveColorBrush(run.UnderlineColor, run.UnderlineColorIsRgb)
            : ResolveColorBrush(run.ForegroundColor);

        if (run.Overline)
        {
            IBrush overlineBrush = ResolveColorBrush(run.ForegroundColor);
            context.DrawLine(new Pen(overlineBrush, thickness), new Point(x, top + thickness), new Point(right, top + thickness));
        }

        double underlineY = bottom - Math.Max(1, _consoleTextSize.Height * 0.1);
        Pen underlinePen = new(underlineBrush, thickness);
        switch (run.UnderlineStyle)
        {
            case UnderlineStyle.Single:
                context.DrawLine(underlinePen, new Point(x, underlineY), new Point(right, underlineY));
                break;
            case UnderlineStyle.Double:
                context.DrawLine(underlinePen, new Point(x, underlineY - thickness * 1.5), new Point(right, underlineY - thickness * 1.5));
                context.DrawLine(underlinePen, new Point(x, underlineY), new Point(right, underlineY));
                break;
            case UnderlineStyle.Dotted:
                DrawDashedLine(context, underlineBrush, thickness, x, right, underlineY, dashLength: thickness, gapLength: thickness * 2);
                break;
            case UnderlineStyle.Dashed:
                DrawDashedLine(context, underlineBrush, thickness, x, right, underlineY, dashLength: thickness * 3, gapLength: thickness * 2);
                break;
            case UnderlineStyle.Curly:
                DrawCurlyLine(context, underlinePen, x, right, underlineY, Math.Max(0.75, _consoleTextSize.Height * 0.045));
                break;
        }
    }

    private static void DrawDashedLine(
        DrawingContext context,
        IBrush brush,
        double thickness,
        double x,
        double right,
        double y,
        double dashLength,
        double gapLength)
    {
        Pen pen = new(brush, thickness, lineCap: PenLineCap.Round);
        for (double start = x; start < right; start += dashLength + gapLength)
        {
            context.DrawLine(pen, new Point(start, y), new Point(Math.Min(start + dashLength, right), y));
        }
    }

    private static void DrawCurlyLine(DrawingContext context, Pen pen, double x, double right, double y, double amplitude)
    {
        const double step = 1.5;
        StreamGeometry geometry = new();
        using (StreamGeometryContext geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(new Point(x, y), isFilled: false);
            int pointCount = Math.Max(2, (int)Math.Ceiling((right - x) / step));
            for (int point = 1; point <= pointCount; point++)
            {
                double position = Math.Min(point * step, right - x);
                double offset = Math.Sin(position * Math.PI / 3) * amplitude;
                geometryContext.LineTo(new Point(x + position, y + offset), isStroked: true);
            }
        }

        context.DrawGeometry(null, pen, geometry);
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
    internal IBrush ResolveColorForTests(int color) => ResolveColorBrush(color);
    internal IBrush ResolveUnderlineColorForTests(ViewportTextRun run) => run.UnderlineColor >= 0
        ? ResolveColorBrush(run.UnderlineColor, run.UnderlineColorIsRgb)
        : ResolveColorBrush(run.ForegroundColor);
}

internal readonly record struct FormattedTextCacheKey(
    string Text,
    int ForegroundColor,
    int BackgroundColor,
    FontWeight FontWeight,
    FontStyle FontStyle,
    UnderlineStyle UnderlineStyle,
    int UnderlineColor,
    bool UnderlineColorIsRgb,
    bool Overline,
    bool Dim,
    bool Invisible,
    bool Strikethrough);

internal readonly record struct PressedKeyId(PhysicalKey PhysicalKey, Key Key)
{
    public static PressedKeyId From(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return e.PhysicalKey == PhysicalKey.None
            ? new PressedKeyId(PhysicalKey.None, e.Key)
            : new PressedKeyId(e.PhysicalKey, Key.None);
    }
}

internal readonly record struct PressedKey(string Key, string Code);
