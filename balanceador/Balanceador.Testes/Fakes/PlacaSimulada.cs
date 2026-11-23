using Balanceador.Contratos;
using Balanceador.Interfaces;
using Balanceador.Servicos;

namespace Balanceador.Testes.Fakes;

internal sealed class PlacaSimulada : IPlaca
{
    public string Id => "A";
    public Simulador Simulador { get; } = new("A");
    public bool PerderConfirmacao { get; set; }

    public PlacaSimulada() => Simulador.ConfigurarFalha(false, 0);

    public Task<SaudePlaca> ConsultarSaude(CancellationToken cancelamento) =>
        Task.FromResult(Simulador.ConsultarSaude());

    public Task<ResultadoTrabalho> Enviar(string id, SolicitacaoTrabalho solicitacao, int vaga, CancellationToken cancelamento) =>
        Task.FromResult(Simulador.Enviar(id, solicitacao, vaga));

    public Task<ResultadoTrabalho> ConsultarTrabalho(string id, CancellationToken cancelamento) =>
        Task.FromResult(Simulador.ConsultarTrabalho(id));

    public Task Cancelar(string id, CancellationToken cancelamento)
    {
        Simulador.Cancelar(id);
        return Task.CompletedTask;
    }

    public Task<PublicacaoBloco?> ConsultarBloco(string id, CancellationToken cancelamento) =>
        Task.FromResult(Simulador.ConsultarBloco(id));

    public Task AvancarBloco(string id, int publicacao, BlocoImagem bloco, CancellationToken cancelamento)
    {
        Simulador.AvancarBloco(id, publicacao, bloco);
        if (PerderConfirmacao)
        {
            PerderConfirmacao = false;
            throw new IOException("Confirmação perdida.");
        }

        return Task.CompletedTask;
    }
}
