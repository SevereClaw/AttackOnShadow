@echo off
rem AttackOnShadow runner: run.cmd install|uninstall|status [path to driver folder or exe]
setlocal EnableExtensions
chcp 65001 >nul
set "ACTION=%~1"
if not defined ACTION set "ACTION=install"
set "EXE="
set "ARG=%~2"
set "TMPFILE=%TEMP%\aos-target.txt"

rem --- elevated re-run passes the path through a temp file ---
if /i "%ARG%"=="--from-temp" (
  set /p EXE=<"%TMPFILE%"
  del "%TMPFILE%" >nul 2>&1
  set "ARG="
)

rem --- 1. path given on the command line / dropped onto the .bat ---
if defined ARG if /i "%~x2"==".exe" if exist "%~f2" set "EXE=%~f2"
if defined ARG if not defined EXE if exist "%~f2\Attack Shark Driver.exe" set "EXE=%~f2\Attack Shark Driver.exe"

rem --- 2. usual install locations ---
if not defined EXE if exist "%LOCALAPPDATA%\Programs\Attack Shark Driver\Attack Shark Driver.exe" set "EXE=%LOCALAPPDATA%\Programs\Attack Shark Driver\Attack Shark Driver.exe"
if not defined EXE if exist "%ProgramFiles%\Attack Shark Driver\Attack Shark Driver.exe" set "EXE=%ProgramFiles%\Attack Shark Driver\Attack Shark Driver.exe"
if not defined EXE if exist "%ProgramFiles(x86)%\Attack Shark Driver\Attack Shark Driver.exe" set "EXE=%ProgramFiles(x86)%\Attack Shark Driver\Attack Shark Driver.exe"

rem --- 3. Windows "installed programs" registry ---
if not defined EXE (
  for /f "usebackq delims=" %%P in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$k='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'; Get-ItemProperty $k -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Attack Shark*' } | ForEach-Object { if ($_.InstallLocation) { $_.InstallLocation } elseif ($_.DisplayIcon) { Split-Path (($_.DisplayIcon -replace ',\d+$','') -replace [char]34,'') } } | Select-Object -First 1"`) do (
    if exist "%%~P\Attack Shark Driver.exe" set "EXE=%%~P\Attack Shark Driver.exe"
  )
)

rem --- 4. ask ---
if not defined EXE (
  echo Could not find Attack Shark Driver automatically.
  echo Drag "Attack Shark Driver.exe" into this window and press Enter:
  set /p "EXE=> "
)
set "EXE=%EXE:"=%"
if not exist "%EXE%" (
  echo Not found: "%EXE%"
  goto :end
)
for %%F in ("%EXE%") do set "DIR=%%~dpF"
set "DIR=%DIR:~0,-1%"
echo Driver: "%DIR%"

rem --- need admin? (Program Files) ---
rem (no parenthesised blocks here: paths like "Program Files (x86)" would break them)
set "PROBE=%DIR%\resources\app\.aos-write-test"
type nul >"%PROBE%" 2>nul
if exist "%PROBE%" goto :rights_ok
echo Administrator rights are needed for this folder, asking Windows...
>"%TMPFILE%" echo %EXE%
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -ArgumentList '%ACTION%','--from-temp' -Verb RunAs"
exit /b
:rights_ok
del "%PROBE%" >nul 2>&1

rem --- close the driver so its files are not in use ---
taskkill /f /im "Attack Shark Driver.exe" >nul 2>&1
timeout /t 1 /nobreak >nul

rem --- run the patcher inside the driver's own Electron (Node mode) ---
set "LOG=%TEMP%\aos-patch.log"
set "ELECTRON_RUN_AS_NODE=1"
"%EXE%" "%~dp0patch.js" %ACTION% "%DIR%" >"%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
set "ELECTRON_RUN_AS_NODE="
type "%LOG%"
echo.
if not "%RC%"=="0" (
  echo FAILED, exit code %RC%. Log: "%LOG%"
) else (
  if /i "%ACTION%"=="install" echo Done. Start Attack Shark Driver as usual.
  if /i "%ACTION%"=="uninstall" echo Done. The original driver is back.
)

:end
echo.
pause
