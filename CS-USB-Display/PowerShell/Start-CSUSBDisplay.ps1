param(
    [switch]$SkipDependencyCheck,
    [switch]$NoMic,
    [switch]$NoAudio
)

$ErrorActionPreference = "Stop"
$Host.UI.RawUI.WindowTitle = "CS USB Display v0.2.0"

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Test-Command([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Test-VirtualDisplayDriver {
    try {
        $devices = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue
        return [bool]($devices | Where-Object {
            $_.FriendlyName -match "Virtual Display|Virtual-Display|IddSample|IDD"
        })
    } catch {
        return $false
    }
}

function Test-VBCable {
    try {
        $devices = Get-PnpDevice -Class Media -ErrorAction SilentlyContinue
        return [bool]($devices | Where-Object {
            $_.FriendlyName -match "VB-Audio|CABLE"
        })
    } catch {
        return $false
    }
}

function Install-VirtualDisplayDriver {
    if (-not (Test-Command "winget")) {
        throw "winget nao foi encontrado. Atualize o App Installer do Windows e tente novamente."
    }

    Write-Step "Instalando Virtual Display Driver"
    & winget install --id VirtualDrivers.Virtual-Display-Driver -e --accept-package-agreements --accept-source-agreements

    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao instalar o Virtual Display Driver pelo winget."
    }
}

function Install-VBCable {
    Write-Step "Baixando VB-CABLE oficial"
    $tempRoot = Join-Path $env:TEMP "CSUSBDisplay-VBCABLE"
    $zipPath = Join-Path $env:TEMP "VBCABLE_Driver_Pack45.zip"
    $url = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip"

    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath $tempRoot -Force

    $setup = Join-Path $tempRoot "VBCABLE_Setup_x64.exe"
    if (-not (Test-Path $setup)) {
        throw "Instalador x64 do VB-CABLE nao foi encontrado no pacote baixado."
    }

    $signature = Get-AuthenticodeSignature $setup
    if ($signature.Status -ne "Valid") {
        throw "A assinatura digital do instalador VB-CABLE nao e valida. Instalacao cancelada."
    }

    Write-Host "O Windows exibira o UAC e o instalador oficial do VB-CABLE." -ForegroundColor Yellow
    Write-Host "Clique em Install Driver e conclua o instalador." -ForegroundColor Yellow

    $p = Start-Process -FilePath $setup -Verb RunAs -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        Write-Warning "O instalador retornou codigo $($p.ExitCode). Verifique se a instalacao foi concluida."
    }

    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
}

Write-Host "CS USB Display v0.2.0" -ForegroundColor Green
Write-Host "Segunda tela estendida + audio do PC + microfone do tablet via USB"

if (-not $SkipDependencyCheck) {
    $vddInstalledNow = $false
    $vbInstalledNow = $false

    if (-not (Test-VirtualDisplayDriver)) {
        Write-Host ""
        Write-Warning "Virtual Display Driver nao detectado."
        $answer = Read-Host "Instalar agora pelo winget? [S/N]"
        if ($answer -match '^[sSyY]') {
            Install-VirtualDisplayDriver
            $vddInstalledNow = $true
        } else {
            throw "Sem um monitor virtual, o tablet nao pode funcionar como segunda tela estendida."
        }
    }

    if (-not $NoMic -and -not (Test-VBCable)) {
        Write-Host ""
        Write-Warning "VB-CABLE nao detectado. Ele e necessario para o microfone do tablet aparecer como microfone no Windows."
        $answer = Read-Host "Baixar e abrir o instalador oficial do VB-CABLE agora? [S/N]"
        if ($answer -match '^[sSyY]') {
            Install-VBCable
            $vbInstalledNow = $true
        } else {
            Write-Warning "O video e o audio de saida podem funcionar, mas o microfone virtual ficara desativado."
        }
    }

    if ($vbInstalledNow) {
        Write-Host ""
        Write-Warning "O VB-CABLE exige reinicializacao do Windows para registrar o driver corretamente."
        Write-Host "Reinicie o PC e execute este script novamente." -ForegroundColor Yellow
        Read-Host "Pressione ENTER para sair"
        exit 0
    }

    if ($vddInstalledNow) {
        Start-Sleep -Seconds 3
    }
}

Write-Step "Ativando modo Estender"
$displaySwitch = Join-Path $env:WINDIR "System32\DisplaySwitch.exe"
Start-Process -FilePath $displaySwitch -ArgumentList "/extend" -Wait
Start-Sleep -Seconds 2

$hostExe = Join-Path $PSScriptRoot "CSUsbDisplayHost.exe"
if (-not (Test-Path $hostExe)) {
    throw "CSUsbDisplayHost.exe nao foi encontrado em $PSScriptRoot"
}

$adbExe = Join-Path $PSScriptRoot "platform-tools\adb.exe"
if (-not (Test-Path $adbExe)) {
    throw "ADB nao foi encontrado. Extraia todo o ZIP do Windows antes de executar o script."
}

Write-Step "Verificando tablet USB"
$adbOutput = & $adbExe devices
Write-Host ($adbOutput -join [Environment]::NewLine)

if (-not (($adbOutput -join "`n") -match "\tdevice")) {
    Write-Warning "Nenhum tablet Android autorizado foi detectado."
    Write-Host "Ative Depuracao USB no tablet, conecte o cabo e aceite a autorizacao RSA."
    Read-Host "Depois disso, pressione ENTER para tentar novamente"
    $adbOutput = & $adbExe devices
    if (-not (($adbOutput -join "`n") -match "\tdevice")) {
        throw "Tablet ainda nao autorizado no ADB."
    }
}

Write-Step "Iniciando CS USB Display"
Start-Process -FilePath $hostExe -ArgumentList @("--auto")

Write-Host ""
Write-Host "O host foi iniciado." -ForegroundColor Green
Write-Host "A tela nao primaria e selecionada automaticamente."
Write-Host "Para usar o microfone do tablet em Discord/Teams/etc., selecione CABLE Output (VB-Audio Virtual Cable) como microfone."
