# AvaloniaTerminal
[![Nuget (with prereleases)](https://img.shields.io/nuget/vpre/AvaloniaTerminal.svg?style=flat-square)](https://www.nuget.org/packages/AvaloniaTerminal)
[![Nuget (with prereleases)](https://img.shields.io/nuget/dt/AvaloniaTerminal.svg?style=flat-square)](https://www.nuget.org/packages/AvaloniaTerminal)

Avalonia terminal control built on top of [XtermSharp](https://github.com/migueldeicaza/XtermSharp).

![screenshot](https://raw.githubusercontent.com/IvanJosipovic/AvaloniaTerminal/refs/heads/alpha/docs/Screenshot.png)

## Features

- Terminal rendering backed by `XtermSharp`
- Scrollback with mouse wheel, scrollbar, `PageUp`, and `PageDown`
- Caret rendering with theme-aware default styling
- Text selection with drag, double-click word selection, triple-click row selection, and drag auto-scroll
- Bindable selection state via `SelectedText` and `HasSelection`
- Mouse reporting mode support for terminal apps such as `vim`, `less`, and `tmux`
- Search helpers for finding and navigating matches in the terminal buffer
- Host-friendly context and clipboard hooks
- Configurable right-click behavior via `RightClickAction`
- Model-driven API for feeding terminal output and sending user input
- Sample desktop app with `Shell`, `Scroll`, and `Selection` tabs
- Windows sample shell backed by ConPTY, with redirected-shell fallback when ConPTY is unavailable

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

Feed terminal output:

```csharp
_terminal.Feed("Hello from AvaloniaTerminal\r\n");
```

Receive user input:

```csharp
_terminal.UserInput += bytes =>
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

model.UserInput += bytes =>
{
    process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
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
- `Search(string)`, `SelectNextSearchResult()`, `SelectPreviousSearchResult()`
- `SelectAll()`, `ClearSelection()`
- `SelectedText`, `HasSelection`
- `ScrollOffset`, `MaxScrollback`
- `IsMouseModeActive`

`TerminalControl`:

- `Model`
- `SelectedText`, `HasSelection`
- `RightClickAction`
- `SelectAll()`
- `CopySelection()`
- `CopySelectionAsync()`
- `Paste(string)`
- `PasteFromClipboardAsync()`
- `Search(string)`
- `SelectNextSearchResult()`
- `SelectPreviousSearchResult()`
- `ContextRequested`

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

When the terminal application enables mouse reporting, `TerminalControl` forwards pointer press, release, and motion events to `XtermSharp` instead of using them for text selection. This allows interactive terminal applications to receive mouse input.

## Styling

The library includes terminal color resources in:

- [`src/AvaloniaTerminal/Styles/Colors.axaml`](src/AvaloniaTerminal/Styles/Colors.axaml)

The desktop sample includes those resources automatically. If you host the control yourself, include the style resource in your application when needed:

```xml
<Application.Styles>
    <StyleInclude Source="avares://AvaloniaTerminal/Styles/Colors.axaml" />
</Application.Styles>
```

Useful styling hooks on `TerminalControl`:

- `FontFamily`
- `FontSize`
- `CaretBrush`
- `SelectionBrush`

## Samples

The repo includes a shared samples project and a desktop sample host.

Current sample tabs:

- `Shell`: starts a platform-appropriate shell
  - Windows: prefers ConPTY-backed `pwsh.exe`, with redirected fallback
  - macOS/Linux: uses the existing redirected-shell sample backend
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

## Testing

The repo has headless tests covering terminal behavior without needing a visible desktop session.

```bash
dotnet test --project tests/AvaloniaTerminal.Tests/AvaloniaTerminal.Tests.csproj -f net10.0
```
