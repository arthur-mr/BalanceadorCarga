namespace Balanceador.Contratos;

public sealed record SaudeVaga(
    int Indice,
    bool Ocupado,
    string? TrabalhoId,
    double Progresso);
