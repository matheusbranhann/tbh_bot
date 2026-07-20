using System.Diagnostics;
using TbhBot.Core.Native;

namespace TbhBot.Core.Memory;

/// <summary>
/// Acha o processo do jogo, abre um handle com direitos de leitura/escrita e resolve a base do
/// GameAssembly.dll (o módulo IL2CPP onde vivem os offsets). Equivalente ao attach do pymem no Python.
/// </summary>
public sealed class ProcessTarget : IDisposable
{
    public const string ProcessName = "TaskBarHero";   // sem o .exe (como o Process.GetProcessesByName espera)
    public const string GameModule  = "GameAssembly.dll";

    public nint   Handle     { get; private set; }
    public int    ProcessId  { get; private set; }
    public nint   ModuleBase { get; private set; }
    public int    ModuleSize { get; private set; }
    public string ModulePath { get; private set; } = "";   // caminho do GameAssembly.dll (p/ o build-hash)
    public bool   IsAttached => Handle != 0 && ModuleBase != 0;

    /// <summary>Attacha ao jogo em execução. Retorna false se o processo/módulo não for achado.</summary>
    public bool Attach()
    {
        var proc = Process.GetProcessesByName(ProcessName).FirstOrDefault();
        if (proc is null) return false;

        var handle = NativeMethods.OpenProcess(NativeMethods.ACCESS_RW, false, (uint)proc.Id);
        if (handle == 0) return false;

        Handle = handle;
        ProcessId = proc.Id;

        try
        {
            foreach (ProcessModule m in proc.Modules)
            {
                if (string.Equals(m.ModuleName, GameModule, StringComparison.OrdinalIgnoreCase))
                {
                    ModuleBase = m.BaseAddress;
                    ModuleSize = m.ModuleMemorySize;
                    ModulePath = m.FileName ?? "";
                    break;
                }
            }
        }
        catch (Exception)
        {
            // Modules pode lançar se o host não tiver 64-bit ou faltar direito; deixamos ModuleBase=0.
        }

        return IsAttached;
    }

    /// <summary>True se o processo ainda está vivo (para o watchdog de reconexão).</summary>
    public bool IsAlive()
    {
        if (ProcessId == 0) return false;
        try { using var p = Process.GetProcessById(ProcessId); return !p.HasExited; }
        catch { return false; }
    }

    public void Dispose()
    {
        if (Handle != 0) { NativeMethods.CloseHandle(Handle); Handle = 0; }
        ModuleBase = 0;
    }
}
