namespace Balanceador.Modelos;

public static class EstadosTrabalho
{
    public const string NaFila = "naFila";
    public const string Executando = "executando";
    public const string Cancelando = "cancelando";
    public const string Concluido = "concluido";
    public const string Cancelado = "cancelado";
    public const string Falhou = "falhou";

    public static bool EhTerminal(string estado) => estado is Concluido or Cancelado or Falhou;
}
