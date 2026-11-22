using Balanceador.Contratos;

namespace Balanceador.Servicos;

public sealed class ImagemProgressiva
{
    public const int Largura = 512;
    public const int Altura = 384;
    public const int Passagens = 4;

    private const int TamanhoBloco = SolicitacaoTrabalho.TamanhoBloco;
    private const int ColunasGrade = Largura / TamanhoBloco;
    private const int LinhasGrade = Altura / TamanhoBloco;
    private const int TotalPosicoes = ColunasGrade * LinhasGrade;
    private const int TotalBytesImagem = Largura * Altura * 3;
    private const int QuadroMaximo = SolicitacaoTrabalho.QuadroMaximo;

    private readonly object trava = new();
    private readonly Dictionary<string, BlocoImagem> reservas = [];
    private readonly bool[] concluidos = new bool[TotalPosicoes];
    private readonly int[] ordemDoCentro = CriarOrdemDoCentroParaBordas();
    private readonly int[] somas = new int[TotalBytesImagem];
    private readonly byte[] pixels = new byte[TotalBytesImagem];

    private string sessao = Guid.NewGuid().ToString("N");
    private int quadro;
    private int passagem;
    private int quantidade;
    private int revisao;
    private string? aviso;

    public BlocoImagem? Reservar(string trabalho)
    {
        lock (trava)
        {
            if (reservas.TryGetValue(trabalho, out var existente))
            {
                return existente;
            }

            if (passagem >= Passagens)
            {
                return null;
            }

            var indicesOcupados = reservas.Values.Select(IndiceDoBloco).ToHashSet();
            foreach (int indice in ordemDoCentro)
            {
                if (concluidos[indice] || indicesOcupados.Contains(indice))
                {
                    continue;
                }

                var bloco = CriarBlocoDoIndice(quadro, passagem, indice);
                reservas.Add(trabalho, bloco);
                return bloco;
            }

            return null;
        }
    }

    public bool Integrar(string trabalho, BlocoImagem bloco, byte[] dados)
    {
        if (dados.Length != SolicitacaoTrabalho.BytesBloco)
        {
            throw new IOException("Bloco RGB incompleto.");
        }

        lock (trava)
        {
            bool blocoEhDaRodadaAtual = bloco.Quadro == quadro && bloco.Passagem == passagem;
            if (!blocoEhDaRodadaAtual || !reservas.TryGetValue(trabalho, out var esperado) || esperado != bloco)
            {
                return false;
            }

            int indice = IndiceDoBloco(bloco);
            if (concluidos[indice])
            {
                return false;
            }

            RenderizadorCena.IntegrarBlocoRgb(dados, bloco.Coluna, bloco.Linha, Largura, passagem, somas, pixels);

            concluidos[indice] = true;
            reservas.Remove(trabalho);
            quantidade++;
            revisao++;
            aviso = null;

            if (concluidos.All(valor => valor))
            {
                passagem++;
                Array.Clear(concluidos);
            }

            return true;
        }
    }

    public void Liberar(string trabalho)
    {
        lock (trava)
        {
            reservas.Remove(trabalho);
        }
    }

    public void InformarFalha(string mensagem)
    {
        lock (trava)
        {
            aviso = mensagem;
        }
    }

    public ProgressoImagem ObterEstado(string? mensagem = null)
    {
        lock (trava)
        {
            return new(sessao, quadro, Largura, Altura, revisao, quantidade, TotalPosicoes * Passagens, mensagem ?? aviso);
        }
    }

    public (byte[] Pixels, int Revisao)? ObterPixels(string identificador)
    {
        lock (trava)
        {
            return identificador == sessao ? (pixels.ToArray(), revisao) : null;
        }
    }

    public void Resetar()
    {
        lock (trava)
        {
            sessao = Guid.NewGuid().ToString("N");
            quadro = (quadro + 1) % (QuadroMaximo + 1);
            passagem = 0;
            quantidade = 0;
            revisao = 0;
            aviso = null;
            reservas.Clear();
            Array.Clear(concluidos);
            Array.Clear(somas);
            Array.Clear(pixels);
        }
    }

    private static int[] CriarOrdemDoCentroParaBordas()
    {
        double centroColuna = (ColunasGrade - 1) / 2.0;
        double centroLinha = (LinhasGrade - 1) / 2.0;

        return Enumerable.Range(0, TotalPosicoes)
            .OrderBy(indice =>
            {
                double distanciaColuna = indice % ColunasGrade - centroColuna;
                double distanciaLinha = indice / ColunasGrade - centroLinha;
                return distanciaColuna * distanciaColuna + distanciaLinha * distanciaLinha;
            })
            .ToArray();
    }

    private static int IndiceDoBloco(BlocoImagem bloco) =>
        bloco.Linha / TamanhoBloco * ColunasGrade + bloco.Coluna / TamanhoBloco;

    private static BlocoImagem CriarBlocoDoIndice(int quadro, int passagem, int indice) =>
        new(quadro, passagem, indice % ColunasGrade * TamanhoBloco, indice / ColunasGrade * TamanhoBloco);
}
