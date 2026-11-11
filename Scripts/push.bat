@echo off
setlocal enabledelayedexpansion

echo =====================================================================
echo                V-Eval Service Push Tool
echo =====================================================================
echo:

REM Lay thu muc goc cua Service (la thu muc cha cua Scripts)
pushd "%~dp0.."
set "SERVICE_DIR=%CD%"
popd

pushd "%SERVICE_DIR%"

REM Kiem tra xem thu muc nay co phai repo Git hop le khong
for /f "tokens=*" %%a in ('git rev-parse --show-toplevel 2^>nul') do set "REPO_ROOT=%%a"
if "!REPO_ROOT!"=="" (
    powershell -Command "Write-Host '[LOI] Thu muc nay chua duoc khoi tao Git repository!' -ForegroundColor Red"
    popd
    pause
    exit /b 1
)

for /f "tokens=*" %%b in ('git rev-parse --abbrev-ref HEAD') do set "CURRENT_BRANCH=%%b"

echo [INFO] Service Directory: %CD%
echo [INFO] Nhanh hien tai: !CURRENT_BRANCH!
echo:

REM =====================================================================
REM BUOC 1: CHECK LICH SU & TAP PULL CHU DONG
REM =====================================================================
echo [1/3] Kiem tra dong bo lich su voi Remote...
git fetch origin >nul 2>&1

set "REMOTE_SHA="
for /f "tokens=*" %%a in ('git rev-parse --verify --quiet origin/!CURRENT_BRANCH!') do set "REMOTE_SHA=%%a"

if not "!REMOTE_SHA!"=="" (
    for /f "tokens=*" %%a in ('git rev-parse HEAD') do set "LOCAL_SHA=%%a"
    for /f "tokens=*" %%a in ('git merge-base HEAD origin/!CURRENT_BRANCH!') do set "BASE_SHA=%%a"

    if not "!LOCAL_SHA!"=="!REMOTE_SHA!" (
        if "!LOCAL_SHA!"=="!BASE_SHA!" (
            echo [INFO] Code local bi cham hon remote. Dang tien hanh pull...
            git pull origin !CURRENT_BRANCH!
            if !ERRORLEVEL! neq 0 (
                powershell -Command "Write-Host '[CANH BAO DO] Pull that bai do co Conflict! Vui long kiem tra va merge thu cong truoc khi push!' -ForegroundColor Red"
                popd
                pause
                exit /b 1
            )
        ) else if "!REMOTE_SHA!"=="!BASE_SHA!" (
            echo [INFO] Code local dang nhanh hon remote. San sang push.
        ) else (
            powershell -Command "Write-Host '[CANH BAO DO] Lich su giua Local va Remote bi LECH (Diverged)! Thu tu dong pull...' -ForegroundColor Yellow"
            git pull origin !CURRENT_BRANCH!
            if !ERRORLEVEL! neq 0 (
                powershell -Command "Write-Host '[CANH BAO DO] Khong the tu dong merge! Vui long giai quyet Conflict thu cong truoc khi push!' -ForegroundColor Red"
                popd
                pause
                exit /b 1
            )
        )
    ) else (
        echo [INFO] Lich su local va remote da dong bo.
    )
) else (
    echo [INFO] Nhanh origin/!CURRENT_BRANCH! chua co tren remote. Se tao moi khi push.
)
echo:

REM =====================================================================
REM BUOC 2: LUA CHON NHANH PUSH (Numbered Menu)
REM =====================================================================
echo [2/3] Chon nhanh de push:
echo   1. Push tren nhanh hien tai (!CURRENT_BRANCH!)
echo   2. Chuyen va push sang mot nhanh DA CO (Select tu danh sach)
echo   3. Tao NHANH MOI va push
echo:

set "TARGET_BRANCH=!CURRENT_BRANCH!"
set "BRANCH_OPTION=1"
set /p BRANCH_OPTION="Nhap lua chon cua ban (1, 2, 3): "

if "!BRANCH_OPTION!"=="2" (
    echo:
    echo Danh sach cac nhanh local hien co:
    set count=0
    for /f "tokens=*" %%b in ('git branch --format="%%(refname:short)"') do (
        set /a count+=1
        set "b_!count!=%%b"
        echo   !count!. %%b
    )
    
    echo:
    set "CHOICE_NUM=1"
    set /p CHOICE_NUM="Nhap so thu tu nhanh ban muon chon: "
    
    if defined b_!CHOICE_NUM! (
        set "TARGET_BRANCH=!b_%CHOICE_NUM%!"
        echo [INFO] Da chon nhanh: !TARGET_BRANCH!
        git checkout !TARGET_BRANCH!
    ) else (
        powershell -Command "Write-Host '[LOI] So thu tu khong hop le! Giu nguyen nhanh hien tai.' -ForegroundColor Red"
    )
)

if "!BRANCH_OPTION!"=="3" (
    echo:
    set "NEW_BRANCH="
    set /p NEW_BRANCH="Nhap ten nhanh MOI muon tao (vi du: feature/jwt-transform): "
    if not "!NEW_BRANCH!"=="" (
        set "TARGET_BRANCH=!NEW_BRANCH!"
        echo [INFO] Dang tao va checkout sang nhanh moi: !TARGET_BRANCH!
        git checkout -b !TARGET_BRANCH!
    ) else (
        powershell -Command "Write-Host '[LOI] Ten nhanh khong duoc de rong! Giu nguyen nhanh hien tai.' -ForegroundColor Red"
    )
)

echo:
REM =====================================================================
REM BUOC 3: NHAP COMMIT MESSAGE & PUSH
REM =====================================================================
echo [3/3] Tien hanh Commit va Push len origin/!TARGET_BRANCH!...

set HAS_CHANGES=0
for /f "tokens=*" %%i in ('git status --porcelain') do set HAS_CHANGES=1

if "!HAS_CHANGES!"=="1" (
    git status -s
    echo:
    set "COMMIT_MSG="
    set /p COMMIT_MSG="Nhap Commit Message: "
    if "!COMMIT_MSG!"=="" set "COMMIT_MSG=update code for !TARGET_BRANCH!"
    
    git add -A
    git commit -m "!COMMIT_MSG!"
) else (
    echo [INFO] Khong co thay doi moi o local de commit.
)

echo Dang push len remote origin/!TARGET_BRANCH!...
git push -u origin !TARGET_BRANCH!

if !ERRORLEVEL! equ 0 (
    powershell -Command "Write-Host '[THANH CONG] Da push code len origin/!TARGET_BRANCH! thanh cong!' -ForegroundColor Green"
) else (
    powershell -Command "Write-Host '[THAT BAI] Push code that bai! Vui long kiem tra quyen truy cap hoac conflict.' -ForegroundColor Red"
)

popd
echo:
pause
