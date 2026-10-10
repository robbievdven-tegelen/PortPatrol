@echo off
rem Needs the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0).
cd /d "%~dp0src"
echo Framework dependent (small, needs the .NET 8 Desktop Runtime):
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0bin"
echo.
echo Self contained (about 160 MB, no runtime needed):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0bin_selfcontained"
pause
