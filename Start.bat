@echo off
setlocal EnableExtensions

cd /d "%~dp0"

set "PROJECT=src\DoraExplorer.App\DoraExplorer.App.csproj"
set "STAGING_DIR=%~dp0build\staging"
set "COMPILE_DIR=%~dp0build\compile"
set "LOGS_DIR=%~dp0logs"
set "APP_EXE=%COMPILE_DIR%\DoraExplorer.App.exe"

rem === STOP: encerrar instancias anteriores antes de compilar/iniciar ===
echo Encerrando instancias anteriores do Dora Explorer...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\stop-dora.ps1"
rem Codigo 2: o app continua aberto e este terminal nao consegue fecha-lo (por exemplo, esta como administrador).
if "%errorlevel%"=="2" (
    echo.
    echo [ERRO] Compilacao cancelada: feche o Dora Explorer e rode o Start.bat de novo.
    pause
    exit /b 1
)

ping 127.0.0.1 -n 2 >nul

rem === START: localizar o .NET SDK ===
set "DOTNET="
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
if not defined DOTNET (
    for /f "delims=" %%I in ('where dotnet 2^>nul') do if not defined DOTNET set "DOTNET=%%I"
)

if not defined DOTNET (
    echo [ERRO] dotnet.exe nao encontrado.
    echo Instale o .NET 8 SDK em: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

rem Ter dotnet.exe nao basta: so o runtime instalado nao compila. E preciso um SDK 8 ou superior.
set "SDK_LIST=%TEMP%\dora-sdks-%RANDOM%.txt"
"%DOTNET%" --list-sdks >"%SDK_LIST%" 2>nul
findstr /r "^[89]\. ^[1-9][0-9]\." "%SDK_LIST%" >nul
set "SDK_STATUS=%errorlevel%"
del "%SDK_LIST%" >nul 2>&1

if not "%SDK_STATUS%"=="0" (
    echo.
    echo [ERRO] Nenhum .NET SDK 8 ou superior foi encontrado.
    echo Apenas o runtime esta instalado, e ele nao consegue compilar o projeto.
    echo Instale o .NET 8 SDK em: https://dotnet.microsoft.com/download/dotnet/8.0
    echo Ou, no terminal: winget install Microsoft.DotNet.SDK.8
    pause
    exit /b 1
)

if not exist "%STAGING_DIR%" mkdir "%STAGING_DIR%"
if not exist "%COMPILE_DIR%" mkdir "%COMPILE_DIR%"
if not exist "%LOGS_DIR%" mkdir "%LOGS_DIR%"

rem === MAP: atualizar project.md e project-metodos.md para os agentes ===
echo Atualizando project.md e project-metodos.md...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\generate-project-map.ps1"
if not "%errorlevel%"=="0" (
    echo [AVISO] Falha ao gerar project.md e project-metodos.md - continuando compilacao.
)

rem Remove o executavel anterior: se a compilacao falhar, nunca se inicia uma versao antiga.
if exist "%APP_EXE%" del /q "%APP_EXE%" >nul 2>&1

echo Compilando Dora Explorer...
"%DOTNET%" build "%PROJECT%" -c Release --nologo -o "%COMPILE_DIR%"
rem Atencao: "if errorlevel 1" nao detecta codigos negativos, como o 0x8000809B do dotnet sem SDK.
if not "%errorlevel%"=="0" (
    echo.
    echo [ERRO] Falha na compilacao.
    pause
    exit /b 1
)

if not exist "%APP_EXE%" (
    echo.
    echo [ERRO] Executavel nao encontrado: build\compile\DoraExplorer.App.exe
    pause
    exit /b 1
)

echo Publicando em build\staging...
robocopy "%COMPILE_DIR%" "%STAGING_DIR%" /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np >nul
rem Robocopy usa codigos 0-7 para sucesso; 8 ou mais indica falha.
if %errorlevel% geq 8 (
    echo [AVISO] Nao foi possivel atualizar build\staging ^(ficheiros em uso^).
)

echo Iniciando Dora Explorer...
start "" "%APP_EXE%"

rem Confirma que o processo continua vivo: se encerrar logo, a causa esta no log mais recente.
ping 127.0.0.1 -n 4 >nul
tasklist /fi "imagename eq DoraExplorer.App.exe" 2>nul | find /i "DoraExplorer.App.exe" >nul
if errorlevel 1 (
    echo.
    echo [AVISO] O Dora Explorer encerrou logo apos iniciar.
    echo Consulte o log mais recente na pasta logs\ do projeto.
    pause
    exit /b 1
)

exit /b 0
