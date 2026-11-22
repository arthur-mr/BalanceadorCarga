namespace Balanceador.Testes.Fakes;

internal sealed class RelogioTeste(DateTimeOffset? inicial = null) : TimeProvider
{
    private DateTimeOffset agora = inicial ?? DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => agora;

    public void Avancar(int milissegundos) => agora = agora.AddMilliseconds(milissegundos);
}
