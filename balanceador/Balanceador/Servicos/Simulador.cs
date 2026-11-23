using System.Diagnostics;
using Balanceador.Contratos;
using Balanceador.Modelos;

namespace Balanceador.Servicos;

public sealed class Simulador(string id)
{
    private const int QuantidadeVagas = 4;
    private const int QuantidadeNucleos = 2;
    private const int TimeoutExecucaoSegundos = 300;
    private const double CpuMaximaSimulada = 90;
    private const long MemoriaLivreSimulada = 180000;
    private const int RssiSimulado = -45;

    private sealed class Vaga
    {
        public ResultadoTrabalho? Resultado { get; set; }
        public SolicitacaoTrabalho? Solicitacao { get; set; }
        public CancellationTokenSource? Cancelamento { get; set; }
        public SolicitacaoTrabalho? BlocoAtual { get; set; }
        public PublicacaoBloco? Publicacao { get; set; }
        public int Sequencia { get; set; }
    }

    private readonly object trava = new();
    private readonly Stopwatch tempoLigado = Stopwatch.StartNew();
    private readonly Vaga[] vagas = Enumerable.Range(0, QuantidadeVagas).Select(_ => new Vaga()).ToArray();

    private bool offline;
    private int atrasoMs = 250;
    private long concluidos;
    private double[]? cpuForcada;

    public void ConfigurarFalha(bool indisponivel, int atraso, double[]? cpu = null)
    {
        if (atraso is < 0 or > 30000)
        {
            throw new ArgumentException("Atraso entre 0 e 30000 ms.");
        }

        if (cpu is not null && (cpu.Length != QuantidadeNucleos || cpu.Any(valor => !double.IsFinite(valor) || valor is < 0 or > 100)))
        {
            throw new ArgumentException("Informe dois percentuais de CPU entre 0 e 100.");
        }

        lock (trava)
        {
            offline = indisponivel;
            atrasoMs = atraso;
            cpuForcada = cpu?.ToArray();
        }
    }

    public SaudePlaca ConsultarSaude()
    {
        lock (trava)
        {
            VerificarOnline();

            SaudeNucleo[] nucleos = Enumerable.Range(0, QuantidadeNucleos)
                .Select(CalcularSaudeNucleo)
                .ToArray();

            SaudeVaga[] estadosVagas = vagas
                .Select((vaga, indice) => new SaudeVaga(
                    indice,
                    EstaOcupada(vaga),
                    vaga.Resultado?.Id,
                    vaga.Resultado?.Progresso ?? 0))
                .ToArray();

            return new(
                id,
                nucleos.Average(nucleo => nucleo.Cpu),
                true,
                0,
                nucleos,
                estadosVagas,
                concluidos,
                tempoLigado.ElapsedMilliseconds,
                MemoriaLivreSimulada,
                RssiSimulado);
        }
    }

    public ResultadoTrabalho Enviar(string identificador, SolicitacaoTrabalho solicitacao, int indiceVaga)
    {
        solicitacao.Validar();
        if (indiceVaga is < 0 or >= QuantidadeVagas || identificador.Length != 32 || !identificador.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Trabalho inválido.");
        }

        lock (trava)
        {
            VerificarOnline();

            Vaga? existente = vagas.FirstOrDefault(vaga => vaga.Resultado?.Id == identificador);
            if (existente is not null)
            {
                if (existente.Solicitacao != solicitacao || existente.Resultado!.Vaga != indiceVaga)
                {
                    throw new InvalidOperationException("ID reutilizado com parâmetros diferentes.");
                }
                return existente.Resultado!;
            }

            Vaga vaga = vagas[indiceVaga];
            if (EstaOcupada(vaga))
            {
                throw new InvalidOperationException("Vaga ocupada.");
            }

            vaga.Cancelamento?.Dispose();
            vaga.Cancelamento = new(TimeSpan.FromSeconds(TimeoutExecucaoSegundos));
            vaga.Solicitacao = solicitacao;
            vaga.BlocoAtual = solicitacao;
            vaga.Publicacao = null;
            vaga.Sequencia = 0;
            vaga.Resultado = new(
                identificador,
                EstadosTrabalho.Executando,
                solicitacao.Nivel,
                indiceVaga % QuantidadeNucleos,
                indiceVaga,
                0,
                SolicitacaoTrabalho.TamanhoBloco,
                SolicitacaoTrabalho.TamanhoBloco,
                0,
                solicitacao.Quadro,
                solicitacao.Passagem,
                solicitacao.Coluna,
                solicitacao.Linha,
                solicitacao.LarguraImagem,
                solicitacao.AlturaImagem,
                solicitacao.DuracaoSegundos);

            int atraso = atrasoMs;
            CancellationToken cancelamento = vaga.Cancelamento.Token;
            _ = Task.Run(() => Executar(vaga, solicitacao, atraso, cancelamento));
            return vaga.Resultado;
        }
    }

    public ResultadoTrabalho ConsultarTrabalho(string identificador)
    {
        lock (trava)
        {
            VerificarOnline();
            return Localizar(identificador).Resultado!;
        }
    }

    public PublicacaoBloco? ConsultarBloco(string identificador)
    {
        lock (trava)
        {
            VerificarOnline();
            return Localizar(identificador).Publicacao;
        }
    }

    public void AvancarBloco(string identificador, int publicacao, BlocoImagem bloco)
    {
        lock (trava)
        {
            VerificarOnline();

            Vaga vaga = Localizar(identificador);
            SolicitacaoTrabalho proxima = vaga.Solicitacao! with
            {
                Quadro = bloco.Quadro,
                Passagem = bloco.Passagem,
                Coluna = bloco.Coluna,
                Linha = bloco.Linha
            };
            proxima.Validar();

            if (vaga.Sequencia == publicacao + 1 && vaga.BlocoAtual == proxima)
            {
                return;
            }

            if (!EstaOcupada(vaga) || vaga.Sequencia != publicacao || vaga.Publicacao is null)
            {
                throw new InvalidOperationException("Publicação já substituída ou tarefa encerrada.");
            }

            vaga.Sequencia++;
            vaga.BlocoAtual = proxima;
            vaga.Publicacao = null;
        }
    }

    public void Cancelar(string identificador)
    {
        lock (trava)
        {
            VerificarOnline();
            Vaga vaga = Localizar(identificador);
            if (EstaOcupada(vaga))
            {
                vaga.Cancelamento!.Cancel();
            }
        }
    }

    private void VerificarOnline()
    {
        if (offline)
        {
            throw new IOException("Placa offline.");
        }
    }

    private static bool EstaOcupada(Vaga vaga) => vaga.Resultado?.Estado == EstadosTrabalho.Executando;

    private Vaga Localizar(string identificador) =>
        vagas.FirstOrDefault(vaga => vaga.Resultado?.Id == identificador)
        ?? throw new KeyNotFoundException("Trabalho desconhecido.");

    private SaudeNucleo CalcularSaudeNucleo(int indiceNucleo)
    {
        Vaga[] ativasNoNucleo = vagas
            .Where((vaga, posicao) => posicao % QuantidadeNucleos == indiceNucleo && EstaOcupada(vaga))
            .ToArray();

        double cpuBase = indiceNucleo == 0 ? 4 : 2;
        double somaIntensidades = ativasNoNucleo.Sum(vaga => PerfilRenderizacao.Obter(vaga.Resultado!.Nivel).Intensidade);
        double cpu = cpuForcada?[indiceNucleo] ?? Math.Min(CpuMaximaSimulada, cpuBase + somaIntensidades);

        ResultadoTrabalho? resultadoRepresentativo = ativasNoNucleo.FirstOrDefault()?.Resultado;
        return new SaudeNucleo(
            indiceNucleo,
            ativasNoNucleo.Length > 0,
            resultadoRepresentativo?.Id,
            resultadoRepresentativo?.Progresso ?? 0,
            cpu);
    }

    private async Task Executar(Vaga vaga, SolicitacaoTrabalho solicitacao, int atraso, CancellationToken cancelamento)
    {
        var relogio = Stopwatch.StartNew();
        string estado = EstadosTrabalho.Concluido;

        try
        {
            await Task.Delay(atraso, cancelamento);

            var tempoExecucao = Stopwatch.StartNew();
            var controle = new ControleIntensidade(PerfilRenderizacao.Obter(solicitacao.Nivel).Intensidade);

            do
            {
                SolicitacaoTrabalho atual;
                int sequencia;

                lock (trava)
                {
                    atual = vaga.BlocoAtual!;
                    sequencia = vaga.Sequencia;
                }

                byte[] imagem = await RenderizadorCena.Renderizar(
                    atual,
                    _ => AtualizarProgresso(vaga, solicitacao, tempoExecucao, relogio),
                    cancelamento,
                    controle);

                PublicarBlocoSeNecessario(vaga, atual, sequencia, imagem);
            }
            while (tempoExecucao.Elapsed.TotalSeconds < solicitacao.DuracaoSegundos);

            cancelamento.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            estado = EstadosTrabalho.Cancelado;
        }
        catch (Exception)
        {
            estado = EstadosTrabalho.Falhou;
        }

        lock (trava)
        {
            vaga.Resultado = vaga.Resultado! with
            {
                Estado = estado,
                TempoMs = relogio.ElapsedMilliseconds,
                Progresso = estado == EstadosTrabalho.Concluido ? 100 : vaga.Resultado.Progresso
            };

            if (estado == EstadosTrabalho.Concluido)
            {
                concluidos++;
            }
        }
    }

    private void AtualizarProgresso(
        Vaga vaga,
        SolicitacaoTrabalho solicitacao,
        Stopwatch tempoExecucao,
        Stopwatch relogio)
    {
        double progresso = Math.Min(99.9, 100.0 * tempoExecucao.Elapsed.TotalSeconds / solicitacao.DuracaoSegundos);

        lock (trava)
        {
            vaga.Resultado = vaga.Resultado! with
            {
                Progresso = progresso,
                TempoMs = relogio.ElapsedMilliseconds
            };
        }
    }

    private void PublicarBlocoSeNecessario(
        Vaga vaga,
        SolicitacaoTrabalho blocoCalculado,
        int sequenciaOriginal,
        byte[] imagem)
    {
        lock (trava)
        {
            if (vaga.Sequencia == sequenciaOriginal && vaga.Publicacao is null)
            {
                vaga.Publicacao = new(
                    sequenciaOriginal,
                    blocoCalculado.Quadro,
                    blocoCalculado.Passagem,
                    blocoCalculado.Coluna,
                    blocoCalculado.Linha,
                    imagem);
            }
        }
    }
}
