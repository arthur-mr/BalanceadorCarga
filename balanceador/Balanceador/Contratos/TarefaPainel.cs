namespace Balanceador.Contratos;

public sealed record TarefaPainel(
    long Numero,
    string Nivel,
    string Estado,
    double Progresso);
