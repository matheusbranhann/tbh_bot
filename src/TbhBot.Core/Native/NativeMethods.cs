using System.Runtime.InteropServices;

namespace TbhBot.Core.Native;

/// <summary>
/// P/Invoke fino para ler/escrever a memória de outro processo (kernel32).
/// Usa <c>[LibraryImport]</c> (source-generated) — mais rápido e AOT-friendly que o <c>[DllImport]</c> antigo.
/// </summary>
internal static partial class NativeMethods
{
    // Direitos de acesso ao processo (winnt.h).
    internal const uint PROCESS_VM_OPERATION     = 0x0008;
    internal const uint PROCESS_VM_READ          = 0x0010;
    internal const uint PROCESS_VM_WRITE         = 0x0020;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint ACCESS_RW = PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    // A syscall que domina o custo. lpBuffer é `ref byte` -> o marshaller fixa (pin) o buffer durante a chamada.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadProcessMemory(
        nint hProcess, nint lpBaseAddress, ref byte lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteProcessMemory(
        nint hProcess, nint lpBaseAddress, ref byte lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    // ---- enumeração de regiões (VirtualQueryEx) — base do AOB scan e da alloc de code-cave ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint VirtualQueryEx(nint hProcess, nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);

    // ---- alocação remota (code-caves para hitkill/dispatcher) ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint VirtualAllocEx(nint hProcess, nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualFreeEx(nint hProcess, nint lpAddress, nuint dwSize, uint dwFreeType);

    // ---- chamada off-thread de getters puros (il2cpp exports, iuw) via CreateRemoteThread ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateRemoteThread(nint hProcess, nint lpThreadAttributes, nuint dwStackSize,
        nint lpStartAddress, nint lpParameter, uint dwCreationFlags, nint lpThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    // Estados/proteções de página (memoryapi.h)
    internal const uint MEM_COMMIT  = 0x1000;
    internal const uint MEM_RESERVE = 0x2000;
    internal const uint MEM_FREE    = 0x10000;
    internal const uint MEM_RELEASE = 0x8000;
    internal const uint PAGE_READWRITE         = 0x04;
    internal const uint PAGE_EXECUTE_READWRITE = 0x40;
}

/// <summary>Layout x64 de MEMORY_BASIC_INFORMATION (winnt.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MEMORY_BASIC_INFORMATION
{
    public nuint BaseAddress;
    public nuint AllocationBase;
    public uint  AllocationProtect;
    public uint  __alignment1;
    public nuint RegionSize;
    public uint  State;
    public uint  Protect;
    public uint  Type;
    public uint  __alignment2;
}
