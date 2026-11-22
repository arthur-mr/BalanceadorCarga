namespace Balanceador.Contratos;

public sealed record PerfilRenderizacao(string Nivel, int Amostras, int Intensidade)
{
    public static readonly PerfilRenderizacao[] Todos =
    [
        new("leve", 8, 25),
        new("medio", 32, 55),
        new("pesado", 96, 85)
    ];

    public static PerfilRenderizacao Obter(string nivel) =>
        Todos.FirstOrDefault(perfil => perfil.Nivel == nivel)
        ?? throw new ArgumentException("Nível inválido. Use leve, medio ou pesado.");
}
