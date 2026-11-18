# PCHUB

A custom framework for running local AI apps on Windows: one place to watch your GPU, run ComfyUI, Ollama, Forge and other AI repos, and find new ones.

Windows 10 (1809+) and Windows 11, 64-bit. Built with .NET 10 and WPF. Self-contained: nothing else to install.

## What it does

- **GPU at a glance**: VRAM, load, temperature, power, fan and clock (NVIDIA). AMD and Intel show VRAM and load.
- **Your AI apps**: starts with an empty list. *Find my AI apps* looks for ComfyUI (portable, git, Desktop), Ollama, LM Studio, Forge / Stable Diffusion WebUI, Fooocus, SwarmUI, Text Generation WebUI, InvokeAI, KoboldCpp, Jan, GPT4All, Pinokio, AnythingLLM and Msty, and you tick which to add. *Add app* adds anything else.
- **Start / Stop / Free the GPU**: stop only the processes that belong to an app, asking windows to close first; *Free the GPU* closes your AI apps and unloads Ollama models, then reports how much VRAM came back.
- **Apps inside PCHUB**: ComfyUI and other web-based tools open in tabs inside the app (Edge WebView2). Pop-up browser tabs from tools started by PCHUB land in a PCHUB tab, and links to sites outside your PC always ask first.
- **Get apps**: image, video and chat apps from a built-in list and a live GitHub search, shown as **SFW** (left), **NSFW 18+** (right, hidden until the user confirms they are 18 or older) and **Not rated** (bottom). Downloads go to `Documents\PCHUB Downloads`; PCHUB picks the Windows build for the PC, asks first, verifies GitHub's checksum when one is published, marks files as downloaded from the internet, and never runs them.
- **Optional Claude scan**: with the user's own Anthropic API key, Claude reviews each download for safety red flags, legal red flags and an age gate. Off by default.
- **Drives & fragmentation**: each drive as an animated 3D block map; *Optimize* runs Windows' own optimizer.
- **History** of what started, stopped, crashed and how much VRAM it used.

## Your responsibility

You are responsible for everything you download. No matter which app, SFW or NSFW, it is up to you to make sure the files are clean (free of malware) and legal to use where you live. Before using any download, have Claude scan it ("Scan with Claude") and scan it with your antivirus. PCHUB and Claude's scan help, but they can't guarantee a file is safe or legal.

## Install

Download the zip from Releases, unzip anywhere and run `PCHUB.exe`. Settings and history are stored in `%LOCALAPPDATA%\PCHUB`. For portable use, put an empty file named `portable` next to `PCHUB.exe` and everything is kept in a `data` folder beside it.

## Build from source

```bash
dotnet publish src/PcHub/PcHub.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o app
```

Use `-r win-arm64` for ARM PCs. Requires the .NET 10 SDK.

## Safety design

- Never needs admin rights; Windows' optimizer asks for admin itself.
- Only ends processes it has matched to one of your apps, checks each process's start time so a reused process ID is never touched, and never ends itself or Windows' own processes.
- Never runs downloaded files. Downloads only from GitHub's own download hosts.
- The Anthropic API key is stored encrypted with Windows DPAPI for the current user.
