# AvaloniaTerminal
[![Nuget (with prereleases)](https://img.shields.io/nuget/vpre/AvaloniaTerminal.svg?style=flat-square)](https://www.nuget.org/packages/AvaloniaTerminal)
[![Nuget (with prereleases)](https://img.shields.io/nuget/dt/AvaloniaTerminal.svg?style=flat-square)](https://www.nuget.org/packages/AvaloniaTerminal)
[![codecov](https://codecov.io/gh/IvanJosipovic/AvaloniaTerminal/graph/badge.svg?token=cy0a3RcojP)](https://codecov.io/gh/IvanJosipovic/AvaloniaTerminal)

Avalonia terminal control built on top of [XTerm.NET](https://github.com/tomlm/XTerm.NET).

![screenshot](https://raw.githubusercontent.com/IvanJosipovic/AvaloniaTerminal/refs/heads/alpha/docs/Screenshot.png)

## Features

- Terminal rendering backed by `XTerm.NET`
- Standalone `Terminal` wrapper for direct engine integration
- Scrollback with mouse wheel, scrollbar, `PageUp`, and `PageDown`
- Caret rendering with theme-aware default styling
- Text selection with drag, double-click word selection, triple-click row selection, and drag auto-scroll
- Bindable selection state via `SelectedText` and `HasSelection`
- Search helpers for finding and navigating matches in the terminal buffer
- Mouse reporting mode support for xterm-compatible terminal apps
- Host-friendly context menu and clipboard hooks
- Configurable right-click behavior via `RightClickAction`
- Model-driven API for feeding terminal output and sending user input
- Sample desktop app with `Shell`, `Scroll`, and `Selection` tabs
- Windows sample shell backed by ConPTY, with redirected-shell fallback when ConPTY is unavailable
- Sample shell disables resize reflow to avoid TUI resize corruption in apps such as `mc`

## Install

```bash
dotnet add package AvaloniaTerminal
```

## Basic Usage

Add the control in XAML:

```xml
<Window
    xmlns="https://github.com/avaloniaui"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:terminal="using:AvaloniaTerminal">
    <terminal:TerminalControl Name="TerminalView" />
</Window>
```

Create and assign a `TerminalControlModel` in code-behind or a view model:

```csharp
using Avalonia.Controls;

namespace MyApp;

public partial class MainWindow : Window
{
    private readonly TerminalControlModel _terminal = new();

    public MainWindow()
    {
        InitializeComponent();
        TerminalView.Model = _terminal;
    }
}
```

You can also pass `AvaloniaTerminal.TerminalOptions` when you need non-default terminal behavior:

```csharp
var model = new TerminalControlModel(new TerminalOptions
{
    Cols = 120,
    Rows = 30,
    ReflowOnResize = false,
});
```

Feed terminal output:

```csharp
_terminal.Feed("Hello from AvaloniaTerminal\r\n");
```

Receive user input:

```csharp
_terminal.UserInput += (_, e) =>
{
    // Send bytes to a pty, process stdin, socket, ssh session, etc.
};
```

## Common Integration Pattern

The usual flow is:

1. Create a `TerminalControlModel`
2. Bind or assign it to `TerminalControl.Model`
3. Forward terminal output into `model.Feed(...)`
4. Forward `model.UserInput` to your process or remote shell

Example with a local process or remote session:

```csharp
var model = new TerminalControlModel();

model.UserInput += (_, e) =>
{
    var bytes = e.Data;
    process.StandardInput.BaseStream.Write(bytes.Span);
    process.StandardInput.BaseStream.Flush();
};

_ = Task.Run(async () =>
{
    var buffer = new byte[4096];

    while (true)
    {
        var read = await process.StandardOutput.BaseStream.ReadAsync(buffer);
        if (read == 0)
        {
            break;
        }

        model.Feed(buffer, read);
    }
});
```

## Core APIs

`TerminalControlModel`:

- `Feed(string)` / `Feed(byte[], int)` for terminal output
- `Send(string)` / `Send(byte[])` for programmatic input
- `ScrollLines(int)`, `PageUp()`, `PageDown()`, `ScrollToYDisp(int)`
- `ScrollToPosition(double)`, `EnsureCaretIsVisible()`
- `StartSelection(int, int)`, `StartSelectionFromSoftStart()`, `SetSoftSelectionStart(int, int)`
- `DragExtendSelection(int, int)`, `ShiftExtendSelection(int, int)`
- `SelectWordOrExpression(int, int)`, `SelectRow(int)`, `SelectAll()`, `ClearSelection()`
- `Search(string)`, `SelectNextSearchResult()`, `SelectPreviousSearchResult()`
- `SelectedText`, `HasSelection`
- `Title`
- `ScrollOffset`, `MaxScrollback`, `ScrollPosition`, `ScrollThumbsize`, `CanScroll`
- `CaretColumn`, `CaretRow`, `IsCaretVisible`
- `IsMouseModeActive`
- `SizeChanged`
- `Terminal`, `SearchService`
- `OptionAsMetaKey`

`Terminal`:

- `Feed(string)` / `Feed(byte[], int)` for terminal output
- `Resize(int, int)`
- `SwitchToAltBuffer()`, `SwitchToNormalBuffer()`
- `TitleChanged`
- `Engine`, `Buffer`, `Selection`, `IsAlternateBufferActive`
- `Cols`, `Rows`, `Title`

`TerminalControl`:

- `Model`
- `SelectedText`, `HasSelection`
- `RightClickAction`
- `IsMouseModeActive`
- `FontFamily`, `FontSize`, `CaretBrush`, `SelectionBrush`
- `SelectAll()`
- `CopySelection()`
- `CopySelectionAsync()`
- `Paste(string)`
- `PasteFromClipboardAsync()`
- `Search(string)`
- `SelectNextSearchResult()`
- `SelectPreviousSearchResult()`
- `ContextRequested`

`TerminalOptions` used by `TerminalControlModel`:

- `Cols`
- `Rows`
- `Scrollback`
- `ConvertEol`
- `TabStopWidth`
- `TermName`
- `ReflowOnResize`

## Selection And Context Menus

Clients often want to enable a context menu item only when text is selected. `TerminalControl` exposes that directly:

```csharp
if (TerminalView.HasSelection)
{
    var text = TerminalView.CopySelection();
}
```

You can also bind against `SelectedText` and `HasSelection` from the control or the model, or handle `ContextRequested` for a custom menu.

Selection behavior:

- drag selects text
- double-click selects a word or expression
- triple-click selects a full row
- dragging above or below the viewport auto-scrolls and keeps extending the selection

Right-click behavior is configurable:

- `ContextMenu`: raise `ContextRequested`
- `CopyOrPaste`: copy when selection exists, otherwise paste from the clipboard
- `None`: ignore right-click

Example:

```xml
<terminal:TerminalControl RightClickAction="CopyOrPaste" />
```

Programmatic clipboard helpers:

```csharp
await TerminalView.CopySelectionAsync();
await TerminalView.PasteFromClipboardAsync();
```

`ContextRequested` carries:

- pointer position relative to the control
- current `SelectedText`
- current `HasSelection`

## Search

Search is buffer-based

```csharp
var count = TerminalView.Search("error");

if (count > 0)
{
    TerminalView.SelectNextSearchResult();
}
```

Useful model properties:

- `SearchResultCount`
- `CurrentSearchResultIndex`
- `LastSearchText`

## Mouse Reporting

When the terminal application enables mouse reporting, `TerminalControl` forwards pointer press, release, and motion events to `XTerm.NET` instead of using them for text selection. This allows interactive terminal applications to receive mouse input.

This is controlled by the terminal app, not by the Avalonia host. If an app does not switch the terminal into xterm mouse mode, the control will keep using the pointer for normal text selection.

## Styling

The library defines its default terminal styling in:

- [`src/AvaloniaTerminal/Styles/Colors.axaml`](src/AvaloniaTerminal/Styles/Colors.axaml)

The desktop sample includes those resources automatically. If you host the control yourself, include the style resource in your application:

```xml
<Application.Styles>
    <StyleInclude Source="avares://AvaloniaTerminal/Styles/Colors.axaml" />
</Application.Styles>
```

`Colors.axaml` provides:

- the default `TerminalControl` font settings
- the exported 256-color terminal palette as `AvaloniaTerminalColor0` through `AvaloniaTerminalColor255`
- resource keys that the control reads for optional caret and selection overrides

Available resource keys:

- `AvaloniaTerminalFontFamily`
- `AvaloniaTerminalFontSize`
- `AvaloniaTerminalCaretBrush`
- `AvaloniaTerminalSelectionBrush`
- `AvaloniaTerminalColor0` ... `AvaloniaTerminalColor255`

You can override those resources at the application level to align the terminal with your app theme:

```xml
<Application.Resources>
    <x:String x:Key="AvaloniaTerminalFontFamily">Fira Code</x:String>
    <x:Double x:Key="AvaloniaTerminalFontSize">14</x:Double>
    <SolidColorBrush x:Key="AvaloniaTerminalCaretBrush" Color="#FFB000" />
    <SolidColorBrush x:Key="AvaloniaTerminalSelectionBrush" Color="#4060A0FF" />
    <SolidColorBrush x:Key="AvaloniaTerminalColor0" Color="#111111" />
    <SolidColorBrush x:Key="AvaloniaTerminalColor15" Color="#F5F5F5" />
</Application.Resources>
```

`TerminalControl` also exposes direct styling hooks when you want to customize a single instance:

- `FontFamily`
- `FontSize`
- `CaretBrush`
- `SelectionBrush`

Example:

```xml
<terminal:TerminalControl
    FontFamily="JetBrains Mono"
    FontSize="13"
    CaretBrush="Orange"
    SelectionBrush="#4060A0FF" />
```

Notes:

- font defaults come from `Colors.axaml` and can be overridden by application resources
- caret and selection use the control properties first, then the corresponding application resources, then the built-in fallback behavior
- ANSI foreground/background rendering uses the exported `AvaloniaTerminalColor*` resource keys, so hosts can replace the palette without changing library code

## Samples

The repo includes a shared samples project and a desktop sample host.

Current sample tabs:

- `Shell`: starts a platform-appropriate shell
  - Windows: prefers ConPTY-backed `pwsh.exe`, with redirected fallback
  - macOS/Linux: uses the existing redirected-shell sample backend
  - uses `RightClickAction="CopyOrPaste"`
  - constructs the model with `ReflowOnResize = false` to keep full-screen TUIs stable during window resize
- `Scroll`: preloaded scrollback sample
- `Selection`: demonstrates selection and bindable selected text

Desktop sample host:

- [`src/AvaloniaTerminal.Desktop/Views/MainWindow.axaml`](src/AvaloniaTerminal.Desktop/Views/MainWindow.axaml)

Shared sample controls:

- [`src/AvaloniaTerminal.Samples/ShellControl.axaml`](src/AvaloniaTerminal.Samples/ShellControl.axaml)
- [`src/AvaloniaTerminal.Samples/ScrollSampleControl.axaml`](src/AvaloniaTerminal.Samples/ScrollSampleControl.axaml)
- [`src/AvaloniaTerminal.Samples/SelectionControl.axaml`](src/AvaloniaTerminal.Samples/SelectionControl.axaml)

## Running The Sample App

```bash
dotnet run --project src/AvaloniaTerminal.Desktop
```

### Sample shell notes

- On Windows, the sample uses ConPTY when available.
- Full-screen TUIs such as `mc` render correctly with the current ambiguous-width fix and with resize reflow disabled in the sample shell.
- Mouse interaction in TUIs still depends on the application enabling xterm mouse reporting. Remote Linux apps often do this; some local Windows console apps may not.

## Testing

The repo has headless tests covering terminal behavior without needing a visible desktop session.

```bash
dotnet test --project tests/AvaloniaTerminal.Tests/AvaloniaTerminal.Tests.csproj -f net10.0
```
