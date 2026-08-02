[CmdletBinding()]
param(
    [string]$ModelSource = (Join-Path $PSScriptRoot '..\models'),
    [string]$RuntimeSource = (Join-Path $PSScriptRoot '..\runtimes'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\packages'),
    [switch]$IncludeQualityModel,
    [switch]$StandardOnly,
    [switch]$SkipExisting
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\LinguaCue.App\LinguaCue.App.csproj'
$modelRoot = [System.IO.Path]::GetFullPath($ModelSource)
$runtimeRoot = [System.IO.Path]::GetFullPath($RuntimeSource)
$outputRootPath = [System.IO.Path]::GetFullPath($OutputRoot)
$rids = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')
$tarCommand = (Get-Command tar -ErrorAction SilentlyContinue).Source
$sevenZipCommand = (Get-Command 7za.exe,7z.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if ($IsWindows -and (Test-Path -LiteralPath 'C:\Program Files\Git\usr\bin\tar.exe')) {
    # Windows 10's bsdtar does not support --mode; Git for Windows ships GNU tar.
    $tarCommand = 'C:\Program Files\Git\usr\bin\tar.exe'
    $env:PATH = "C:\Program Files\Git\usr\bin;$env:PATH"
}

function Assert-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "缺少 $Description：$Path"
    }
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "缺少目录：$Source"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Copy-CommonAssets([string]$Destination) {
    Assert-File (Join-Path $repoRoot 'portable.flag') 'portable.flag'
    Assert-File (Join-Path $repoRoot 'README.md') 'README.md'
    Assert-File (Join-Path $repoRoot 'LICENSE') 'LICENSE'
    Assert-File (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') 'THIRD_PARTY_NOTICES.md'

    Copy-Item -LiteralPath (Join-Path $repoRoot 'portable.flag') -Destination $Destination -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $Destination -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $Destination -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $Destination -Force

    $fontSource = Join-Path $repoRoot 'assets'
    if (Test-Path -LiteralPath $fontSource -PathType Container) {
        Copy-DirectoryContents $fontSource (Join-Path $Destination 'assets')
    }
}

function Copy-Models([string]$Destination) {
    $speech = Join-Path $modelRoot 'speech\ggml-large-v3-turbo.bin'
    $standard = Join-Path $modelRoot 'translation\Hy-MT2-1.8B-Q4_K_M.gguf'
    Assert-File $speech 'Whisper large-v3-turbo 模型'
    Assert-File $standard 'Hy-MT2 1.8B 标准模型'

    $speechDestination = Join-Path $Destination 'models\speech'
    $translationDestination = Join-Path $Destination 'models\translation'
    New-Item -ItemType Directory -Path $speechDestination,$translationDestination -Force | Out-Null
    Copy-Item -LiteralPath $speech -Destination $speechDestination -Force
    Copy-Item -LiteralPath $standard -Destination $translationDestination -Force

    if ($IncludeQualityModel -or -not $StandardOnly) {
        $quality = Join-Path $modelRoot 'translation\Hy-MT2-7B-Q4_K_M.gguf'
        Assert-File $quality 'Hy-MT2 7B 高质量模型'
        Copy-Item -LiteralPath $quality -Destination $translationDestination -Force
    }
}

function Assert-Runtime([string]$Destination, [string]$Rid) {
    $suffix = if ($Rid -eq 'win-x64') { '.exe' } else { '' }
    Assert-File (Join-Path $Destination "runtimes\$Rid\ffmpeg\ffmpeg$suffix") 'FFmpeg'
    Assert-File (Join-Path $Destination "runtimes\$Rid\ffmpeg\ffprobe$suffix") 'FFprobe'
    Assert-File (Join-Path $Destination "runtimes\$Rid\whisper\whisper-cli$suffix") 'whisper-cli'
    Assert-File (Join-Path $Destination "runtimes\$Rid\llama\llama-server$suffix") 'llama-server'
}

function Move-PayloadIntoMacBundle([string]$Stage, [string]$Payload) {
    $bundle = Join-Path $Stage 'LinguaCue.app'
    $macos = Join-Path $bundle 'Contents\MacOS'
    New-Item -ItemType Directory -Path $macos -Force | Out-Null
    Get-ChildItem -LiteralPath $Payload -Force | Move-Item -Destination $macos -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\macos\Info.plist') -Destination (Join-Path $bundle 'Contents\Info.plist') -Force
    Remove-Item -LiteralPath $Payload -Force -Recurse
}

New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

foreach ($rid in $rids) {
    $stage = Join-Path $outputRootPath "stage-$rid"
    $publishRoot = Join-Path $stage 'payload'
    $archive = if ($rid -eq 'win-x64') {
        Join-Path $outputRootPath 'LinguaCue-win-x64.zip'
    } else {
        Join-Path $outputRootPath "LinguaCue-$rid.tar.gz"
    }

    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Force -Recurse
    }
    if (Test-Path -LiteralPath $archive) {
        if ($SkipExisting) {
            Write-Host "跳过已有归档：$archive"
            if (Test-Path -LiteralPath $stage) {
                Remove-Item -LiteralPath $stage -Force -Recurse
            }
            continue
        }
        Remove-Item -LiteralPath $archive -Force
    }

    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    Write-Host "Publishing $rid ..."
    dotnet publish $project -c Release -r $rid --self-contained true --no-restore -p:IncludeBundledAssets=false -p:PublishSingleFile=false -p:PublishTrimmed=false -o $publishRoot
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败：$rid"
    }

    Copy-CommonAssets $publishRoot
    Copy-Models $publishRoot

    $runtimeSourceForRid = Join-Path $runtimeRoot $rid
    Copy-DirectoryContents $runtimeSourceForRid (Join-Path $publishRoot "runtimes\$rid")
    Assert-Runtime $publishRoot $rid

    if ($rid.StartsWith('osx-', [System.StringComparison]::Ordinal)) {
        Move-PayloadIntoMacBundle $stage $publishRoot
    } else {
        Get-ChildItem -LiteralPath $publishRoot -Force | Move-Item -Destination $stage -Force
        Remove-Item -LiteralPath $publishRoot -Force -Recurse
    }

    Write-Host "Packing $archive ..."
    if ($rid -eq 'win-x64') {
        if (-not [string]::IsNullOrWhiteSpace($sevenZipCommand)) {
            # Compress-Archive cannot write ZIP64 entries larger than its stream limit.
            Push-Location $stage
            try {
                & $sevenZipCommand a -tzip -mx=1 $archive '*'
            }
            finally {
                Pop-Location
            }
            if ($LASTEXITCODE -ne 0) {
                throw "7-Zip 打包失败：$rid"
            }
        } else {
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
        }
    } else {
        if ([string]::IsNullOrWhiteSpace($tarCommand)) {
            throw '找不到 tar，无法生成 Linux/macOS 归档。'
        }
        Push-Location $outputRootPath
        try {
            # GNU tar under Git for Windows treats a drive-letter archive path as a host name;
            # create it from the output directory using only the archive file name.
            & $tarCommand -czf (Split-Path -Leaf $archive) --mode=755 -C $stage .
        }
        finally {
            Pop-Location
        }
        if ($LASTEXITCODE -ne 0) {
            throw "tar 打包失败：$rid"
        }
    }

    Remove-Item -LiteralPath $stage -Force -Recurse
    Write-Host "完成：$archive"
}
