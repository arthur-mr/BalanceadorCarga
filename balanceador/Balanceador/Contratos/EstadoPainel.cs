namespace Balanceador.Contratos;

public sealed record EstadoPainel(
    PlacaPainel[] Placas,
    TarefaPainel[] Fila,
    UltimaDecisaoPainel? UltimaDecisao,
    string Situacao,
    string? Aviso,
    ProgressoImagem? Imagem = null);
