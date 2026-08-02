# Third-party notices

LinguaCue source code is MIT licensed. Release bundles can contain third-party components and model weights that retain their own licenses.

| Component | Typical license | Project/model page |
| --- | --- | --- |
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia |
| Semi.Avalonia | MIT | https://github.com/irihitech/Semi.Avalonia |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| FFmpeg | LGPL 2.1+ or GPL, depending on build | https://ffmpeg.org/legal.html |
| whisper.cpp | MIT | https://github.com/ggml-org/whisper.cpp |
| llama.cpp | MIT | https://github.com/ggml-org/llama.cpp |
| Whisper model weights | MIT | https://github.com/openai/whisper |
| Hy-MT2 model weights | Apache-2.0 | https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF |
| Noto Sans SC | SIL Open Font License 1.1 | https://github.com/notofonts/noto-cjk |

Release maintainers must preserve the exact licenses and source offers required by the binaries they distribute. In particular, an FFmpeg build can become GPL depending on its configure options.

The bundled Noto Sans SC font is in `assets/fonts/files/`; its license is included at `assets/fonts/OFL.txt`.
