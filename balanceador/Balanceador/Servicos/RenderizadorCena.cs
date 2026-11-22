using System.Numerics;
using Balanceador.Contratos;

namespace Balanceador.Servicos;

public static class RenderizadorCena
{
    private const float AlturaChao = -0.85f;
    private const float DistanciaMinimaIntersecao = 0.001f;
    private const float AfastamentoSombra = 0.003f;
    private const int QuadrosPorVoltaLuz = 36;
    private const int CanaisRgb = 3;

    private readonly record struct Esfera(Vector3 Centro, float Raio, Vector3 Cor);

    private static readonly Esfera[] esferas =
    [
        new(new(-0.85f, -0.15f, 0), 0.7f, new(0.95f, 0.25f, 0.08f)),
        new(new(0.75f, -0.3f, 0.5f), 0.55f, new(0.08f, 0.38f, 0.95f)),
        new(new(0.3f, 0.65f, 1), 0.4f, new(0.25f, 0.85f, 0.35f))
    ];

    private static readonly Vector3 camera = new(0, 0.5f, -4);

    private static float Sortear(ref uint estado)
    {
        estado = unchecked(estado * 1664525u + 1013904223u);
        return (estado >> 8) / 16777216f;
    }

    private static float Interceptar(Vector3 origem, Vector3 direcao, Esfera esfera)
    {
        Vector3 deslocamento = origem - esfera.Centro;
        float projecao = Vector3.Dot(deslocamento, direcao);
        float discriminante = projecao * projecao - Vector3.Dot(deslocamento, deslocamento) + esfera.Raio * esfera.Raio;

        if (discriminante < 0)
        {
            return float.PositiveInfinity;
        }

        float distancia = -projecao - MathF.Sqrt(discriminante);
        return distancia > DistanciaMinimaIntersecao ? distancia : float.PositiveInfinity;
    }

    private static Vector3 Iluminar(Vector3 direcao, Vector3 luz)
    {
        float distancia = float.PositiveInfinity;
        Esfera? esferaAtingida = null;

        foreach (var esfera in esferas)
        {
            float candidata = Interceptar(camera, direcao, esfera);
            if (candidata < distancia)
            {
                distancia = candidata;
                esferaAtingida = esfera;
            }
        }

        bool atingiuChao = false;
        float distanciaChao = direcao.Y < -DistanciaMinimaIntersecao
            ? (AlturaChao - camera.Y) / direcao.Y
            : float.PositiveInfinity;

        if (distanciaChao < distancia)
        {
            distancia = distanciaChao;
            atingiuChao = true;
        }

        if (float.IsPositiveInfinity(distancia))
        {
            float altura = Math.Clamp(direcao.Y + 0.5f, 0, 1);
            return new Vector3(0.12f, 0.19f, 0.3f) + new Vector3(0.25f, 0.3f, 0.35f) * altura;
        }

        Vector3 ponto = camera + direcao * distancia;
        Vector3 normal = atingiuChao
            ? Vector3.UnitY
            : Vector3.Normalize(ponto - esferaAtingida!.Value.Centro);

        Vector3 corSuperficie = atingiuChao
            ? CorDoChaoQuadriculado(ponto)
            : esferaAtingida!.Value.Cor;

        Vector3 deslocamentoLuz = luz - ponto;
        float distanciaLuz = deslocamentoLuz.Length();
        Vector3 direcaoLuz = deslocamentoLuz / distanciaLuz;

        bool emSombra = EstaEmSombra(ponto, normal, direcaoLuz, distanciaLuz);
        float difusa = emSombra ? 0 : MathF.Max(0, Vector3.Dot(normal, direcaoLuz));

        Vector3 reflexo = normal * (2 * Vector3.Dot(normal, direcaoLuz)) - direcaoLuz;
        float brilho = emSombra ? 0 : 0.3f * MathF.Pow(MathF.Max(0, Vector3.Dot(reflexo, -direcao)), 24);

        return Vector3.Clamp(corSuperficie * (0.13f + 0.85f * difusa) + new Vector3(brilho), Vector3.Zero, Vector3.One);
    }

    private static Vector3 CorDoChaoQuadriculado(Vector3 ponto)
    {
        bool quadradoClaro = (((int)MathF.Floor(ponto.X) + (int)MathF.Floor(ponto.Z)) & 1) == 0;
        float tonalidade = quadradoClaro ? 0.65f : 0.3f;
        return new Vector3(tonalidade);
    }

    private static bool EstaEmSombra(Vector3 ponto, Vector3 normal, Vector3 direcaoLuz, float distanciaLuz)
    {
        Vector3 origemSombra = ponto + normal * AfastamentoSombra;
        foreach (var esfera in esferas)
        {
            if (Interceptar(origemSombra, direcaoLuz, esfera) < distanciaLuz)
            {
                return true;
            }
        }
        return false;
    }

    public static Vector3 CalcularPixel(int coluna, int linha, SolicitacaoTrabalho solicitacao)
    {
        var perfil = PerfilRenderizacao.Obter(solicitacao.Nivel);
        uint semente = unchecked(
            2166136261u ^
            (uint)solicitacao.Quadro * 73856093u ^
            (uint)solicitacao.Passagem * 19349663u ^
            (uint)coluna * 83492791u ^
            (uint)linha * 2654435761u);

        float anguloLuz = solicitacao.Quadro % QuadrosPorVoltaLuz * (2 * MathF.PI / QuadrosPorVoltaLuz);
        var centroLuz = new Vector3(-2.4f * MathF.Cos(anguloLuz), 3.5f, -2 + 1.5f * MathF.Sin(anguloLuz));
        var corAcumulada = Vector3.Zero;

        for (int amostra = 0; amostra < perfil.Amostras; amostra++)
        {
            float horizontal = ((coluna + Sortear(ref semente)) / solicitacao.LarguraImagem - 0.5f) * 1.25f;
            float vertical = (0.5f - (linha + Sortear(ref semente)) / solicitacao.AlturaImagem) * 0.94f - 0.08f;
            var direcao = Vector3.Normalize(new Vector3(horizontal, vertical, 1));

            float deslocamentoX = (Sortear(ref semente) - 0.5f) * 0.8f;
            float deslocamentoZ = (Sortear(ref semente) - 0.5f) * 0.8f;
            var pontoLuz = centroLuz + new Vector3(deslocamentoX, 0, deslocamentoZ);

            corAcumulada += Iluminar(direcao, pontoLuz);
        }

        return corAcumulada / perfil.Amostras;
    }

    public static async Task<byte[]> Renderizar(
        SolicitacaoTrabalho solicitacao,
        Action<double> informarProgresso,
        CancellationToken cancelamento,
        ControleIntensidade? controle = null)
    {
        solicitacao.Validar();
        var perfil = PerfilRenderizacao.Obter(solicitacao.Nivel);
        var imagem = new byte[SolicitacaoTrabalho.BytesBloco];
        controle ??= new(perfil.Intensidade);

        for (int linha = 0; linha < SolicitacaoTrabalho.TamanhoBloco; linha++)
        {
            for (int coluna = 0; coluna < SolicitacaoTrabalho.TamanhoBloco; coluna++)
            {
                cancelamento.ThrowIfCancellationRequested();
                Vector3 cor = CalcularPixel(solicitacao.Coluna + coluna, solicitacao.Linha + linha, solicitacao);

                int posicao = (linha * SolicitacaoTrabalho.TamanhoBloco + coluna) * CanaisRgb;
                imagem[posicao] = (byte)(cor.X * 255);
                imagem[posicao + 1] = (byte)(cor.Y * 255);
                imagem[posicao + 2] = (byte)(cor.Z * 255);

                await controle.Aguardar(cancelamento);
            }

            informarProgresso(100.0 * (linha + 1) / SolicitacaoTrabalho.TamanhoBloco);
        }

        return imagem;
    }

    public static void IntegrarBlocoRgb(
        byte[] pixelsBloco,
        int colunaBloco,
        int linhaBloco,
        int larguraImagem,
        int passagem,
        int[] somasAcumuladas,
        byte[] imagemDestino)
    {
        int quantidadePassagens = passagem + 1;

        for (int linha = 0; linha < SolicitacaoTrabalho.TamanhoBloco; linha++)
        {
            for (int coluna = 0; coluna < SolicitacaoTrabalho.TamanhoBloco; coluna++)
            {
                int indiceOrigem = (linha * SolicitacaoTrabalho.TamanhoBloco + coluna) * CanaisRgb;
                int indiceDestino = ((linhaBloco + linha) * larguraImagem + colunaBloco + coluna) * CanaisRgb;

                for (int canal = 0; canal < CanaisRgb; canal++)
                {
                    somasAcumuladas[indiceDestino + canal] += pixelsBloco[indiceOrigem + canal];
                    imagemDestino[indiceDestino + canal] = (byte)(somasAcumuladas[indiceDestino + canal] / quantidadePassagens);
                }
            }
        }
    }
}
