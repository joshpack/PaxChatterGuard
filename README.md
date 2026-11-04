# PAX Chatter Guard

A tiny tray app that stops mechanical-keyboard chatter: one key press that comes out as two letters.

When a key is pressed again within a few milliseconds of being released, that second press is almost always the switch bouncing, not you. PAX Chatter Guard drops it before it reaches any program.

## Features

- **One delay for everything.** The default is 30 ms, and you can change it in the settings window.
- **Per-key exceptions** for the keys that chatter worst, or for keys you double-tap quickly on purpose.
- **Per-keyboard settings.** Each keyboard model it sees can have its own delay and exceptions; the rest follow the defaults.
- **Auto-adjust (optional).** It learns from chatter that slips through and raises that key's delay just above it, never past a limit you set (45 ms by default), so fast typing stays safe.
- **Hotkey** Ctrl+Alt+Shift+F9 turns filtering on and off.
- **Light on resources.** About 13 MB of RAM in the tray and no CPU while idle. The settings window is only created when you open it.
- **Start with the system**, optional.

## Platforms

| Platform | How it filters | Status |
| --- | --- | --- |
| Windows | Low-level keyboard hook (`WH_KEYBOARD_LL`) with a watchdog | Tested |
| Linux | evdev grab + uinput virtual keyboard | Builds, untested |
| macOS | `CGEventTap` | Builds, untested |

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:DebugType=none -o publish
```

Settings are stored in `%APPDATA%\PaxChatterGuard\settings.json` (Windows) or the equivalent config folder on other systems.

## Code

| Folder | What's there |
| --- | --- |
| `Core/` | `ChatterEngine`: the filtering rules, auto-adjust and the toggle hotkey |
| `Platform/` | One keyboard backend per OS behind `IKeyboardBackend` |
| `Services/` | Settings, autostart, the service that ties the engine to a backend |
| `Views/` | The settings window |

Built with [Avalonia](https://avaloniaui.net/) 12 on .NET 10.
