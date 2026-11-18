# Floatboard

Floatboard is an always-on-top clipboard and system monitor for Windows 10 and 11.

## Download and run

Open **Releases** in this repository and download the ZIP for your processor.
Most Intel and AMD laptops use **x64**. Windows-on-Arm laptops use **arm64**.
Extract the ZIP and run `Floatboard.exe`. The release includes the .NET runtime.
The executable is unsigned, so Windows may show a reputation prompt.

## Use Floatboard

- Copy something in a browser, Word, File Explorer, or another app. Floatboard automatically saves supported clipboard items while it runs.
- Select a history item and click **Copy**, double-click it, or press Enter. Switch to the destination app and press **Ctrl+V**.
- Select **Pin** to keep an item above recent entries. Select **Delete** to remove it from history.
- Drag the title bar or resize the panel from its edges. **Settings** lets you change theme and opacity or hide the panel to the system tray. Double-click its tray icon to show it again.
- Open **Usage & weather** for CPU, RAM, GPU, and live network download/upload rates. The menu can enable a Windows notification when CPU, RAM, or GPU hits 90%. GPU activity depends on Windows driver counter support. Network rates report current traffic across active interfaces.
- Choose **Settings → Set weather city** to display current conditions in Celsius. Open-Meteo receives the city query and location coordinates. Weather refreshes approximately every 15 minutes.

## Clipboard history

Floatboard keeps up to 200 entries across restarts in `%LOCALAPPDATA%\Floatboard\history.json`. The oldest unpinned entry is replaced when the history is full. Clipboard history is stored locally as readable JSON. Use **Pause automatic capture** before copying sensitive material. Deleting in Floatboard removes the saved entry. Windows and other applications manage their own clipboard history.

Supported formats include Unicode text, web HTML, RTF, standard Windows bitmap images, and copied file paths. Paste formatting depends on the destination app. File entries refer to the original files, which must still exist. Some apps use private formats that Floatboard cannot restore. Each entry has a 1 MB history limit.

## Build from source

Install the .NET 8 SDK, then run this command from the project directory.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Use `win-arm64` for Windows on Arm. The GitHub Actions workflow also builds both architectures and uploads ZIP artifacts for signed-in collaborators.
