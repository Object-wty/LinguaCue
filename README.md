# LinguaCue（语幕）

LinguaCue 是 Windows、Linux 和 macOS 上的完全离线字幕工作台：本地提取音频、Whisper 生成时间轴、Hy-MT2 翻译，并可把原文、译文或双语字幕烧录进新视频。媒体不会上传，不需要 Python、Docker、账号或云端 API。

## 源码仓库与便携包

Git 仓库只保存源码、项目文件、测试、文档和许可证。模型权重、FFmpeg、Whisper、llama.cpp、字体二进制、媒体样本、编译产物和压缩包都被 .gitignore 排除。

便携包不提交到 Git，生成在 artifacts/packages/：

    LinguaCue-win-x64.zip
    LinguaCue-linux-x64.tar.gz
    LinguaCue-osx-x64.tar.gz
    LinguaCue-osx-arm64.tar.gz

Windows 解压后双击 LinguaCue.exe；Linux/macOS 解压 tar.gz 后运行对应的 LinguaCue（macOS 可双击 LinguaCue.app）。首次运行前请确认包内已经放入模型和对应平台的原生运行库。

## 功能

- 一次选择多个视频；每个任务有独立 CLI 进程、临时目录、日志、进度、耗时、取消、重试和烧录状态。
- FIFO 队列，并发数 1–4，默认 2；转换和烧录共享并发上限。
- 输出原文、译文和双语 UTF-8 SRT，并可在界面中校对后导出。
- 任务完成后可选择原文、译文或双语 SRT，烧录为新的 H.264/AAC MP4，不覆盖原视频。
- 默认内置 Noto Sans SC，字号 20；支持字体、字号、文字颜色、描边颜色、描边宽度和底部边距。
- 自动选择 CUDA → Vulkan → CPU（Windows/Linux），Metal → CPU（macOS）；GPU 初始化失败自动回退 CPU。
- GPU 平衡档默认 Whisper beam-size/best-of 为 3/3；另有极速 1/1 与质量 5/5。
- 桌面端只负责界面和队列，转换、翻译、烧录全部由 LinguaCue.Cli 子进程执行。

## 第一次准备：模型和原生库

下面的文件必须放进应用目录的相对路径。下载页面均来自组件项目或模型作者；大文件建议使用浏览器或支持断点续传的下载器，并下载后核对页面提供的 SHA256。

### 1. Whisper 模型（必需）

文件名必须是 models/speech/ggml-large-v3-turbo.bin，大小约 1.62 GiB。

- Whisper.cpp 模型仓库：https://huggingface.co/ggerganov/whisper.cpp/tree/main
- 直接下载：https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin?download=true
- 模型仓库列出的 SHA256：4af2b29d7ec73d781377bfd1758ca957a807e941

### 2. Hy-MT2 翻译模型（至少标准模型）

标准模型文件名是 models/translation/Hy-MT2-1.8B-Q4_K_M.gguf，约 1.13 GiB。

- Hy-MT2 1.8B-GGUF（腾讯官方）：https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF
- 直接下载：https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf?download=true

高质量模型文件名是 models/translation/Hy-MT2-7B-Q4_K_M.gguf，约 4.62 GiB。便携包默认同时复制 1.8B 和 7B；如果磁盘空间有限，可用 `-StandardOnly` 只生成标准模型包。

- Hy-MT2 7B-GGUF（腾讯官方）：https://huggingface.co/tencent/Hy-MT2-7B-GGUF
- 直接下载：https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/main/Hy-MT2-7B-Q4_K_M.gguf?download=true

### 3. FFmpeg / FFprobe（必需）

每个平台都需要 ffmpeg 和 ffprobe 两个文件，放在 runtimes/<rid>/ffmpeg/。

- FFmpeg 官方下载页：https://ffmpeg.org/download.html
- Windows 可使用 gyan.dev Windows builds：https://www.gyan.dev/ffmpeg/builds/，解压 ffmpeg.exe 和 ffprobe.exe。
- Linux x64 可使用 johnvansickle 静态构建：https://johnvansickle.com/ffmpeg/，解压其中的 ffmpeg 和 ffprobe。
- macOS 可使用 evermeet.cx macOS builds：https://evermeet.cx/ffmpeg/，分别下载 `https://evermeet.cx/ffmpeg/getrelease/zip`（ffmpeg）与 `https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip`（ffprobe）。该站点目前只提供 Intel 构建；Apple Silicon 包需要 Rosetta 2，或换用本机 arm64 FFmpeg 构建。

FFmpeg 的具体构建可能是 LGPL 或 GPL；请按实际构建保留对应许可证和源代码提供信息。

### 4. Whisper.cpp（必需）

可执行文件名必须是 whisper-cli（Windows 为 whisper-cli.exe），放在 runtimes/<rid>/whisper/ 或对应 GPU 子目录。

- Windows CPU：在 https://github.com/ggml-org/whisper.cpp/releases 下载 whisper-bin-x64.zip。
- Windows NVIDIA：下载 whisper-cublas-12.4.0-bin-x64.zip，把 Release/ 内全部文件放到 runtimes/win-x64/whisper/cuda/。
- Linux x64 CPU：官方 Release 的 whisper-bin-ubuntu-x64.tar.gz。
- macOS Intel / Apple Silicon：可使用 https://github.com/sjoerdteunisse/whisper.cpp/releases/tag/v1.0.0 中的 whisper-cpp-darwin-x64.zip / whisper-cpp-darwin-arm64.zip。这是第三方构建，请同时保留其许可证和校验值。

### 5. llama.cpp（启用翻译必需）

至少需要 llama-server（兼容回退 llama-completion/llama-cli），放在 runtimes/<rid>/llama/ 或对应 GPU 子目录。

- llama.cpp 官方 Releases：https://github.com/ggml-org/llama.cpp/releases
- Windows CPU：llama-*-bin-win-cpu-x64.zip
- Windows NVIDIA：llama-*-bin-win-cuda-12.4-x64.zip，并额外放入匹配的 cudart-llama-bin-win-cuda-12.4-x64.zip；文件放到 runtimes/win-x64/llama/cuda/。
- Windows Vulkan：llama-*-bin-win-vulkan-x64.zip，放到 runtimes/win-x64/llama/vulkan/。
- Linux x64：llama-*-bin-ubuntu-x64.tar.gz
- macOS Intel：llama-*-bin-macos-x64.tar.gz
- macOS Apple Silicon：llama-*-bin-macos-arm64.tar.gz

## 目录结构

    LinguaCue/
    ├─ LinguaCue.exe / LinguaCue / LinguaCue.app
    ├─ LinguaCue.Cli(.exe)
    ├─ portable.flag
    ├─ assets/
    │  └─ fonts/files/NotoSansSC-VF.ttf
    ├─ models/
    │  ├─ speech/ggml-large-v3-turbo.bin
    │  └─ translation/
    │     ├─ Hy-MT2-1.8B-Q4_K_M.gguf
    │     └─ Hy-MT2-7B-Q4_K_M.gguf       （可选）
    └─ runtimes/
       └─ <rid>/
          ├─ ffmpeg/ffmpeg(.exe), ffprobe(.exe)
          ├─ whisper/whisper-cli(.exe)   （CPU 平铺目录）
          │  └─ cuda/ 或 metal/ 或 vulkan/
          └─ llama/llama-server(.exe)    （CPU 平铺目录）
             └─ cuda/ 或 metal/ 或 vulkan/

程序只查找当前可执行文件目录下的 models/、assets/ 与 runtimes/<rid>/，不会从其他下载目录猜测依赖。GPU 目录不存在或启动失败时自动使用 CPU 平铺目录。

## 编译源码

需要 .NET 8 SDK：https://dotnet.microsoft.com/download/dotnet/8.0。源码编译不需要模型和原生库；只有运行转换时才需要它们。

    dotnet restore LinguaCue.sln
    dotnet build LinguaCue.sln -c Debug
    dotnet test LinguaCue.sln -c Release

## 生成四个平台便携包

先按上面的目录结构准备模型和原生库，然后在 Windows PowerShell 执行：

    .\tools\Build-Packages.ps1 -ModelSource ".\models" -RuntimeSource ".\runtimes" -OutputRoot ".\artifacts\packages"

如果模型暂时放在 Debug 输出目录，可指定：

    .\tools\Build-Packages.ps1 -ModelSource ".\src\LinguaCue.App\bin\Debug\net8.0\models"

脚本会为 win-x64、linux-x64、osx-x64、osx-arm64 分别执行自包含发布，复制两个 Hy-MT2 模型、字体和匹配 RID 的原生库，并生成 Windows ZIP 及 Linux/macOS tar.gz。发布包不依赖目标机安装 .NET；macOS 包会额外生成可双击的 LinguaCue.app。

Windows 同时打包 7B 模型时需要安装 7-Zip（脚本会自动寻找 `7z.exe` 或 `7za.exe`），因为系统 `Compress-Archive` 无法稳定写入超过 2 GiB 的 ZIP。

单独发布 Windows：

    dotnet publish src/LinguaCue.App/LinguaCue.App.csproj -c Release -r win-x64 --self-contained true -p:IncludeBundledAssets=false -o artifacts/publish/win-x64

## CLI

转换：

    LinguaCue.Cli convert --input "D:\media\demo.mp4" --output "D:\media\subtitles" --source auto --target zh --translate true --bilingual true --model standard --acceleration auto --performance balanced --threads 6

烧录：

    LinguaCue.Cli burn --input "D:\media\demo.mp4" --subtitle "D:\media\subtitles\demo.bilingual.zh.srt" --output "D:\media\demo.subtitled.mp4" --font-name "Noto Sans SC" --font-size 20 --primary-color "#FFFFFF" --outline-color "#000000" --outline-width 3 --margin-bottom 20 --encoder auto

标准输出是 UTF-8 JSON Lines，包含 progress、result、error、canceled 和操作类型；桌面任务日志会记录最终命令、线程数、后端和回退原因。

## 测试和许可证

    dotnet test tests/LinguaCue.Core.Tests/LinguaCue.Core.Tests.csproj

测试覆盖 SRT 编码与解析、双语内容、ASS 转义与样式、Unicode 路径、Worker 协议、运行时后端解析、队列并发/FIFO/取消、真实 CLI 子进程和字幕烧录。

源码使用 MIT（LICENSE）。第三方组件和模型权重的许可证见 THIRD_PARTY_NOTICES.md；Noto Sans SC 的 SIL OFL 文本在 assets/fonts/OFL.txt。
