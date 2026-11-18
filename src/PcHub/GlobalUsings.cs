global using System.IO;
global using System.Net.Http;
global using PcHub.Platform;

// Native DLLs (nvml, dxgi, kernel32...) load only from System32 on Windows, never from the app folder,
// so a planted DLL next to a portable PCHUB.exe can't be picked up. (macOS imports use absolute paths.)
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
