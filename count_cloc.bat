@echo off
:: This file must stay CRLF: cmd.exe resolves `goto :label` by byte offset, so LF
:: endings break the labels below. .gitattributes pins *.bat to eol=crlf.
::
:: Count lines of code in the repo this file lives in, wherever it is checked out.
::
::   count_cloc.bat                        count the repo this file lives in
::   count_cloc.bat <dir>                  count another directory
::   set "CLOC_EXE=X:\path\to\cloc.exe"    force a specific cloc
::
:: cloc resolution order: %CLOC_EXE% -> cloc on PATH -> cloc*.exe in a few common
:: tool folders. Install it with:  winget install --id AlDanial.Cloc --exact
chcp 65001 >nul
:: No delayed expansion: nothing uses !var!, and it would eat "!" in paths.
setlocal

set "TARGET_DIR=%~1"
if "%TARGET_DIR%"=="" set "TARGET_DIR=%~dp0."

:: 1. explicit override
if defined CLOC_EXE if exist "%CLOC_EXE%" goto :found

:: 2. on PATH (winget AlDanial.Cloc puts it there). Ask for cloc.exe only: a bare
::    "where cloc" also returns an extensionless script or cloc.cmd (npm install),
::    which cannot be run here, and a .cmd run without CALL would never return.
for /f "delims=" %%I in ('where cloc.exe 2^>nul') do (
    set "CLOC_EXE=%%I"
    goto :found
)

:: 3. common tool folders; the loop keeps the last match, i.e. the highest version name
::    in the last folder that has one
for %%D in ("D:\tools" "C:\tools" "%USERPROFILE%\tools") do (
    for %%F in ("%%~D\cloc*.exe") do set "CLOC_EXE=%%~fF"
)
if defined CLOC_EXE if exist "%CLOC_EXE%" goto :found

echo(
echo   [ERROR] cloc was not found.
echo(
echo   Install it:   winget install --id AlDanial.Cloc --exact
echo   Or point at an existing copy:   set "CLOC_EXE=X:\path\to\cloc.exe"
echo(
exit /b 1

:found
:: The paths stay quoted and out of ( ) blocks, so a folder name with ) or & cannot
:: break the parse.
if exist "%TARGET_DIR%\" goto :have_target
echo [ERROR] target directory not found: "%TARGET_DIR%"
exit /b 1

:have_target
echo [INFO] cloc:   "%CLOC_EXE%"
echo [INFO] target: "%TARGET_DIR%"
echo(

:: Count "." from inside the target so --fullpath paths start at the repo root (cloc
:: uses "/" there even on Windows) and a parent folder named e.g. "build" cannot
:: exclude the whole tree. Excluded names match whole path components only, so
:: .github / .githooks are still counted. build/dist/target/bin/obj are build output
:: (dist also holds copies of scripts\*.lua); vendor is third-party source.
pushd "%TARGET_DIR%"
"%CLOC_EXE%" . ^
    --fullpath ^
    --not-match-d="(^|/)(\.claude|\.git|\.vs|build|dist|target|bin|obj|vendor)(/|$)" ^
    --exclude-lang="JSON,XML"
set "RC=%ERRORLEVEL%"
popd

exit /b %RC%
