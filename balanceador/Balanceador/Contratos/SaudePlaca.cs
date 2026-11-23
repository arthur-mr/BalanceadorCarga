namespace Balanceador.Contratos;

public sealed record SaudePlaca(
    string No,
    double CpuTotal,
    bool CpuValida,
    long IdadeAmostraMs,
    SaudeNucleo[] Nucleos,
    SaudeVaga[] Vagas,
    long TrabalhosConcluidos,
    long TempoLigadoMs,
    long MemoriaLivre,
    int Rssi);
