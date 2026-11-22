using Balanceador.Contratos;
using Balanceador.Modelos;

namespace Balanceador.Servicos;

public sealed class CenaCompartilhada
{
    private const string EstadoRenderizando = "renderizando";
    private const string EstadoIntervalo = "intervalo";
    private const string EstadoParada = "parada";
    private const string EstadoConcluida = "concluida";
    private const string EstadoFalhou = "falhou";

    private const int MaximoTentativasBloco = 3;
    private const int IntervaloEntreQuadrosMs = 1500;

    private sealed class Bloco(int coluna, int linha)
    {
        public int Coluna { get; } = coluna;
        public int Linha { get; } = linha;
        public Trabalho? Trabalho { get; set; }
        public int PassagensConcluidas { get; set; }
        public int Tentativas { get; set; }
        public string? UltimaPlaca { get; set; }
    }

    private sealed class Contribuicao
    {
        public int Blocos { get; set; }
        public long TempoMs { get; set; }
    }

    private readonly Func<SolicitacaoTrabalho, Trabalho> enfileirar;
    private readonly Bloco[] blocos;
    private readonly Dictionary<string, Contribuicao> contribuicoes = [];
    private readonly int[] somas;
    private readonly byte[] imagem;
    private readonly int largura;
    private readonly int altura;
    private readonly Func<bool> podeEnfileirar;
    private readonly bool continuo;
    private readonly string nivel;
    private readonly TimeProvider relogio;

    private DateTimeOffset proximoQuadroEm;
    private int quadro;
    private int passagem;
    private int quadrosConcluidos;
    private int revisao;
    private string estado = EstadoRenderizando;
    private string? erro;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool Ativa => estado is EstadoRenderizando or EstadoIntervalo;
    public int Quadro => quadro;
    public int Revisao => revisao;

    public CenaCompartilhada(
        string nivel,
        bool continuo,
        Func<SolicitacaoTrabalho, Trabalho> enfileirar,
        TimeProvider? relogio = null,
        int largura = 128,
        int altura = 96,
        Func<bool>? podeEnfileirar = null)
    {
        PerfilRenderizacao.Obter(nivel);
        if (!SolicitacaoTrabalho.ResolucaoValida(largura, altura))
        {
            throw new ArgumentException("Resolução inválida.");
        }

        this.largura = largura;
        this.altura = altura;
        this.podeEnfileirar = podeEnfileirar ?? (() => true);
        this.nivel = nivel;
        this.continuo = continuo;
        this.enfileirar = enfileirar;
        this.relogio = relogio ?? TimeProvider.System;

        somas = new int[largura * altura * 3];
        imagem = new byte[somas.Length];

        int colunasGrade = largura / SolicitacaoTrabalho.TamanhoBloco;
        int linhasGrade = altura / SolicitacaoTrabalho.TamanhoBloco;
        blocos = Enumerable.Range(0, linhasGrade)
            .SelectMany(linha => Enumerable.Range(0, colunasGrade)
                .Select(coluna => new Bloco(
                    coluna * SolicitacaoTrabalho.TamanhoBloco,
                    linha * SolicitacaoTrabalho.TamanhoBloco)))
            .ToArray();

        PrepararPassagem();
    }

    public void Parar(Action<string> cancelar)
    {
        if (Ativa)
        {
            estado = EstadoParada;
        }

        foreach (var bloco in blocos)
        {
            if (bloco.Trabalho is { } trabalho && !EstadosTrabalho.EhTerminal(trabalho.Estado))
            {
                cancelar(trabalho.Id);
            }
        }
    }

    public void Atualizar(Action<string> cancelar, bool distribuicaoPausada = false)
    {
        if (estado == EstadoIntervalo && !TentarIniciarProximoQuadro(distribuicaoPausada))
        {
            return;
        }

        if (estado != EstadoRenderizando)
        {
            return;
        }

        AbastecerFila();

        if (!ProcessarBlocosEmAndamento(cancelar))
        {
            return;
        }

        AvancarPassagemOuConcluirQuadro();
    }

    public byte[] CopiarImagem() => imagem.ToArray();

    public object ObterEstado(Func<Trabalho, double> obterProgresso)
    {
        double somaProgresso = blocos.Sum(bloco =>
        {
            double progressoAtual = bloco.PassagensConcluidas <= passagem &&
                bloco.Trabalho?.Estado == EstadosTrabalho.Executando
                ? obterProgresso(bloco.Trabalho)
                : 0;
            return bloco.PassagensConcluidas * 100.0 + progressoAtual;
        });

        double progresso = somaProgresso / (blocos.Length * SolicitacaoTrabalho.Passagens);

        return new
        {
            id = Id,
            estado,
            erro,
            nivel,
            continuo,
            quadro,
            passagem,
            quadrosConcluidos,
            revisao,
            largura,
            altura,
            tamanhoBloco = SolicitacaoTrabalho.TamanhoBloco,
            passagens = SolicitacaoTrabalho.Passagens,
            progresso = Math.Clamp(progresso, 0, 100),
            blocosConcluidos = blocos.Sum(bloco => bloco.PassagensConcluidas),
            totalBlocos = blocos.Length * SolicitacaoTrabalho.Passagens,
            blocos = blocos.Select(bloco => new
            {
                bloco.Coluna,
                bloco.Linha,
                bloco.PassagensConcluidas,
                estado = bloco.Trabalho?.Estado,
                placa = bloco.Trabalho?.Placa ?? bloco.UltimaPlaca,
                nucleo = bloco.Trabalho?.Nucleo,
                progresso = bloco.Trabalho is null ? 0 : obterProgresso(bloco.Trabalho)
            }).ToArray(),
            contribuicoes = contribuicoes.OrderBy(par => par.Key).Select(par => new
            {
                placa = par.Key,
                blocos = par.Value.Blocos,
                tempoMs = par.Value.TempoMs
            }).ToArray()
        };
    }

    private void PrepararPassagem()
    {
        foreach (var bloco in blocos)
        {
            bloco.Tentativas = 1;
            bloco.Trabalho = null;
        }

        AbastecerFila();
    }

    private void AbastecerFila()
    {
        foreach (var bloco in blocos.Where(bloco => bloco.Trabalho is null))
        {
            if (!podeEnfileirar())
            {
                break;
            }

            bloco.Trabalho = enfileirar(new(nivel, quadro, passagem, bloco.Coluna, bloco.Linha, largura, altura));
        }
    }

    private bool TentarIniciarProximoQuadro(bool distribuicaoPausada)
    {
        if (distribuicaoPausada || relogio.GetUtcNow() < proximoQuadroEm)
        {
            return false;
        }

        quadro = (quadro + 1) % (SolicitacaoTrabalho.QuadroMaximo + 1);
        passagem = 0;
        Array.Clear(imagem);
        Array.Clear(somas);

        foreach (var bloco in blocos)
        {
            bloco.PassagensConcluidas = 0;
            bloco.UltimaPlaca = null;
        }

        revisao++;
        estado = EstadoRenderizando;
        PrepararPassagem();
        return true;
    }

    private bool ProcessarBlocosEmAndamento(Action<string> cancelar)
    {
        foreach (var bloco in blocos)
        {
            var trabalho = bloco.Trabalho;
            if (trabalho is null || bloco.PassagensConcluidas > passagem)
            {
                continue;
            }

            if (trabalho.Estado == EstadosTrabalho.Concluido && trabalho.Imagem is not null)
            {
                Integrar(bloco, trabalho);
                continue;
            }

            if (trabalho.Estado is not (EstadosTrabalho.Falhou or EstadosTrabalho.Cancelado))
            {
                continue;
            }

            if (bloco.Tentativas >= MaximoTentativasBloco)
            {
                erro = $"Não foi possível calcular o bloco ({bloco.Coluna}, {bloco.Linha}) após três tentativas. {trabalho.Erro}";
                Parar(cancelar);
                estado = EstadoFalhou;
                return false;
            }

            bloco.Tentativas++;
            bloco.Trabalho = null;
        }

        return true;
    }

    private void AvancarPassagemOuConcluirQuadro()
    {
        if (blocos.Any(bloco => bloco.PassagensConcluidas <= passagem))
        {
            return;
        }

        if (passagem + 1 < SolicitacaoTrabalho.Passagens)
        {
            passagem++;
            PrepararPassagem();
            return;
        }

        quadrosConcluidos++;
        estado = continuo ? EstadoIntervalo : EstadoConcluida;
        proximoQuadroEm = relogio.GetUtcNow().AddMilliseconds(IntervaloEntreQuadrosMs);
    }

    private void Integrar(Bloco bloco, Trabalho trabalho)
    {
        RenderizadorCena.IntegrarBlocoRgb(
            trabalho.Imagem!,
            bloco.Coluna,
            bloco.Linha,
            largura,
            passagem,
            somas,
            imagem);

        bloco.PassagensConcluidas++;
        bloco.UltimaPlaca = trabalho.Placa;
        revisao++;

        if (!contribuicoes.TryGetValue(trabalho.Placa!, out var contribuicao))
        {
            contribuicao = new Contribuicao();
            contribuicoes[trabalho.Placa!] = contribuicao;
        }

        contribuicao.Blocos++;
        contribuicao.TempoMs += trabalho.Resultado!.TempoMs;
    }
}
