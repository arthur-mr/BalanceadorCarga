namespace Balanceador.Servicos;

public sealed class TrabalhadorBalanceamento(
    BalanceadorDeCarga balanceador,
    ILogger<TrabalhadorBalanceamento> registro) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancelamento)
    {
        using var temporizador = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                await balanceador.ExecutarCiclo(cancelamento);
            }
            while (await temporizador.WaitForNextTickAsync(cancelamento));
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested)
        {
        }
        catch (Exception erro)
        {
            registro.LogCritical(erro, "Falha no balanceador.");
            throw;
        }
    }
}
