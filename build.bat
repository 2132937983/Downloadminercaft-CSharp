@echo off
rem ============================================================================
rem  MC Downloader - build script
rem
rem  Uses only the C# compiler that ships with Windows 10/11
rem  (.NET Framework 4.x: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe).
rem  No .NET SDK, MSBuild or Visual Studio is required.
rem
rem  Usage:
rem    build.bat          build everything (same as "build.bat all")
rem    build.bat gui      build only bin\MCDownloader.exe        (WPF app)
rem    build.bat shell    build only bin\MCDownloader.Shell.exe  (command line)
rem    build.bat clean    delete the bin\ folder
rem
rem  Every build first removes the outputs it is about to produce, plus files
rem  left over from older versions of this project.
rem ============================================================================
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "OUT=bin"
set "GUI_EXE=%OUT%\MCDownloader.exe"
set "SHELL_EXE=%OUT%\MCDownloader.Shell.exe"
rem Outputs of earlier versions (WinForms GUIs) that no longer exist
set "LEGACY=MCDownloader.Gui.exe MCDownloader.Styled.exe"

set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=all"
if /i "%TARGET%"=="all" goto :target_ok
if /i "%TARGET%"=="gui" goto :target_ok
if /i "%TARGET%"=="shell" goto :target_ok
if /i "%TARGET%"=="clean" goto :target_ok
echo Unknown target "%TARGET%".
echo Usage: build.bat [all ^| gui ^| shell ^| clean]
exit /b 2
:target_ok

rem ---------------------------------------------------------------- clean only
if /i "%TARGET%"=="clean" (
    call :ensure_not_running MCDownloader.exe || goto :abort
    call :ensure_not_running MCDownloader.Shell.exe || goto :abort
    if exist "%OUT%" rmdir /s /q "%OUT%"
    if exist "%OUT%" (
        echo [ERROR] Could not delete %OUT%\ - a file in it is still in use.
        goto :abort
    )
    echo Deleted %OUT%\
    goto :eof
)

rem ---------------------------------------------------------- locate compiler
set "FX=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FX%\csc.exe" set "FX=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FX%\csc.exe" (
    echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
    goto :abort
)
if not exist "%FX%\WPF\PresentationFramework.dll" (
    echo [ERROR] WPF assemblies not found in "%FX%\WPF".
    goto :abort
)
set "CSC=%FX%\csc.exe"

rem ---------------------------------------------------------- remove old files
rem Check every program we are about to replace before deleting anything
if /i not "%TARGET%"=="shell" call :ensure_not_running MCDownloader.exe || goto :abort
if /i not "%TARGET%"=="gui" call :ensure_not_running MCDownloader.Shell.exe || goto :abort
if not exist "%OUT%" mkdir "%OUT%"
for %%f in (%LEGACY%) do call :remove "%OUT%\%%f" || goto :abort
if /i not "%TARGET%"=="shell" call :remove "%GUI_EXE%" || goto :abort
if /i not "%TARGET%"=="gui" call :remove "%SHELL_EXE%" || goto :abort

rem ------------------------------------------------------------ common options
set "FLAGS=/nologo /noconfig /codepage:65001 /optimize+ /platform:anycpu /warn:4"
set "REFS=/r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll"
set "ICON="
if exist "assets\app.ico" set "ICON=/win32icon:assets\app.ico"

rem ------------------------------------------------------------------ WPF app
if /i "%TARGET%"=="shell" goto :build_shell
echo Building %GUI_EXE% ...
set "WPF=/lib:"%FX%\WPF" /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.Xml.dll"
rem Every src\App\*.xaml is embedded as "McDownloader.<file name>" and loaded at runtime by XamlReader
set "XAML="
for %%f in (src\App\*.xaml) do set "XAML=!XAML! /resource:%%f,McDownloader.%%~nxf"
"%CSC%" %FLAGS% %REFS% %WPF% %ICON% /target:winexe /out:"%GUI_EXE%" !XAML! ^
    src\Common\McCore.cs src\Common\GuiCore.cs src\App\*.cs || goto :fail

rem -------------------------------------------------------------- command line
:build_shell
if /i "%TARGET%"=="gui" goto :done
echo Building %SHELL_EXE% ...
"%CSC%" %FLAGS% %REFS% %ICON% /target:exe /out:"%SHELL_EXE%" ^
    src\Common\McCore.cs src\Shell\ShellProgram.cs || goto :fail

:done
echo.
echo Build succeeded:
for %%f in ("%OUT%\*.exe") do echo   %%~nxf  (%%~zf bytes)
exit /b 0

:fail
echo.
echo [ERROR] Build failed.
exit /b 1

:abort
rem Single exit point for errors: "exit /b" inside ( ) blocks or after || can lose its
rem exit code when the script is started with "cmd /c".
exit /b 1

rem ================================================================ helpers

rem Fails if a process with the given image name is running (its exe would be locked)
:ensure_not_running
rem Full paths: a Unix "find" (e.g. from Git for Windows) may come first on PATH
"%SystemRoot%\System32\tasklist.exe" /fi "imagename eq %~1" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "%~1" >nul
if not errorlevel 1 (
    echo [ERROR] %~1 is running. Close it and run the build again.
    exit /b 1
)
exit /b 0

rem Deletes a file if it exists; fails if it cannot be deleted
:remove
if not exist "%~1" exit /b 0
del /f /q "%~1" 2>nul
if exist "%~1" (
    echo [ERROR] Could not delete %~1 - is it still in use?
    exit /b 1
)
echo Removed old %~1
exit /b 0
