namespace Balanceador.Contratos;

public sealed record PublicacaoBloco(
    int Publicacao,
    int Quadro,
    int Passagem,
    int Coluna,
    int Linha,
    byte[] Pixels);
