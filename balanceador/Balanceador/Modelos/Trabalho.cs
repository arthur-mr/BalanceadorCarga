using Balanceador.Contratos;

namespace Balanceador.Modelos;

public sealed class Trabalho
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required long Numero { get; init; }
    public required SolicitacaoTrabalho Solicitacao { get; set; }
    public string Estado { get; set; } = EstadosTrabalho.NaFila;
    public string? Placa { get; set; }
    public int? Nucleo { get; set; }
    public int? Vaga { get; set; }
    public int Tentativa { get; init; } = 1;
    public bool Substituido { get; set; }
    public string? Erro { get; set; }
    public ResultadoTrabalho? Resultado { get; set; }
    public required DateTimeOffset CriadoEm { get; init; }
    public DateTimeOffset? IniciadoEm { get; set; }
}
