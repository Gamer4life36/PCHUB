global using System.IO;
global using System.Net.Http;

// Native DLLs (nvml, dxgi, kernel32...) load only from System32, never from the app folder,
// so a planted DLL next to a portable PCHUB.exe can't be picked up.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
