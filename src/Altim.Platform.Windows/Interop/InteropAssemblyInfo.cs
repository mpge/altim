#if WINDOWS

using System.Runtime.InteropServices;

// Every native module this assembly imports — user32, shell32, kernel32, gdiplus — is
// a Windows system library, so the loader is told to look in System32 and nowhere
// else. Without this the default search order includes the directory the executable
// was started from, and a file named gdiplus.dll dropped beside Altim would be loaded
// in preference to the real one.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

#endif
