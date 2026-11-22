namespace Balanceador.Contratos;

public sealed record SolicitacaoTrabalho(
    string Nivel,
    int Quadro = 0,
    int Passagem = 0,
    int Coluna = 0,
    int Linha = 0,
    int LarguraImagem = 128,
    int AlturaImagem = 96,
    string Tipo = "bloco",
    int DuracaoSegundos = 0)
{
    public const int LarguraCena = 128;
    public const int AlturaCena = 96;
    public const int TamanhoBloco = 16;
    public const int Passagens = 4;
    public const int BytesBloco = TamanhoBloco * TamanhoBloco * 3;
    public const int QuadroMaximo = 1_000_000;
    public const int DuracaoMinimaCargaSegundos = 1;
    public const int DuracaoMaximaCargaSegundos = 120;

    public void Validar()
    {
        PerfilRenderizacao.Obter(Nivel);

        bool quadroValido = Quadro is >= 0 and <= QuadroMaximo;
        bool passagemValida = Passagem is >= 0 and < Passagens;
        bool resolucaoValida = ResolucaoValida(LarguraImagem, AlturaImagem);

        bool coordenadasValidas =
            Coluna >= 0 && Coluna < LarguraImagem &&
            Linha >= 0 && Linha < AlturaImagem &&
            Coluna % TamanhoBloco == 0 &&
            Linha % TamanhoBloco == 0;

        bool tipoEDuracaoValidos = Tipo switch
        {
            "bloco" => DuracaoSegundos == 0,
            "carga" => DuracaoSegundos is >= DuracaoMinimaCargaSegundos and <= DuracaoMaximaCargaSegundos,
            _ => false
        };

        if (!quadroValido || !passagemValida || !resolucaoValida || !coordenadasValidas || !tipoEDuracaoValidos)
        {
            throw new ArgumentException("Coordenadas, quadro ou passagem inválidos.");
        }
    }

    public static bool ResolucaoValida(int largura, int altura) =>
        largura is 128 or 256 or 384 or 512 &&
        altura == largura * 3 / 4;
}
