namespace Balanceador.Contratos;

public sealed record ProgressoImagem(
    string Sessao,
    int Quadro,
    int Largura,
    int Altura,
    int Revisao,
    int BlocosConcluidos,
    int TotalBlocos,
    string? Aviso);
