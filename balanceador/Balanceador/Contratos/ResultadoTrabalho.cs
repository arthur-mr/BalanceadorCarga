namespace Balanceador.Contratos;

public sealed record ResultadoTrabalho(
    string Id,
    string Estado,
    string Nivel,
    int Nucleo,
    int Vaga,
    double Progresso,
    int Largura,
    int Altura,
    long TempoMs,
    int Quadro = 0,
    int Passagem = 0,
    int Coluna = 0,
    int Linha = 0,
    int LarguraImagem = 512,
    int AlturaImagem = 384,
    int DuracaoSegundos = 20);
