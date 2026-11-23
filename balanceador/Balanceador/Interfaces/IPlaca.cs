using Balanceador.Contratos;

namespace Balanceador.Interfaces;

public interface IPlaca
{
    string Id { get; }
    Task<SaudePlaca> ConsultarSaude(CancellationToken cancelamento);
    Task<ResultadoTrabalho> Enviar(string id, SolicitacaoTrabalho solicitacao, int vaga, CancellationToken cancelamento);
    Task<ResultadoTrabalho> ConsultarTrabalho(string id, CancellationToken cancelamento);
    Task Cancelar(string id, CancellationToken cancelamento);
    Task<PublicacaoBloco?> ConsultarBloco(string id, CancellationToken cancelamento);
    Task AvancarBloco(string id, int publicacao, BlocoImagem bloco, CancellationToken cancelamento);
}
