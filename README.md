# PCHUB

A custom framework for running local AI apps on Windows and macOS: one place to watch your GPU, run ComfyUI, Ollama, Forge and other AI repos, and find new ones.

- **Windows** 10 (1809+) and 11, 64-bit (x64 or ARM).
- **macOS** 13 Ventura or newer, Apple Silicon or Intel.

Built with .NET 10 and Avalonia. Self-contained: nothing else to install.

## What it does

- **GPU at a glance**: VRAM, load, temperature, power, fan and clock (NVIDIA). AMD and Intel show VRAM and load. On a Mac, PCHUB shows GPU load and the memory in use (Apple Silicon shares memory between the CPU and GPU).
- **Your AI apps**: starts with an empty list. *Find my AI apps* looks for ComfyUI (portable, git, Desktop), Ollama, LM Studio, Forge / Stable Diffusion WebUI, Fooocus, SwarmUI, Text Generation WebUI, InvokeAI, KoboldCpp, Jan, GPT4All, Pinokio, AnythingLLM and Msty, and you tick which to add. *Add app* adds anything else.
- **Start / Stop / Free the GPU**: stop only the processes that belong to an app, asking them to close first; *Free the GPU* closes your AI apps and unloads Ollama models, then reports how much memory came back.
- **Apps inside PCHUB**: ComfyUI and other web-based tools open in tabs inside the app (Edge WebView2 on Windows, the built-in WebKit on macOS). Pop-up browser tabs from tools started by PCHUB land in a PCHUB tab, and links to sites outside your computer always ask first.
- **Get apps**: image, video and chat apps from a built-in list and a live GitHub search, shown as **SFW** (left), **NSFW 18+** (right, hidden until the user confirms they are 18 or older) and **Not rated** (bottom). Downloads go to `Documents/PCHUB Downloads`; PCHUB picks the right build for the computer (Windows or Mac, Apple Silicon or Intel), asks first, verifies GitHub's checksum when one is published, marks files as downloaded from the internet, and never runs them.
- **Optional Claude scan**: with the user's own Anthropic API key, Claude reviews each download for safety red flags, legal red flags and an age gate. Off by default.
- **Drives**: on Windows, each drive as an animated 3D block map; *Optimize* runs Windows' own optimizer. On a Mac, drives show used and free space (Macs don't need defragmenting).
- **History** of what started, stopped, crashed and how much memory it used.

## Your responsibility

You are responsible for everything you download. No matter which app, SFW or NSFW, it is up to you to make sure the files are clean (free of malware) and legal to use where you live. Before using any download, have Claude scan it ("Scan with Claude") and scan it with your antivirus. PCHUB and Claude's scan help, but they can't guarantee a file is safe or legal.

## Install

Download the zip for your computer from [Releases](../../releases).

**Windows**: unzip anywhere and run `PCHUB.exe`. Settings and history are stored in `%LOCALAPPDATA%\PCHUB`. For portable use, put an empty file named `portable` next to `PCHUB.exe` and everything is kept in a `data` folder beside it.

**macOS**: pick `PCHUB-macos-apple-silicon.zip` (M1 and newer) or `PCHUB-macos-intel.zip`. Unzip it and drag `PCHUB.app` to Applications. PCHUB isn't notarized by Apple, so the first time, **right-click PCHUB.app → Open → Open** (on macOS 15 and newer: try to open it once, then go to System Settings → Privacy & Security and click *Open Anyway*). Settings and history are stored in `~/Library/Application Support/PCHUB`.

## Build from source

Requires the .NET 10 SDK.

```bash
# Windows
dotnet publish src/PcHub/PcHub.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o app

# macOS (then wrap in PCHUB.app as in .github/workflows/build.yml)
dotnet publish src/PcHub/PcHub.csproj -c Release -r osx-arm64 --self-contained true -p:PublishReadyToRun=false -o out
```

Use `-r win-arm64` for ARM PCs and `-r osx-x64` for Intel Macs. Every push builds all three on GitHub Actions; pushing a `v*` tag publishes a release.

## Safety design

- Never needs admin rights; Windows' optimizer asks for admin itself.
- Only ends processes it has matched to one of your apps, checks each process's start time so a reused process ID is never touched, and never ends itself or the system's own processes.
- Never runs downloaded files. Downloads only from GitHub's own download hosts.
- The Anthropic API key is stored encrypted with Windows DPAPI for the current user, or in the macOS Keychain.
