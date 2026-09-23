# No longer maintained, see https://github.com/MLG-SERBUR/AutoDefaultToHeadset-NativeAOT instead

---

# Auto Default To Headset

Lightweight Windows utility that sets matching headset output and input devices as default when they appear.

It sets every Core Audio role for each matched endpoint:

- console
- multimedia
- communications

Default behavior:

- output match: active device name contains `headset` or `headphones`
- input match: active device name contains `headset`

Exact endpoint ids are supported when name matching is too broad.

## Requirements

- Windows 10 build 19041 or later, or Windows 11
- .NET 8 SDK or newer for build

Relevant files:

- Project file: [AutoDefaultToHeadset.csproj](AutoDefaultToHeadset.csproj)
- Main source: [Program.cs](Program.cs)

## Build

From the repo root:

```cmd
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Published output:

```text
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

## Pick device match

List endpoints:

```cmd
AutoDefaultToHeadset.exe --list-devices
```

Most reliable order:

1. Use exact endpoint ids if they stay stable after disconnect/reconnect.
2. Use friendly-name substrings if ids change.

Exact ids:

```cmd
AutoDefaultToHeadset.exe --render-id "{output endpoint id}" --capture-id "{input endpoint id}"
```

Name match:

```cmd
AutoDefaultToHeadset.exe --render-match "Arctis" --capture-match "Arctis"
```

Same text for both:

```cmd
AutoDefaultToHeadset.exe --match "Headset"
```

## Run

Foreground:

```cmd
AutoDefaultToHeadset.exe
```

Hidden:

```cmd
AutoDefaultToHeadset.exe --background
```

Replace an already-running instance, then continue with this launch:

```cmd
AutoDefaultToHeadset.exe --background
```

Exit immediately instead of replacing a running instance:

```cmd
AutoDefaultToHeadset.exe --background --exit-if-running
```

## Startup

Run `install.bat` from the published folder. It opens a selector, asks for output and input devices, creates a Startup shortcut with matching arguments, then launches the switcher.

Direct installer mode:

```cmd
AutoDefaultToHeadset.exe --install
```

New launches replace an already-running copy.

Manual Startup shortcut:

```powershell
$exe = "C:\full\path\to\AutoDefaultToHeadset.exe"
$args = "--background"
$startup = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\AutoDefaultToHeadset.lnk"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($startup)
$sc.TargetPath = $exe
$sc.Arguments = $args
$sc.WorkingDirectory = Split-Path $exe
$sc.Save()
```

Task Scheduler:

```cmd
schtasks /Create /SC ONLOGON /TN "AutoDefaultToHeadset" /TR "\"C:\full\path\AutoDefaultToHeadset.exe\" --background" /RL HIGHEST /F
```

## Notes

- Uses Windows Core Audio COM directly. No NuGet packages.
- Scans once on startup, then reacts to endpoint add/state/property events.
- Keeps one active instance.
- Console logging only; no log file.
