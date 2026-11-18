PCHUB
=====

A small Windows app that watches your graphics card and runs your local AI apps from one place.

What it does
- Live GPU stats: VRAM, load, temperature, power, fan and clock (NVIDIA).
  AMD and Intel cards show VRAM and load (temperature/power need an NVIDIA card).
- PCHUB works on its own; no other apps are needed. It starts with an empty app list and
  never adds anything by itself. If you want, "Find my AI apps" looks for ComfyUI (portable,
  git or Desktop), Ollama, LM Studio, Forge / Stable Diffusion WebUI, Fooocus, SwarmUI,
  Text Generation WebUI, InvokeAI, KoboldCpp, Jan, GPT4All, Pinokio, AnythingLLM and Msty,
  and you tick which ones to add. "Add app" adds anything else.
- Start / Stop each app, see its VRAM, GPU use and uptime, open its log.
- Web-based apps (ComfyUI, Forge, Fooocus...) open in a tab INSIDE PCHUB, not in your browser.
  Apps started from PCHUB that try to pop open a browser tab by themselves are shown in a PCHUB
  tab instead. Links to sites outside your PC always ask before opening your browser.
  (Uses Microsoft Edge WebView2, built into Windows 11 and most Windows 10 PCs; if it's
  missing, PCHUB tells you and offers a button. Nothing opens without your click.)
- "Free the GPU": asks first, then closes your AI apps and unloads Ollama models,
  and tells you how much VRAM came back.
- "Get apps": image, video and chat apps from a built-in list plus a live GitHub search.
  SFW apps on the left (green), NSFW 18+ apps on the right (red; hidden until you confirm
  you're 18 or older), and apps PCHUB couldn't classify at the bottom (grey).
  Downloads go to Documents\PCHUB Downloads (changeable). PCHUB picks the Windows build for
  your PC, asks before downloading (file, size, source, checksum), verifies GitHub's checksum
  when one is published, marks the file as downloaded from the internet, and never runs it.
- Optional Claude scan: with your own Anthropic API key, PCHUB asks Claude after each
  download (or when you press "Scan with Claude") to look for safety red flags, legal red
  flags and whether the app has its own age gate. Off by default; costs about $0.05-$0.15
  per scan on your Anthropic account. Red flags or a missing age gate are shown as a
  warning and noted in History.

YOUR RESPONSIBILITY
  You are responsible for everything you download. No matter which app, SFW or NSFW, it is
  up to you to make sure the files are clean (free of malware) and legal to use where you
  live. Before using any download, have Claude scan it ("Scan with Claude") and scan it with
  your antivirus. PCHUB and Claude's scan help, but they can't guarantee a file is safe or
  legal.
- "Using the GPU": every program using video memory right now.
- Drives & fragmentation: each drive as a 3D block map, with fragmentation from Windows'
  last analysis. "Optimize" runs Windows' own optimizer (asks for admin) and the blocks
  animate while it works.
- History of what started, stopped, crashed and how much VRAM it peaked at.

Requirements
- Windows 10 (version 1809 or newer) or Windows 11, 64-bit.
- Nothing else to install: .NET is built in.
- A Mac version (macOS 13 or newer, Apple Silicon or Intel) is on the GitHub Releases page.

Install
- Unzip anywhere and run PCHUB.exe.
- Settings and history go to %LOCALAPPDATA%\PCHUB.
- Portable mode: put an empty file named "portable" next to PCHUB.exe and everything is
  kept in a "data" folder beside it instead (good for USB sticks).

Safety
- PCHUB only ends processes it has matched to one of your apps, asks windows to close
  first, never ends Windows' own processes or itself, and never needs admin rights.
- The app list is data\tools.json (or %LOCALAPPDATA%\PCHUB\tools.json). "Edit list" opens it.

Low-power mode (bottom right) samples less often for older PCs and laptops on battery.

Build from source
  dotnet publish src\PcHub\PcHub.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o app
  (use -r win-arm64 for ARM PCs)
