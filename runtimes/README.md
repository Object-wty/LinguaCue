# LinguaCue 原生运行时目录

每个平台使用自己的 RID：

- win-x64
- linux-x64
- osx-x64
- osx-arm64

应用运行时至少需要：

    runtimes/<rid>/ffmpeg/ffmpeg(.exe)
    runtimes/<rid>/ffmpeg/ffprobe(.exe)
    runtimes/<rid>/whisper/whisper-cli(.exe)
    runtimes/<rid>/llama/llama-server(.exe)

CPU 运行时可以直接放在上述平铺目录。GPU 运行时放在子目录，程序按平台自动选择：

    runtimes/win-x64/whisper/cuda/
    runtimes/win-x64/llama/cuda/
    runtimes/linux-x64/llama/vulkan/
    runtimes/osx-arm64/whisper/metal/
    runtimes/osx-arm64/llama/metal/

Whisper 和 llama.cpp 的目录必须包含它们随包提供的所有 DLL、dylib、so 和辅助文件，不能只复制可执行文件。Windows CUDA 运行时还需要匹配的 CUDA DLL；macOS Metal 运行时需要对应的 dylib。

Git 仓库只保留本说明文件。原生二进制由 README.md 中的官方项目链接下载后放入本地目录，再运行 tools/Build-Packages.ps1。
