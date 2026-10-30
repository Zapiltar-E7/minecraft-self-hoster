@echo off
rem Rebuilds "Minecraft Host.exe" (needs the .NET 8 SDK). Output goes next to the server folder.
cd /d "%~dp0MinecraftHost"
dotnet publish -c Release -o "%~dp0publish" || goto :error
copy /y "%~dp0publish\MinecraftHost.exe" "%~dp0..\Minecraft Host.exe" >nul || goto :error
echo Built: %~dp0..\Minecraft Host.exe
pause
exit /b 0
:error
echo Build failed.
pause
exit /b 1
