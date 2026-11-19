PCHUB for Linux
===============

Watches your graphics card and runs your local AI apps (ComfyUI, Ollama, Forge, LM Studio...) from
one place. Full guide: https://github.com/Gamer4life36/PCHUB

Start
- Run ./install.sh to add PCHUB to your app menu (for your user only, no sudo needed),
  or just run ./PCHUB from this folder.
- ./install.sh --remove takes it out of the menu again. Delete this folder to remove PCHUB.

Needs
- A 64-bit Linux desktop with glibc: Ubuntu 22.04+, Debian 12+, Fedora, Mint, Arch, Pop!_OS...
- To show apps like ComfyUI inside PCHUB: WebKitGTK.
    Ubuntu, Debian, Mint:  sudo apt install libwebkit2gtk-4.1-0
    Fedora:                sudo dnf install webkit2gtk4.1
    Arch:                  sudo pacman -S webkit2gtk-4.1
- GPU stats: NVIDIA cards need NVIDIA's own driver (not nouveau) for full sensors and VRAM per app.
  AMD cards show VRAM, load, temperature and power through the standard amdgpu driver.

Where things are
- Settings and history: ~/.local/share/PCHUB
- Your Claude API key (optional): the desktop keyring if secret-tool is installed
  (sudo apt install libsecret-tools), otherwise a file only your user can read.

YOUR RESPONSIBILITY
  You are responsible for everything you download. No matter which app, SFW or NSFW, it is up to
  you to make sure the files are clean (free of malware) and legal to use where you live. Before
  using any download, have Claude scan it ("Scan with Claude") and scan it with your antivirus.
  PCHUB and Claude's scan help, but they can't guarantee a file is safe or legal.

Safety
- PCHUB only ends processes it has matched to one of your apps, only your own (never system
  services or other users' programs), and never needs root.
- It never runs downloaded files.
