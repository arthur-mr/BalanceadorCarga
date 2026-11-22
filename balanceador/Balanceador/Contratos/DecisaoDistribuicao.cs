namespace Balanceador.Contratos;

public sealed record DecisaoDistribuicao(
    DateTimeOffset Instante,
    string Tipo,
    string Placa,
    string? Trabalho = null,
    string? Nivel = null,
    string? Categoria = null,
    int? Nucleo = null,
    string? Motivo = null,
    CandidataDistribuicao[]? Candidatas = null,
    long? Numero = null);
