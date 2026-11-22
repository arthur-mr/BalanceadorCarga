namespace Balanceador.Contratos;

public sealed record FalhaSimulador(bool Offline, int AtrasoMs = 250, double[]? CpuPorNucleo = null);
