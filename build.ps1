<#
    Compila o Claude Indicator e (se o Inno Setup estiver instalado) gera o instalador.

    Uso:
      .\build.ps1                  # exe único self-contained + instalador (se possível)
      .\build.ps1 -NoInstaller     # apenas o exe
      .\build.ps1 -FrameworkDependent   # exe pequeno, exige .NET 8 Desktop Runtime instalado
      .\build.ps1 -Run             # compila e executa
#>
[CmdletBinding()]
param(
    [switch]$NoInstaller,
    [switch]$FrameworkDependent,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

$project    = Join-Path $root 'src\ClaudeIndicator\ClaudeIndicator.csproj'
$publishDir = Join-Path $root 'publish'
$distDir    = Join-Path $root 'dist'

Write-Host '== Claude Indicator — build ==' -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'O .NET SDK 8 não foi encontrado.' -ForegroundColor Red
    Write-Host 'Instale com:  winget install Microsoft.DotNet.SDK.8'
    exit 1
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $publishDir,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '--nologo'
)

if ($FrameworkDependent) {
    $publishArgs += '-p:SelfContained=false'
    Write-Host 'Modo: framework-dependent (requer o .NET 8 Desktop Runtime no PC)' -ForegroundColor Yellow
} else {
    $publishArgs += '-p:SelfContained=true'
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
    Write-Host 'Modo: self-contained (roda em qualquer Windows x64)' -ForegroundColor Yellow
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { Write-Host 'Falha ao compilar.' -ForegroundColor Red; exit $LASTEXITCODE }

$exe = Join-Path $publishDir 'ClaudeIndicator.exe'
if (-not (Test-Path $exe)) { Write-Host 'Executável não encontrado após o publish.' -ForegroundColor Red; exit 1 }

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "OK: $exe ($sizeMb MB)" -ForegroundColor Green

if (-not $NoInstaller) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if ($iscc) {
        Write-Host 'Gerando instalador com Inno Setup…' -ForegroundColor Cyan
        & $iscc (Join-Path $root 'installer.iss')
        if ($LASTEXITCODE -eq 0) {
            Get-ChildItem $distDir -Filter *.exe | ForEach-Object {
                Write-Host ("Instalador: " + $_.FullName) -ForegroundColor Green
            }
        }
    } else {
        Write-Host 'Inno Setup 6 não encontrado — instalador não gerado.' -ForegroundColor Yellow
        Write-Host 'Para gerar:  winget install JRSoftware.InnoSetup   e rode este script de novo.'
        Write-Host 'Sem instalador o app funciona igual: basta executar o .exe da pasta publish.'
    }
}

if ($Run) {
    Write-Host 'Iniciando…' -ForegroundColor Cyan
    Start-Process $exe
}
