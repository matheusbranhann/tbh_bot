using TbhBot.Core;

namespace TbhBot.Core.Automation;

/// <summary>
/// Watchdog: reconecta quando o jogo fecha/reabre. Portado do racional do _watchdog_loop
/// (tbh_core.py ~997): quando o processo morre, avisa (Detached), e fica tentando re-attachar
/// ate o jogo voltar (Attached). Aqui NAO relançamos o jogo (isso e responsabilidade do
/// orquestrador/launcher); so cuidamos do ciclo attach/detach de forma idiomatica em C# (async,
/// cancelavel), no lugar da thread+GIL do Python.
/// </summary>
public sealed class Watchdog(Engine engine)
{
    // Intervalo de checagem de vida (~1s como no Python que usava sleep(2); 1s reage mais rapido).
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Disparado quando o jogo volta e o Attach() reconecta.</summary>
    public event Action? Attached;

    /// <summary>Disparado na transicao "estava vivo -> morreu".</summary>
    public event Action? Detached;

    /// <summary>Log textual (equivalente ao self.log do Python).</summary>
    public event Action<string>? Log;

    public async Task RunAsync(CancellationToken ct)
    {
        // Estado local: reflete se, na ultima checagem, o jogo estava conectado e vivo.
        // Comeca considerando o estado atual do engine para nao disparar Attached/Detached espurios.
        bool wasAlive = engine.IsAttached && engine.Target.IsAlive();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool alive = engine.IsAttached && engine.Target.IsAlive();

                if (wasAlive && !alive)
                {
                    // Transicao vivo -> morto: solta o dispatcher/estado do lado de quem escuta.
                    Log?.Invoke("watchdog: jogo fechou — aguardando reabrir");
                    Detached?.Invoke();
                    wasAlive = false;
                }
                else if (!wasAlive)
                {
                    // Ainda desconectado: tenta re-attachar. Se colar, o jogo voltou.
                    // (No Python o tick() re-attacha; aqui chamamos Attach() explicito.)
                    if (engine.Attach() && engine.Target.IsAlive())
                    {
                        Log?.Invoke("watchdog: jogo voltou — reconectado");
                        Attached?.Invoke();
                        wasAlive = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Nunca deixa o loop morrer por uma falha pontual de leitura/attach.
                Log?.Invoke($"watchdog: erro ({ex.Message}) — seguindo");
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // cancelamento e saida limpa, nao erro
            }
        }
    }
}
