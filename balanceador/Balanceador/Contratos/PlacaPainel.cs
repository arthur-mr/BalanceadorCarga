namespace Balanceador.Contratos;

public sealed record PlacaPainel(
    string Id,
    string Nome,
    bool Online,
    bool Habilitada,
    double? Cpu,
    string? Erro,
    TarefaPainel[] Tarefas);
