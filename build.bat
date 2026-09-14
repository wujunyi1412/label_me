@echo off
setlocal EnableExtensions
chcp 65001 >nul
set "PROJECT_ROOT=%~dp0"
set "PROJECT_ROOT=%PROJECT_ROOT:~0,-1%"
set "BUILD_DIR=%PROJECT_ROOT%\build\cmake"
set "BUILD_CONFIG=%~1"
if "%BUILD_CONFIG%"=="" set "BUILD_CONFIG=Release"

if /I not "%BUILD_CONFIG%"=="Release" if /I not "%BUILD_CONFIG%"=="Debug" (
    echo [ERROR] Configuration must be Release or Debug.
    echo Usage: build.bat [Release^|Debug]
    exit /b 2
)

where cmake >nul 2>nul
if errorlevel 1 (
    echo [ERROR] CMake was not found in PATH.
    exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet was not found in PATH. Install the .NET 9 SDK.
    exit /b 1
)

echo [1/2] Configuring CMake [%BUILD_CONFIG%]...
cmake -S "%PROJECT_ROOT%" -B "%BUILD_DIR%" -DDOTNET_CONFIGURATION=%BUILD_CONFIG%
if errorlevel 1 goto :failed

echo [2/2] Building and publishing...
cmake --build "%BUILD_DIR%" --config %BUILD_CONFIG% --target LabelMeWpf
if errorlevel 1 goto :failed

echo.
echo Build succeeded: %PROJECT_ROOT%\dist\%BUILD_CONFIG%\LabelMeWpf.exe
exit /b 0

:failed
echo.
echo [ERROR] Build failed with exit code %errorlevel%.
exit /b %errorlevel%
