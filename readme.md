# Generic Read/Write Program for YModem Written in "C#"

## Features: 
1. Fully compatible with Secure CRT 1K send/receive mode. 
2. Supports 1k sending method. 
3. Supports receiving in 128-byte and 1K-byte blocks. 
4. Supports reading and writing multiple files.
5. Fully open-source under the MIT license. 

![](intro.gif)


Attachments: YMODEM协议参考中文译制版
XMODEM-YMODEM-Protocol-Reference_881014


## Windows build and debug (non-MSIX)

The WinUI project now uses an unpackaged deployment model (`WindowsPackageType=None`) to make local debugging easier in JetBrains Rider and avoid MSIX certificate/signing requirements.

### Run from Rider
1. Open `YModemWin/YModemWin.sln` in Rider.
2. Set `YModemWin.App` as startup project with `Debug | x64`.
3. Run/Debug directly; the app starts as a normal desktop process instead of an MSIX-installed app.

### Publish an unpackaged build
```bash
dotnet publish YModemWin/YModemWin.App/YModemWin.App.csproj -c Release -r win-x64 --self-contained false
```

Publish output is produced under `YModemWin/YModemWin.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`.


### Troubleshooting
If you hit `MrtCore.PriGen.targets` errors in Rider or `dotnet build` (for example missing `Microsoft.Build.Packaging.Pri.Tasks.dll` or `Microsoft.Build.AppxPackage.dll`), this project disables both PRI and Appx/MSIX packaging targets for unpackaged builds (`GenerateProjectPriFile=false`, `AppxGeneratePriEnabled=false`, `GenerateAppxPackageOnBuild=false`, `AppxPackage=false`).

If your local `obj` cache was created before this change, clean and rebuild:
```bash
dotnet clean YModemWin/YModemWin.App/YModemWin.App.csproj -c Debug
dotnet build YModemWin/YModemWin.App/YModemWin.App.csproj -c Debug
```

If Rider still triggers deploy packaging steps, disable "Deploy" in the run configuration for local debug.
