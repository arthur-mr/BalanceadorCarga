namespace Balanceador.Contratos;

public sealed record ResultadoTrabalho(
    string Id,
    string Estado,
    string Nivel,
    int Nucleo,
    double Progresso,
    int Largura,
    int Altura,
    long TempoMs,
    int Quadro = 0,
    int Passagem = 0,
    int Coluna = 0,
    int Linha = 0,
    int LarguraImagem = 128,
    int AlturaImagem = 96,
    string Tipo = "bloco",
    int DuracaoSegundos = 0,
    int? Vaga = null);
