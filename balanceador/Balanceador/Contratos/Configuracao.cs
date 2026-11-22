namespace Balanceador.Contratos;

public sealed record Configuracao(bool Pausado, int TimeoutSegundos, int LimiteTarefas = 4, int IntervaloAtribuicaoMs = 1000);
