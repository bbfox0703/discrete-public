@echo off
setlocal EnableDelayedExpansion

:: ============================================================
:: build.cmd -- UnityCEDumper build wrapper
::
:: Mirrors UE5CEDumper's build.cmd. Friendly argument parsing,
:: routes to build.ps1 with the resolved flags. All output lands
:: in dist\ regardless of mode.
::
:: Usage:
::   build              Build Release (all targets)
::   build debug        Build Debug
::   build release      Build Release
::   build publish      Build Publish (Native AOT single-file)
::   build clean        Wipe dist/ + build/ before building
::   build dll          Build DLLs only (both backends)
::   build mono         Build Mono DLL only
::   build il2cpp       Build IL2CPP DLL only
::   build ui           Build UI only
::   build test         Build + run xUnit v3 tests
::   build proxy        Build Proxy DLLs (Phase 12 placeholder)
::   build publish clean   Publish, clean first
::   build /?           Show full usage
:: ============================================================

set "MODE=Release"
set "TARGET=All"
set "BACKEND=Both"
set "CLEAN="
set "HAS_ARGS=0"

:parse_args
if "%~1"=="" goto :run
set "HAS_ARGS=1"

set "ARG=%~1"
:: Case-insensitive matching
for %%A in ("%ARG%") do set "UPPER=%%~A"
call :to_upper UPPER

if "!UPPER!"=="DEBUG"   ( set "MODE=Debug"     & shift & goto :parse_args )
if "!UPPER!"=="RELEASE" ( set "MODE=Release"   & shift & goto :parse_args )
if "!UPPER!"=="PUBLISH" ( set "MODE=Publish"   & shift & goto :parse_args )
if "!UPPER!"=="CLEAN"   ( set "CLEAN=-Clean"   & shift & goto :parse_args )
if "!UPPER!"=="DLL"     ( set "TARGET=DLL"     & shift & goto :parse_args )
if "!UPPER!"=="MONO"    ( set "TARGET=DLL"     & set "BACKEND=Mono"   & shift & goto :parse_args )
if "!UPPER!"=="IL2CPP"  ( set "TARGET=DLL"     & set "BACKEND=IL2CPP" & shift & goto :parse_args )
if "!UPPER!"=="UI"      ( set "TARGET=UI"      & shift & goto :parse_args )
if "!UPPER!"=="TEST"    ( set "TARGET=Test"    & shift & goto :parse_args )
if "!UPPER!"=="PROXY"   ( set "TARGET=ProxyDLL" & shift & goto :parse_args )
if "!UPPER!"=="ALL"     ( set "TARGET=All"     & shift & goto :parse_args )
if "!UPPER!"=="/?"      goto :usage
if "!UPPER!"=="-H"      goto :usage
if "!UPPER!"=="--HELP"  goto :usage

echo.
echo  ERROR: Unknown argument '%~1'
goto :usage_error

:run
set "LOG=%~dp0build_log.txt"

echo.
echo  UnityCEDumper Build
echo  Mode: %MODE%  Target: %TARGET%  Backend: %BACKEND%  Clean: %CLEAN%
echo  Log:  %LOG%

if "!HAS_ARGS!"=="0" (
    echo  Hint: No arguments -- using defaults. Available options:
    echo    build debug          Debug build
    echo    build dll            DLLs only (both backends)
    echo    build mono           Mono DLL only
    echo    build il2cpp         IL2CPP DLL only
    echo    build ui             UI only
    echo    build test           Run tests
    echo    build publish        Native AOT publish
    echo    build clean          Wipe build/ + dist/ first
    echo    build --help         Full usage
)
echo.

:: -File avoids encoding issues at the cmd.exe -^> PowerShell pipe boundary.
:: build.ps1 owns the transcript via Start-Transcript so CJK output stays intact.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Mode %MODE% -Target %TARGET% -Backend %BACKEND% %CLEAN% -LogFile "%LOG%"
set "EC=%ERRORLEVEL%"

if %EC% neq 0 (
    echo.
    echo  BUILD FAILED [exit code %EC%] -- see %LOG%
    echo.
) else (
    echo.
    echo  BUILD SUCCEEDED -- log: %LOG%
    echo.
)

exit /b %EC%

:usage_error
call :print_usage
exit /b 1

:usage
call :print_usage
exit /b 0

:print_usage
echo.
echo  Usage: build [mode] [target] [options]
echo.
echo  Modes:
echo    debug       Unoptimized, debug symbols (fast iteration)
echo    release     Optimized build (default)
echo    publish     Optimized Native AOT single-file exe (distribution)
echo.
echo  Targets:
echo    all         Build everything (default)
echo    dll         C++ DLLs (both Mono + IL2CPP)
echo    mono        Mono DLL only
echo    il2cpp      IL2CPP DLL only
echo    ui          C# Avalonia UI only
echo    test        Build + run xUnit tests
echo    proxy       Proxy DLLs (Phase 12 placeholder)
echo.
echo  Options:
echo    clean       Wipe build/ + dist/ first
echo.
echo  Examples:
echo    build                   Release build, all targets
echo    build debug             Debug build
echo    build publish clean     Clean + AOT publish
echo    build mono              Mono DLL only
echo    build test              Run tests
echo.
echo  All output lands in dist\ alongside build_number.txt.
echo.
goto :eof

:to_upper
for %%a in (A B C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (
    set "%1=!%1:%%a=%%a!"
)
goto :eof
