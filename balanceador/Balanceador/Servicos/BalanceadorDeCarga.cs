using Balanceador.Contratos;
using Balanceador.Interfaces;
using Balanceador.Modelos;

namespace Balanceador.Servicos;

public sealed class BalanceadorDeCarga(
    IEnumerable<IPlaca> transportes,
    TimeProvider? relogio = null,
    int intervaloAtribuicaoMs = 0)
{
    public const int DuracaoTarefaSegundos = 20;

    private const int LimiteFila = 100;
    private const int MaximoTentativas = 3;
    private const int MaximoTrabalhosHistorico = 200;
    private const int IdadeMaximaTelemetriaMs = 3000;
    private const double LimiteCpu = 85.0;

    private readonly object trava = new();
    private readonly SemaphoreSlim travaCiclo = new(1);
    private readonly List<Trabalho> trabalhos = [];
    private readonly List<EstadoPlaca> placas = transportes.Select(transporte => new EstadoPlaca(transporte)).ToList();
    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;
    private readonly ImagemProgressiva imagemProgressiva = new();

    private DateTimeOffset? ultimaAtribuicao;
    private UltimaDecisaoPainel? ultimaDecisao;
    private long proximoNumero;

    public void ResetarImagem() => imagemProgressiva.Resetar();

    public (byte[] Pixels, int Revisao)? ObterPixelsImagem(string sessao) => imagemProgressiva.ObterPixels(sessao);

    public Trabalho AdicionarTarefa(string nivel)
    {
        var solicitacao = new SolicitacaoTrabalho(nivel, DuracaoSegundos: DuracaoTarefaSegundos);
        solicitacao.Validar();

        lock (trava)
        {
            if (ContarNaFila() >= LimiteFila)
            {
                throw new InvalidOperationException("A fila está cheia. Aguarde algumas tarefas terminarem.");
            }

            var trabalho = new Trabalho
            {
                Numero = ++proximoNumero,
                Solicitacao = solicitacao,
                CriadoEm = relogio.GetUtcNow()
            };
            trabalhos.Add(trabalho);
            return trabalho;
        }
    }

    public EstadoPainel ObterPainel()
    {
        lock (trava)
        {
            Trabalho[] trabalhosVisiveis = trabalhos
                .Where(trabalho => !trabalho.Substituido)
                .ToArray();

            TarefaPainel Resumir(Trabalho trabalho) =>
                new(trabalho.Numero, trabalho.Solicitacao.Nivel, trabalho.Estado, ObterProgresso(trabalho));

            TarefaPainel[] fila = trabalhosVisiveis
                .Where(trabalho =>
                    trabalho.Estado == EstadosTrabalho.NaFila ||
                    (trabalho.Estado == EstadosTrabalho.Falhou && trabalho.Tentativa < MaximoTentativas))
                .Select(trabalho => Resumir(trabalho) with { Estado = EstadosTrabalho.NaFila })
                .ToArray();

            Trabalho? falhaDefinitiva = trabalhosVisiveis
                .LastOrDefault(trabalho =>
                    trabalho.Estado == EstadosTrabalho.Falhou &&
                    trabalho.Tentativa >= MaximoTentativas);

            PlacaPainel[] placasPainel = placas
                .Select(placa => new PlacaPainel(
                    placa.Transporte.Id,
                    placa.Saude?.No ?? placa.Transporte.Id,
                    placa.Online,
                    placa.Habilitada,
                    TelemetriaValida(placa) ? placa.Saude!.CpuTotal : null,
                    placa.Erro,
                    trabalhosVisiveis
                        .Where(trabalho =>
                            trabalho.Placa == placa.Transporte.Id &&
                            trabalho.Estado is EstadosTrabalho.Executando or EstadosTrabalho.Cancelando)
                        .OrderBy(trabalho => trabalho.Numero)
                        .Select(Resumir)
                        .ToArray()))
                .ToArray();

            string situacaoFila = fila.Length == 0
                ? "Nenhuma tarefa aguardando."
                : "As próximas tarefas serão distribuídas automaticamente.";

            string? avisoFalha = falhaDefinitiva is null
                ? null
                : $"A tarefa #{falhaDefinitiva.Numero} não pôde ser concluída após três tentativas. Verifique as placas e adicione novamente.";

            return new(
                placasPainel,
                fila,
                ultimaDecisao,
                situacaoFila,
                avisoFalha,
                imagemProgressiva.ObterEstado());
        }
    }

    public void AlterarParticipacao(string id, bool habilitada)
    {
        lock (trava)
        {
            EstadoPlaca placa = ObterPlaca(id);
            placa.Habilitada = habilitada;

            if (!habilitada)
            {
                InterromperTrabalhosDaPlacaRetirada(placa);
                RecuperarTarefas();
            }
        }
    }

    public bool Cancelar(string id)
    {
        lock (trava)
        {
            Trabalho? trabalho = trabalhos.Find(item => item.Id == id);
            if (trabalho is null)
            {
                return false;
            }

            if (trabalho.Estado == EstadosTrabalho.NaFila)
            {
                trabalho.Estado = EstadosTrabalho.Cancelado;
            }
            else if (trabalho.Estado == EstadosTrabalho.Executando)
            {
                trabalho.Estado = EstadosTrabalho.Cancelando;
            }

            return true;
        }
    }

    public async Task ExecutarCiclo(CancellationToken cancelamento)
    {
        await travaCiclo.WaitAsync(cancelamento);
        try
        {
            await Task.WhenAll(placas.Select(placa => AtualizarPlaca(placa, cancelamento)));

            lock (trava)
            {
                RecuperarTarefas();
            }

            await DistribuirTrabalhosPendentesAsync(cancelamento);

            lock (trava)
            {
                LimparTrabalhosTerminaisAntigos();
            }
        }
        finally
        {
            travaCiclo.Release();
        }
    }

    private int ContarNaFila() => trabalhos.Count(trabalho => trabalho.Estado == EstadosTrabalho.NaFila);

    private EstadoPlaca ObterPlaca(string id) =>
        placas.Find(placa => placa.Transporte.Id == id)
        ?? throw new KeyNotFoundException("Placa desconhecida.");

    private void InterromperTrabalhosDaPlacaRetirada(EstadoPlaca placa)
    {
        foreach (var trabalho in placa.Reservas.OfType<Trabalho>().Where(trabalho =>
            !EstadosTrabalho.EhTerminal(trabalho.Estado)))
        {
            trabalho.Estado = EstadosTrabalho.Falhou;
            trabalho.Erro = "Placa retirada do balanceamento.";
        }
    }

    private void RecuperarTarefas()
    {
        Trabalho[] recuperaveis = trabalhos
            .Where(trabalho =>
                !trabalho.Substituido &&
                trabalho.Estado == EstadosTrabalho.Falhou &&
                trabalho.Tentativa < MaximoTentativas)
            .ToArray();

        foreach (var anterior in recuperaveis)
        {
            if (ContarNaFila() >= LimiteFila)
            {
                break;
            }

            imagemProgressiva.Liberar(anterior.Id);
            anterior.Substituido = true;
            trabalhos.Add(new Trabalho
            {
                Numero = anterior.Numero,
                Solicitacao = anterior.Solicitacao,
                Tentativa = anterior.Tentativa + 1,
                CriadoEm = relogio.GetUtcNow()
            });
        }
    }

    private double ObterProgresso(Trabalho trabalho)
    {
        if (trabalho.Resultado is not null)
        {
            return trabalho.Resultado.Progresso;
        }

        EstadoPlaca? placa = placas.Find(candidata => candidata.Transporte.Id == trabalho.Placa);
        if (trabalho.Vaga is not int vaga || placa?.Saude is null)
        {
            return 0;
        }

        SaudeVaga? saude = placa.ObterVagas().ElementAtOrDefault(vaga);
        return saude?.TrabalhoId == trabalho.Id ? saude.Progresso : 0;
    }

    private bool TelemetriaValida(EstadoPlaca placa) =>
        placa.Online &&
        placa.Saude is { CpuValida: true, IdadeAmostraMs: >= 0 } saude &&
        saude.IdadeAmostraMs + (relogio.GetUtcNow() - placa.AtualizadoEm).TotalMilliseconds <= IdadeMaximaTelemetriaMs;

    private bool PlacaElegivel(EstadoPlaca placa) =>
        placa.Habilitada &&
        TelemetriaValida(placa) &&
        placa.Saude!.CpuTotal < LimiteCpu &&
        placa.PossuiVagaLivre();

    private bool AguardandoIntervalo() =>
        intervaloAtribuicaoMs > 0 &&
        ultimaAtribuicao is { } ultima &&
        relogio.GetUtcNow() - ultima < TimeSpan.FromMilliseconds(intervaloAtribuicaoMs);

    private static IEnumerable<int> ObterIndicesVagasLivres(EstadoPlaca placa)
    {
        SaudeVaga[] vagas = placa.ObterVagas();
        for (int indice = 0; indice < vagas.Length; indice++)
        {
            if (!placa.VagaOcupada(indice, vagas))
            {
                yield return indice;
            }
        }
    }

    private (EstadoPlaca Placa, int Indice, EstadoPlaca[] Disponiveis)? EscolherVaga()
    {
        EstadoPlaca[] disponiveis = placas.Where(PlacaElegivel).ToArray();
        if (disponiveis.Length == 0)
        {
            return null;
        }

        var vagaEscolhida = disponiveis
            .SelectMany(placa => ObterIndicesVagasLivres(placa).Select(indice => (Placa: placa, Indice: indice)))
            .OrderBy(vaga => vaga.Placa.Saude!.CpuTotal)
            .ThenBy(vaga => vaga.Placa.Saude!.Nucleos[vaga.Indice % 2].Cpu)
            .ThenBy(vaga => vaga.Placa.Atribuidos.Sum())
            .ThenBy(vaga => vaga.Placa.Transporte.Id, StringComparer.Ordinal)
            .ThenBy(vaga => vaga.Indice)
            .First();

        return (vagaEscolhida.Placa, vagaEscolhida.Indice, disponiveis);
    }

    private async Task DistribuirTrabalhosPendentesAsync(CancellationToken cancelamento)
    {
        List<(Trabalho Trabalho, EstadoPlaca Placa, int IndiceVaga)> envios = [];

        lock (trava)
        {
            foreach (var candidato in trabalhos.Where(trabalho => trabalho.Estado == EstadosTrabalho.NaFila))
            {
                if (AguardandoIntervalo())
                {
                    break;
                }

                var escolha = EscolherVaga();
                if (escolha is null)
                {
                    break;
                }

                PrepararEnvioTrabalho(candidato, escolha.Value.Placa, escolha.Value.Indice, escolha.Value.Disponiveis);
                envios.Add((candidato, escolha.Value.Placa, escolha.Value.Indice));
            }
        }

        if (envios.Count == 0)
        {
            return;
        }

        await Task.WhenAll(envios.Select(envio =>
            EnviarTrabalhoParaPlacaAsync(envio.Trabalho, envio.Placa, envio.IndiceVaga, cancelamento)));
    }

    private void PrepararEnvioTrabalho(
        Trabalho trabalho,
        EstadoPlaca placa,
        int indiceVaga,
        EstadoPlaca[] disponiveis)
    {
        BlocoImagem bloco = imagemProgressiva.Reservar(trabalho.Id)
            ?? new(imagemProgressiva.ObterEstado().Quadro, 0, 0, 0);

        trabalho.Solicitacao = trabalho.Solicitacao with
        {
            Quadro = bloco.Quadro,
            Passagem = bloco.Passagem,
            Coluna = bloco.Coluna,
            Linha = bloco.Linha,
            LarguraImagem = ImagemProgressiva.Largura,
            AlturaImagem = ImagemProgressiva.Altura
        };

        ultimaDecisao = CriarUltimaDecisao(trabalho, placa, disponiveis);
        trabalho.Estado = EstadosTrabalho.Executando;
        trabalho.Placa = placa.Transporte.Id;
        trabalho.Nucleo = indiceVaga % 2;
        trabalho.Vaga = indiceVaga;
        trabalho.IniciadoEm = relogio.GetUtcNow();

        ultimaAtribuicao = relogio.GetUtcNow();
        placa.Reservas[indiceVaga] = trabalho;
        placa.Atribuidos[indiceVaga]++;
    }

    private static UltimaDecisaoPainel CriarUltimaDecisao(
        Trabalho trabalho,
        EstadoPlaca escolhida,
        EstadoPlaca[] disponiveis)
    {
        string nome = escolhida.Saude?.No ?? escolhida.Transporte.Id;
        double cpuEscolhida = escolhida.Saude!.CpuTotal;

        string motivo;
        if (disponiveis.Length == 1)
        {
            motivo = "era a única placa disponível";
        }
        else if (disponiveis.All(placa => Math.Abs(placa.Saude!.CpuTotal - cpuEscolhida) < 0.5))
        {
            motivo = "as placas tinham uso de CPU semelhante; a escolha foi equilibrada";
        }
        else
        {
            motivo = "estava com menor uso de CPU";
        }

        return new(
            trabalho.Numero,
            trabalho.Solicitacao.Nivel,
            nome,
            $"Tarefa #{trabalho.Numero} enviada para {nome}: {motivo}.");
    }

    private async Task EnviarTrabalhoParaPlacaAsync(
        Trabalho trabalho,
        EstadoPlaca placa,
        int indiceVaga,
        CancellationToken cancelamento)
    {
        try
        {
            ResultadoTrabalho resultado = await placa.Transporte.Enviar(
                trabalho.Id,
                trabalho.Solicitacao,
                indiceVaga,
                cancelamento);
            ValidarResultado(resultado, trabalho);
        }
        catch (Exception erro) when (erro is not OperationCanceledException || !cancelamento.IsCancellationRequested)
        {
            lock (trava)
            {
                trabalho.Estado = EstadosTrabalho.Falhou;
                trabalho.Erro = $"Aceitação incerta: {erro.Message}";
                placa.Erro = trabalho.Erro;
                placa.Online = false;
            }

            await TentarCancelar(placa, trabalho.Id, cancelamento);
        }
    }

    private void LimparTrabalhosTerminaisAntigos()
    {
        int excesso = trabalhos.Count(trabalho => EstadosTrabalho.EhTerminal(trabalho.Estado)) - MaximoTrabalhosHistorico;
        if (excesso <= 0)
        {
            return;
        }

        Trabalho[] removiveis = trabalhos
            .Where(trabalho =>
                EstadosTrabalho.EhTerminal(trabalho.Estado) &&
                placas.All(placa => !placa.Reservas.Contains(trabalho)))
            .Take(excesso)
            .ToArray();

        foreach (var antigo in removiveis)
        {
            trabalhos.Remove(antigo);
        }
    }

    private static bool PercentualValido(double valor) => double.IsFinite(valor) && valor is >= 0 and <= 100;

    private static void ValidarSaude(SaudePlaca saude)
    {
        bool vagasInvalidas =
            saude.Vagas is not { Length: EstadoPlaca.MaximoVagas } ||
            saude.Vagas.Where((vaga, indice) => vaga.Indice != indice || !PercentualValido(vaga.Progresso)).Any();

        if (vagasInvalidas)
        {
            throw new IOException("Vagas inválidas.");
        }

        bool nucleosInvalidos =
            saude.Nucleos is not { Length: 2 } ||
            saude.Nucleos.Where((nucleo, indice) =>
                nucleo.Indice != indice ||
                !PercentualValido(nucleo.Cpu) ||
                !PercentualValido(nucleo.Progresso)).Any();

        if (nucleosInvalidos || !PercentualValido(saude.CpuTotal) || saude.IdadeAmostraMs < 0)
        {
            throw new IOException("Telemetria inválida.");
        }
    }

    private static void ValidarResultado(ResultadoTrabalho resultado, Trabalho trabalho)
    {
        SolicitacaoTrabalho solicitacao = trabalho.Solicitacao;

        bool identificacaoCorreta =
            resultado.Id == trabalho.Id &&
            resultado.Nivel == solicitacao.Nivel &&
            resultado.Nucleo == trabalho.Nucleo &&
            resultado.Vaga == trabalho.Vaga;

        bool parametrosCorretos =
            resultado.Largura == SolicitacaoTrabalho.TamanhoBloco &&
            resultado.Altura == SolicitacaoTrabalho.TamanhoBloco &&
            resultado.Quadro == solicitacao.Quadro &&
            resultado.Passagem == solicitacao.Passagem &&
            resultado.Coluna == solicitacao.Coluna &&
            resultado.Linha == solicitacao.Linha &&
            resultado.LarguraImagem == solicitacao.LarguraImagem &&
            resultado.AlturaImagem == solicitacao.AlturaImagem &&
            resultado.DuracaoSegundos == solicitacao.DuracaoSegundos;

        bool metricasValidas =
            PercentualValido(resultado.Progresso) &&
            resultado.TempoMs >= 0 &&
            resultado.Estado is
                EstadosTrabalho.Executando or
                EstadosTrabalho.Concluido or
                EstadosTrabalho.Cancelado or
                EstadosTrabalho.Falhou &&
            (resultado.Estado != EstadosTrabalho.Concluido || resultado.Progresso == 100);

        if (!identificacaoCorreta || !parametrosCorretos || !metricasValidas)
        {
            throw new IOException("Resposta incompatível com o trabalho.");
        }
    }

    private async Task AtualizarPlaca(EstadoPlaca placa, CancellationToken cancelamento)
    {
        try
        {
            SaudePlaca saude = await placa.Transporte.ConsultarSaude(cancelamento);
            ValidarSaude(saude);

            lock (trava)
            {
                placa.Saude = saude;
                placa.Online = true;
                placa.AtualizadoEm = relogio.GetUtcNow();
                placa.Erro = null;
            }

            SaudeVaga[] vagas = placa.ObterVagas();
            for (int indiceVaga = 0; indiceVaga < vagas.Length; indiceVaga++)
            {
                await AtualizarReservaComTratamentoAsync(placa, indiceVaga, vagas[indiceVaga], cancelamento);
            }
        }
        catch (Exception erro) when (erro is not OperationCanceledException || !cancelamento.IsCancellationRequested)
        {
            Trabalho[] reservas;
            lock (trava)
            {
                placa.Online = false;
                placa.Erro = erro.Message;
                reservas = placa.Reservas.OfType<Trabalho>().ToArray();

                foreach (var trabalho in reservas.Where(trabalho => !EstadosTrabalho.EhTerminal(trabalho.Estado)))
                {
                    trabalho.Estado = EstadosTrabalho.Falhou;
                    trabalho.Erro = erro.Message;
                }
            }

            foreach (var trabalho in reservas)
            {
                await TentarCancelar(placa, trabalho.Id, cancelamento);
            }
        }
    }

    private async Task AtualizarReservaComTratamentoAsync(
        EstadoPlaca placa,
        int indiceVaga,
        SaudeVaga saude,
        CancellationToken cancelamento)
    {
        try
        {
            await AtualizarReserva(placa, indiceVaga, saude, cancelamento);
        }
        catch (Exception erro) when (erro is not OperationCanceledException || !cancelamento.IsCancellationRequested)
        {
            Trabalho? trabalho;
            lock (trava)
            {
                trabalho = placa.Reservas[indiceVaga];
                if (trabalho is not null && !EstadosTrabalho.EhTerminal(trabalho.Estado))
                {
                    trabalho.Estado = EstadosTrabalho.Falhou;
                    trabalho.Erro = erro.Message;
                }
            }

            if (trabalho is not null)
            {
                await TentarCancelar(placa, trabalho.Id, cancelamento);
            }
        }
    }

    private async Task AtualizarReserva(EstadoPlaca placa, int indiceVaga, SaudeVaga saude, CancellationToken cancelamento)
    {
        Trabalho? trabalho;
        lock (trava)
        {
            trabalho = placa.Reservas[indiceVaga];
            if (trabalho is null)
            {
                return;
            }

            int prazoSegundos = trabalho.Solicitacao.DuracaoSegundos + 5;
            if (!EstadosTrabalho.EhTerminal(trabalho.Estado) &&
                relogio.GetUtcNow() - trabalho.IniciadoEm > TimeSpan.FromSeconds(prazoSegundos))
            {
                trabalho.Estado = EstadosTrabalho.Falhou;
                trabalho.Erro = "Timeout de execução.";
            }
        }

        if (trabalho.Estado == EstadosTrabalho.Cancelando || EstadosTrabalho.EhTerminal(trabalho.Estado))
        {
            if (saude.Ocupado && saude.TrabalhoId == trabalho.Id)
            {
                await TentarCancelar(placa, trabalho.Id, cancelamento);
            }

            if (EstadosTrabalho.EhTerminal(trabalho.Estado))
            {
                imagemProgressiva.Liberar(trabalho.Id);
                if (!saude.Ocupado || saude.TrabalhoId != trabalho.Id)
                {
                    lock (trava)
                    {
                        placa.Reservas[indiceVaga] = null;
                    }
                }
                return;
            }
        }

        if (saude.TrabalhoId != trabalho.Id)
        {
            lock (trava)
            {
                trabalho.Estado = EstadosTrabalho.Falhou;
                trabalho.Erro = "A placa perdeu o trabalho ou reiniciou.";
                imagemProgressiva.Liberar(trabalho.Id);
                placa.Reservas[indiceVaga] = null;
            }
            return;
        }

        ResultadoTrabalho resultado = await placa.Transporte.ConsultarTrabalho(trabalho.Id, cancelamento);
        ValidarResultado(resultado, trabalho);
        await ProcessarPublicacaoImagemAsync(placa, trabalho, resultado, cancelamento);

        lock (trava)
        {
            trabalho.Resultado = resultado;
            trabalho.Erro = null;

            if (EstadosTrabalho.EhTerminal(resultado.Estado))
            {
                trabalho.Estado = trabalho.Estado == EstadosTrabalho.Cancelando
                    ? EstadosTrabalho.Cancelado
                    : resultado.Estado;

                if (!saude.Ocupado)
                {
                    placa.Reservas[indiceVaga] = null;
                }
            }
        }
    }

    private async Task ProcessarPublicacaoImagemAsync(
        EstadoPlaca placa,
        Trabalho trabalho,
        ResultadoTrabalho resultado,
        CancellationToken cancelamento)
    {
        try
        {
            await AtualizarImagem(placa, trabalho, resultado, cancelamento);
        }
        catch (Exception erro) when (erro is not OperationCanceledException || !cancelamento.IsCancellationRequested)
        {
            imagemProgressiva.InformarFalha("Aguardando os próximos blocos das placas. A transferência será repetida.");
        }
        finally
        {
            if (EstadosTrabalho.EhTerminal(resultado.Estado))
            {
                imagemProgressiva.Liberar(trabalho.Id);
            }
        }
    }

    private async Task AtualizarImagem(
        EstadoPlaca placa,
        Trabalho trabalho,
        ResultadoTrabalho resultado,
        CancellationToken cancelamento)
    {
        BlocoImagem? esperado = imagemProgressiva.Reservar(trabalho.Id);
        if (esperado is null)
        {
            return;
        }

        PublicacaoBloco? publicacao = await placa.Transporte.ConsultarBloco(trabalho.Id, cancelamento);
        if (publicacao is null)
        {
            return;
        }

        var recebido = new BlocoImagem(publicacao.Quadro, publicacao.Passagem, publicacao.Coluna, publicacao.Linha);
        if (recebido == esperado)
        {
            if (!imagemProgressiva.Integrar(trabalho.Id, esperado, publicacao.Pixels))
            {
                return;
            }

            if (EstadosTrabalho.EhTerminal(resultado.Estado))
            {
                return;
            }

            esperado = imagemProgressiva.Reservar(trabalho.Id);
            if (esperado is null)
            {
                return;
            }
        }

        if (!EstadosTrabalho.EhTerminal(resultado.Estado))
        {
            await placa.Transporte.AvancarBloco(trabalho.Id, publicacao.Publicacao, esperado, cancelamento);
        }
    }

    private static async Task TentarCancelar(EstadoPlaca placa, string id, CancellationToken cancelamento)
    {
        try
        {
            await placa.Transporte.Cancelar(id, cancelamento);
        }
        catch (Exception erro) when (erro is not OperationCanceledException || !cancelamento.IsCancellationRequested)
        {
        }
    }
}
