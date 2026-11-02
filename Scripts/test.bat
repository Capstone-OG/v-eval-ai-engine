@echo off
setlocal enabledelayedexpansion

REM =====================================================================
REM   V-Eval - Microservice Automated Unit Test & Coverage Runner
REM   Service: V-Eval-Ai_Engine
REM =====================================================================

pushd "%~dp0.."
set "SERVICE_DIR=%CD%"
set "SERVICE_NAME=V-Eval-Ai_Engine"
set "TEST_PROJECT=%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\V-Eval-Ai_Engine.Tests.csproj"

powershell -Command "Write-Host '=====================================================' -ForegroundColor DarkCyan"
powershell -Command "Write-Host '  Running Unit Tests for %SERVICE_NAME%' -ForegroundColor Cyan"
powershell -Command "Write-Host '=====================================================' -ForegroundColor DarkCyan"
echo.

REM 1. Phat hien .NET 9 SDK Binary
if exist "%USERPROFILE%\.dotnet\dotnet.exe" (
    set "DOTNET_CMD=%USERPROFILE%\.dotnet\dotnet.exe"
) else (
    set "DOTNET_CMD=dotnet"
)

REM 2. Clean & Build
powershell -Command "Write-Host '[1/4] Bien dich du an Test...' -ForegroundColor DarkCyan"
"%DOTNET_CMD%" build "%TEST_PROJECT%" --configuration Debug >nul 2>&1
if %ERRORLEVEL% neq 0 (
    powershell -Command "Write-Host '[ERROR] Build that bai. Dang chay lai de hien thi loi chi tiet...' -ForegroundColor Red"
    "%DOTNET_CMD%" build "%TEST_PROJECT%" --configuration Debug
    popd
    exit /b 1
)

REM 3. Chuan bi thu muc ket qua
if not exist "%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults\coverage" mkdir "%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults\coverage"
if not exist "%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults\CoverageReport" mkdir "%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults\CoverageReport"

REM 4. Chay Tests kem thu thap Code Coverage
echo.
powershell -Command "Write-Host '[2/4] Dang thuc thi Unit Tests va do luong Code Coverage...' -ForegroundColor DarkCyan"
"%DOTNET_CMD%" test "%TEST_PROJECT%" ^
  --collect:"XPlat Code Coverage" ^
  --results-directory "%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults" ^
  /p:CollectCoverage=true ^
  /p:CoverletOutput="%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults\coverage\" ^
  /p:CoverletOutputFormat=cobertura ^
  /p:Include="[V_Eval_Ai_Engine*]*" ^
  --no-build ^
  --configuration Debug ^
  -v n

set TEST_EXIT_CODE=%ERRORLEVEL%

echo.
powershell -Command "Write-Host '[3/4] Kiem tra ket qua thu thap Cobertura Coverage...' -ForegroundColor DarkCyan"

REM 5. Khoi phuc cong cu ReportGenerator
"%DOTNET_CMD%" tool restore >nul 2>&1

REM 6. Sinh bao cao HTML tu file Cobertura
powershell -Command "Write-Host '[4/4] Dang sinh bao cao HTML Code Coverage...' -ForegroundColor DarkCyan"
"%DOTNET_CMD%" tool run reportgenerator ^
  "-reports:%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults\**\coverage.cobertura.xml" ^
  "-targetdir:%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults\CoverageReport" ^
  "-reporttypes:Html" >nul 2>&1

set "HTML_REPORT=%SERVICE_DIR%\V-Eval-Ai_Engine.Tests\TestResults\CoverageReport\index.html"

echo.
if exist "%HTML_REPORT%" (
    powershell -Command "Write-Host '  [THANH CONG] Bao cao HTML Coverage da duoc tao!' -ForegroundColor Green"
    powershell -Command "Write-Host '  Duong dan: %HTML_REPORT%' -ForegroundColor DarkGreen"
    if "%CI%"=="" start "" "%HTML_REPORT%"
) else (
    powershell -Command "Write-Host '  [THONG BAO] Khong tim thay file bao cao HTML.' -ForegroundColor Yellow"
)

echo.
if %TEST_EXIT_CODE% neq 0 (
    powershell -Command "Write-Host '=== MOT SO TEST BI THAT BAI (Exit Code: %TEST_EXIT_CODE%) ===' -ForegroundColor Yellow"
) else (
    powershell -Command "Write-Host '=== TAT CA TEST DEU PASS 100%% ===' -ForegroundColor Green"
)

popd
exit /b %TEST_EXIT_CODE%
