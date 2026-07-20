namespace TbhBot.Core.Game;

/// <summary>
/// Executa chamadas na MAIN-THREAD do jogo (necessário para ações Unity async/UI: abrir caixa, mover item,
/// o cubo). No engine Python isso é um code-cave que faz hook no <c>InputManager.Update</c> e consome comandos
/// numerados a cada frame.
///
/// <para><b>Por que é uma interface (e não já implementado):</b> o dispatcher real precisa de (1) um
/// disassembler para medir o prólogo relocável do <c>Update</c>, (2) geração de shellcode, (3) suspend/resume
/// de todas as threads durante o patch, (4) resolução de exports IL2CPP e um <c>remote_call</c>. Reescrever
/// isso <i>cego</i> (sem iterar contra o jogo vivo) produziria código quebrado — então fica como interface +
/// stub, para ser implementado e validado ao vivo na box. Ver <c>Game/DISPATCHER_PORT_NOTES.md</c>.</para>
/// </summary>
public interface IMainThreadDispatcher
{
    /// <summary>True quando o hook está instalado e pronto para receber comandos.</summary>
    bool IsReady { get; }

    /// <summary>Chama <paramref name="funcVa"/>(rcx=argP, edx=argI) na main-thread (cmd 12). Retorna rax&amp;0xffffffff.</summary>
    int? Call(long funcVa, nint argP = 0, int argI = 0);

    /// <summary>Enfileira um comando numerado do cave (abrir caixa/mover/fundir). True se foi consumido.</summary>
    bool Command(int cmd, nint argP = 0, int argI = 0);
}

/// <summary>
/// Stub do dispatcher: nunca fica pronto. Enquanto a Fase 3-dispatcher (code-cave) não é portada+validada ao
/// vivo, as ações que dependem da main-thread (auto-box/stash/fuse) ficam inertes — a automação loga e segue.
/// Toda a leitura de estado + cheats por patch de memória (godmode/hitkill/speed/stats/stage/runas/cubo/stage)
/// NÃO dependem daqui e funcionam.
/// </summary>
public sealed class StubDispatcher : IMainThreadDispatcher
{
    public bool IsReady => false;
    public int? Call(long funcVa, nint argP = 0, int argI = 0) => null;
    public bool Command(int cmd, nint argP = 0, int argI = 0) => false;
}
