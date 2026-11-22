using System.Diagnostics;

namespace Balanceador.Servicos;

public sealed class ControleIntensidade(int intensidade)
{
    private const double DuracaoJanelaMs = 20.0;
    private readonly Stopwatch janela = Stopwatch.StartNew();

    public async ValueTask Aguardar(CancellationToken cancelamento)
    {
        double orcamentoMs = DuracaoJanelaMs * intensidade / 100.0;
        double decorridoMs = janela.Elapsed.TotalMilliseconds;
        if (decorridoMs < orcamentoMs)
        {
            return;
        }

        double descansoMs = Math.Max(1.0, DuracaoJanelaMs - decorridoMs);
        await Task.Delay(TimeSpan.FromMilliseconds(descansoMs), cancelamento);
        janela.Restart();
    }
}
