namespace Balanceador.Contratos;

public sealed record SaudePlaca(
    int Versao,
    string No,
    double CpuTotal,
    bool CpuValida,
    long IdadeAmostraMs,
    SaudeNucleo[] Nucleos,
    long TrabalhosConcluidos,
    long TempoLigadoMs,
    long MemoriaLivre,
    int Rssi,
    SaudeVaga[]? Vagas = null);
