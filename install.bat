@echo off
setlocal
REM install.bat - run interactive device selector, create Startup shortcut, launch switcher.
REM Usage: double-click this file, or run: install.bat [MyApp.exe]

if "%~1"=="" (
  for %%F in (*.exe) do (
    set "FOUND_EXE=%%~fF"
    goto :found
  )
  echo No .exe found in "%CD%".
  echo Place the executable here or run: install.bat MyApp.exe
  pause
  exit /b 1
) else (
  if exist "%~1" (
    for %%I in ("%~1") do set "FOUND_EXE=%%~fI"
  ) else (
    echo Specified exe "%~1" not found.
    pause
    exit /b 1
  )
)

:found
echo Using "%FOUND_EXE%"
"%FOUND_EXE%" --install

if %ERRORLEVEL% EQU 0 (
  echo Installer completed.
) else (
  echo Installer failed.
)
pause
