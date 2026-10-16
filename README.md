<p align="center">
  <img src="assets/loupe.png" width="112" alt="Loupe icon">
</p>

<h1 align="center">Loupe</h1>

<p align="center">
  macOS-style hover zoom for Windows.<br>
  Hold <kbd>Ctrl</kbd> + <kbd>Alt</kbd>, scroll, and a floating lens magnifies whatever is under your pointer.
</p>

<p align="center">
  <a href="https://github.com/sergiiopm/loupe/releases/latest"><img src="https://img.shields.io/github/v/release/sergiiopm/loupe?label=download" alt="Latest release"></a>
  <a href="https://github.com/sergiiopm/loupe/actions/workflows/build.yml"><img src="https://github.com/sergiiopm/loupe/actions/workflows/build.yml/badge.svg" alt="Build status"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6" alt="Windows 10 and 11">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="MIT license"></a>
</p>

---

macOS has a lovely accessibility feature: hold a modifier key, scroll, and the screen zooms in around
the pointer. Loupe brings that gesture to Windows. It's handy for presentations, screen walkthroughs,
reading tiny text or inspecting pixels.

## Features

- **Scroll to zoom.** <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + scroll zooms in and out smoothly, up to 20×.
- **Floating lens.** A rounded window with a soft shadow follows the pointer and magnifies the area
  beneath it. It grows and fades in when it opens and eases away when it closes. Clicks pass
  straight through, so you can keep working while zoomed.
- **Shows up in recordings, if you want.** Turn on *Show lens in recordings* to make the lens
  visible to screenshots, screen recorders (OBS, Loom, Snipping Tool…) and screen sharing.
- **Full-screen mode.** Magnifies the whole primary monitor instead, like macOS full-screen zoom.
- **Resizable.** <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Shift</kbd> + scroll changes the lens size.
- **High-DPI and multi-monitor aware.** The lens works across monitors at any display scaling.
- **Tiny and dependency-free.** A single ~250 KB executable that lives in the system tray. No installer,
  no runtime to download.
- English and Spanish UI, picked automatically from your Windows language.

## Usage

| Action                         | Shortcut                                                     |
| ------------------------------ | ------------------------------------------------------------ |
| Zoom in                        | <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + scroll up                 |
| Zoom out (closes at 1×)        | <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + scroll down               |
| Resize the lens                | <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Shift</kbd> + scroll |
| Close the zoom instantly       | <kbd>Esc</kbd>                                               |

Right-click the tray icon to switch between lens and full-screen mode, pick a lens size, change the
shortcut to plain <kbd>Ctrl</kbd> + scroll, toggle smooth scaling, show the lens in recordings or
start Loupe with Windows.
Settings are stored in `%APPDATA%\Loupe\config.ini`.

## Install

1. Download `Loupe.exe` from the [latest release](https://github.com/sergiiopm/loupe/releases/latest).
2. Run it. A blue magnifier icon appears in the system tray.
3. Optionally, right-click the icon and enable **Start with Windows**.

Windows SmartScreen may warn about an unsigned app the first time; choose **More info → Run anyway**.

## Build from source

Loupe compiles with the C# compiler that ships with Windows (.NET Framework 4.x), so no SDK or Visual
Studio is needed:

```cmd
build.cmd
```

The output is `bin\Loupe.exe`. To regenerate the icon after changing its design:

```powershell
powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
```

## How it works

- **Low-level mouse and keyboard hooks** (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`) watch the wheel and Esc.
  They run on their own thread, because every input event in the system waits for them; that way a
  slow frame can never make the pointer lag. When the shortcut is held the wheel event is consumed and
  becomes a zoom target that the zoom eases towards.
- **Rendering** happens in an idle loop on the UI thread, one frame per display refresh: each frame
  either waits on the compositor itself or calls `DwmFlush`. Animations are time-based, so they run at
  the same speed on 60 Hz and 144 Hz screens.
- The **lens** is a click-through, per-pixel-alpha layered window. Each frame it `StretchBlt`s the
  screen area around the pointer, composites it with a cached shadow and hairline edge, and pushes the
  result with `UpdateLayeredWindow`.
- The lens excludes itself from screen capture (`WDA_EXCLUDEFROMCAPTURE`), which is what lets it copy
  the screen beneath it without magnifying its own reflection.
- With *Show lens in recordings* on, the lens instead hosts the Magnification API's magnifier control,
  which renders the desktop while skipping the lens's own windows (`MagSetWindowFilterList`). That way
  it doesn't need to hide from capture. The shadow is drawn by a separate layered window beneath it.
  Every magnifier call waits for a composition, so this lens only changes the transform when the zoom
  changes, never forces extra repaints, and fades in and out instead of growing.
- **Full-screen mode** uses the Windows Magnification API (`MagSetFullscreenTransform`).

## Limitations

- By default the lens is excluded from capture, so **it doesn't appear in screenshots, screen
  recordings or screen sharing**. Turn on *Show lens in recordings* for that; in that mode the rounded
  corners come from a window region and aren't anti-aliased, and DRM-protected video may show as
  black inside the lens.
- Full-screen mode always shows up in recordings.
- Full-screen mode only magnifies the primary monitor.
- Windows doesn't deliver mouse-wheel events from apps running as administrator to non-elevated
  hooks, so the shortcut won't trigger over those windows unless Loupe also runs as administrator.

## Project layout

```
src/
  Program.cs              entry point, DPI awareness, single instance
  LoupeApp.cs             tray icon, menu, zoom easing and render loop
  InputHooks.cs           mouse and keyboard hooks on their own thread
  ILens.cs                common interface of the two lens variants
  LensWindow.cs           default lens: copies the screen, hidden from capture
  RecordableLensWindow.cs lens built on the magnifier control, visible to capture
  LensChrome.cs           rounded corners, shadow and hairline edge
  LensAnimation.cs        appear / disappear animation
  DibSurface.cs           32-bit drawing surface for layered windows
  Magnification.cs        Magnification API lifetime
  FullscreenMagnifier.cs  full-screen zoom via the Magnification API
  Settings.cs             persisted preferences
  Text.cs                 English / Spanish strings
  Native.cs               Win32 interop
assets/                   app icon
tools/make-icon.ps1       generates the icon from code
build.cmd                 one-line build
```

## License

[MIT](LICENSE)
