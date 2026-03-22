using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using System.Globalization;
using XtermSharp;
using Point = Avalonia.Point;

namespace AvaloniaTerminal;

public partial class TerminalControl : Grid
{
    private const double ScrollBarWidth = 16;

    private Size _consoleTextSize;

    private Typeface _typeface;

    private readonly TerminalSurface _surface;

    private readonly ScrollBar _verticalScrollBar;

    private bool _canRenderText;

    private bool _isUpdatingScrollBar;

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
            Model.Send([0x1B]);
            if (!string.IsNullOrEmpty(e.KeySymbol))
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
        UpdateScrollBar();
        _surface.InvalidateVisual();
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
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            owner.ResizeModelToViewport();
        }
    }
}
