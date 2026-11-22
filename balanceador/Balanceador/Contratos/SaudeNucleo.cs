namespace Balanceador.Contratos;

public sealed record SaudeNucleo(
    int Indice,
    bool Ocupado,
    string? TrabalhoId,
    double Progresso,
    double Cpu);
