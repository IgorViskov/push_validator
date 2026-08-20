@echo off
rem Entry point for Windows: works from cmd and PowerShell with no setup.
rem
rem A .cmd file is deliberate. It is not subject to ExecutionPolicy, so it needs neither
rem a flag on every run nor a change to system security settings, and it does not depend
rem on sh. Internally it runs the same .ps1 scripts with -ExecutionPolicy Bypass: the
rem system policy stays untouched, the relaxation lasts one invocation.
rem
rem This file is intentionally ASCII-only. The cmd console runs an OEM codepage (866 on
rem Russian Windows), and UTF-8 Cyrillic echoed from here would come out as garbage.
rem All human-facing text is printed by the .ps1 scripts, which handle encoding correctly.
rem
rem   demo run [dir]            guided walkthrough: does everything, you press Enter
rem   demo bootstrap [dir]      prepare the demo repository
rem   demo 01 <repo>            broken public contract
rem   demo 02 <repo>            discount cap removed
rem   demo 03 <repo>            prompt injection in a comment
rem   demo reset <repo>         roll back to the last push

setlocal
set "HERE=%~dp0"
set "PS=powershell -NoProfile -ExecutionPolicy Bypass"

if "%~1"=="" goto :usage

if /i "%~1"=="run" (
    if "%~2"=="" ( %PS% -File "%HERE%demo-run.ps1" ) else ( %PS% -File "%HERE%demo-run.ps1" -Workspace "%~2" )
    goto :done
)

if /i "%~1"=="bootstrap" (
    if "%~2"=="" ( %PS% -File "%HERE%bootstrap.ps1" ) else ( %PS% -File "%HERE%bootstrap.ps1" -Target "%~2" )
    goto :done
)

if /i "%~1"=="reset" (
    if "%~2"=="" goto :usage
    %PS% -File "%HERE%reset.ps1" -Repo "%~2"
    goto :done
)

set "SCRIPT="
if "%~1"=="01" set "SCRIPT=change-01-breaking-signature.ps1"
if "%~1"=="02" set "SCRIPT=change-02-discount-cap.ps1"
if "%~1"=="03" set "SCRIPT=change-03-injection.ps1"
if not defined SCRIPT goto :usage
if "%~2"=="" goto :usage

%PS% -File "%HERE%%SCRIPT%" -Repo "%~2"
goto :done

:usage
rem Usage text is printed by PowerShell: its output is UTF-8, the cmd console is OEM.
%PS% -File "%HERE%usage.ps1"
endlocal
exit /b 2

:done
endlocal & exit /b %ERRORLEVEL%
