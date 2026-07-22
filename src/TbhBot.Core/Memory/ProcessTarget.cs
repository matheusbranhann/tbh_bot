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
    public string? ModuleError { get; private set; }       // por que o módulo não resolveu no último Attach
    public bool   IsAttached => Handle != 0 && ModuleBase != 0;

    /// <summary>Attacha ao jogo em execução. Retorna false se o processo/módulo não for achado.</summary>
    public bool Attach()
    {
        var proc = Process.GetProcessesByName(ProcessName).FirstOrDefault();
        if (proc is null) return false;

        // Fecha o handle da sessao anterior: o re-attach do watchdog acontece toda vez que o jogo
        // reabre e, sem isso, cada restart vaza um handle do processo morto.
        if (Handle != 0) { NativeMethods.CloseHandle(Handle); Handle = 0; }
        var handle = NativeMethods.OpenProcess(NativeMethods.ACCESS_RW, false, (uint)proc.Id);
        if (handle == 0) return false;

        Handle = handle;
        ProcessId = proc.Id;

        // O MÓDULO TEM DE SER RESOLVIDO DE NOVO A CADA ATTACH — sem este reset, o valor da sessão
        // ANTERIOR sobrevivia quando a enumeração falhava, e como IsAttached só olha Handle+ModuleBase
        // o attach voltava TRUE com a base podre. Acontecia sempre que o watchdog reabria o jogo: o
        // ConnectLoop attacha ~1s depois, antes do GameAssembly.dll (~107 MB) estar enumerável. A partir
        // daí IsAttached ficava true e o ConnectLoop NUNCA mais re-attachava, então tudo que é Base+RVA
        // (dispatcher, auto-box, auto-stash, StageCache) lia lixo até o painel ser REINICIADO — e os
        // AOBs continuavam funcionando enquanto as faixas se sobrepunham, o que fazia a falha parecer
        // seletiva ("stats funciona, caixa não"). Zerado aqui, Attach devolve false e o ConnectLoop
        // tenta de novo a cada 1s até a base certa aparecer.
        ModuleBase = 0; ModuleSize = 0; ModulePath = "";
        ModuleError = null;

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
            if (ModuleBase == 0) ModuleError = $"{GameModule} ainda não está mapeado no processo";
        }
        catch (Exception ex)
        {
            // proc.Modules lança Win32Exception (ERROR_PARTIAL_COPY) num processo que ainda está subindo.
            // Não é fatal — é só "cedo demais". Guardamos o motivo em vez de engolir calado.
            ModuleError = ex.Message;
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
