using Balanceador.Contratos;
using Balanceador.Interfaces;
using Balanceador.Modelos;

namespace Balanceador.Servicos;

public sealed class BalanceadorDeCarga(IEnumerable<IPlaca> transportes, TimeProvider? relogio = null)
{
    public const int DuracaoTarefaSegundos = 20;

    private const int LimiteFila = 100;
    private const int LimiteFilaCena = 64;
    private const int MaximoTentativasPainel = 3;
    private const int MaximoTrabalhosHistorico = 200;
    private const int MaximoDecisoesHistorico = 80;
    private const int MaximoDecisoesExpostas = 40;
    private const int MaximoAmostrasCpu = 60;
    private const int IdadeMaximaTelemetriaMs = 3000;
    private const double LimiteCpuAutomatica = 85.0;
    private const string SituacaoDisponivel = "Disponível";

    private readonly object trava = new();
    private readonly SemaphoreSlim travaCiclo = new(1);
    private readonly List<Trabalho> trabalhos = [];
    private readonly List<EstadoPlaca> placas = transportes.Select(transporte => new EstadoPlaca(transporte)).ToList();
    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;
    private readonly Queue<DecisaoDistribuicao> decisoes = new();
    private readonly ImagemProgressiva imagemProgressiva = new();

    private bool pausado;
    private int timeoutSegundos = 120;
    private CenaCompartilhada? cena;
    private int limiteTarefas = 4;
    private int intervaloAtribuicaoMs;
    private DateTimeOffset? ultimaAtribuicaoAutomatica;
    private long proximoNumero;

    public void ResetarImagem() => imagemProgressiva.Resetar();

    public (byte[] Pixels, int Revisao)? ObterPixelsImagem(string sessao) => imagemProgressiva.ObterPixels(sessao);

    public Trabalho AdicionarTarefa(string nivel)
    {
        var solicitacao = new SolicitacaoTrabalho(
            nivel,
            Coluna: 32,
            Linha: 48,
            Tipo: "carga",
            DuracaoSegundos: DuracaoTarefaSegundos);

        solicitacao.Validar();

        lock (trava)
        {
            if (ContarNaFila() >= LimiteFila)
            {
                throw new InvalidOperationException("A fila está cheia. Aguarde algumas tarefas terminarem.");
            }

            var trabalho = new Trabalho
            {
                Solicitacao = solicitacao,
                Numero = ++proximoNumero
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
                .Where(trabalho => trabalho.EhTarefaDoPainel && !trabalho.Substituido)
                .ToArray();

            TarefaPainel Resumir(Trabalho trabalho) =>
                new(trabalho.Numero!.Value, trabalho.Solicitacao.Nivel, trabalho.Estado, ObterProgresso(trabalho));

            TarefaPainel[] fila = trabalhosVisiveis
                .Where(trabalho =>
                    trabalho.Estado == EstadosTrabalho.NaFila ||
                    (trabalho.Estado == EstadosTrabalho.Falhou && trabalho.Tentativa < MaximoTentativasPainel))
                .Select(trabalho => Resumir(trabalho) with { Estado = EstadosTrabalho.NaFila })
                .ToArray();

            DecisaoDistribuicao? ultimaDecisao = decisoes
                .Reverse()
                .FirstOrDefault(decisao => decisao.Numero.HasValue && decisao.Tipo == "atribuicao");

            Trabalho? falhaDefinitiva = trabalhosVisiveis
                .LastOrDefault(trabalho =>
                    trabalho.Estado == EstadosTrabalho.Falhou &&
                    trabalho.Tentativa >= MaximoTentativasPainel);

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

            string? avisoFirmware = placas.Any(placa => placa.Habilitada && placa.Online && placa.Saude?.Versao == 4)
                ? "Atualize as placas para o firmware v5 para acompanhar a imagem."
                : null;

            return new(
                placasPainel,
                fila,
                ultimaDecisao is null ? null : DescreverDecisao(ultimaDecisao),
                situacaoFila,
                avisoFalha,
                imagemProgressiva.ObterEstado(avisoFirmware));
        }
    }

    public void IniciarCena(string nivel, bool continuo, int largura = 128, int altura = 96)
    {
        PerfilRenderizacao.Obter(nivel);
        if (!SolicitacaoTrabalho.ResolucaoValida(largura, altura))
        {
            throw new ArgumentException("Resolução inválida.");
        }

        lock (trava)
        {
            cena?.Parar(identificador => Cancelar(identificador));
            foreach (var trabalho in trabalhos.Where(trabalho =>
                trabalho.Solicitacao.Tipo == "bloco" && !EstadosTrabalho.EhTerminal(trabalho.Estado)))
            {
                Cancelar(trabalho.Id);
            }

            cena = new(
                nivel,
                continuo,
                EnfileirarInterno,
                largura: largura,
                altura: altura,
                podeEnfileirar: () => ContarNaFila() < LimiteFilaCena);
        }
    }

    public void PararCena()
    {
        lock (trava)
        {
            cena?.Parar(identificador => Cancelar(identificador));
        }
    }

    public (byte[] Imagem, int Revisao)? ObterImagemCena(string sessao, int quadro)
    {
        lock (trava)
        {
            return cena is not null && cena.Id == sessao && cena.Quadro == quadro
                ? (cena.CopiarImagem(), cena.Revisao)
                : null;
        }
    }

    public void Configurar(bool pausar, int timeout, int limite = 4, int intervaloMs = 0)
    {
        if (timeout is < 1 or > 3600)
        {
            throw new ArgumentException("Timeout entre 1 e 3600 segundos.");
        }

        if (limite is not (2 or 4))
        {
            throw new ArgumentException("Limite de tarefas: 2 ou 4 por placa.");
        }

        if (intervaloMs is not (0 or 250 or 500 or 1000 or 2000))
        {
            throw new ArgumentException("Intervalo de atribuição inválido.");
        }

        lock (trava)
        {
            pausado = pausar;
            timeoutSegundos = timeout;
            limiteTarefas = limite;
            intervaloAtribuicaoMs = intervaloMs;
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
            }

            Registrar(new(DateTimeOffset.UtcNow, habilitada ? "retorno" : "retirada", id));

            if (!habilitada)
            {
                RecuperarTarefas();
            }
        }
    }

    public void AdicionarCarga(string id, string nivel, int quantidade, int duracaoSegundos)
    {
        lock (trava)
        {
            EstadoPlaca placa = ObterPlaca(id);
            if (!placa.Habilitada)
            {
                throw new InvalidOperationException("Reinclua a placa antes de adicionar carga.");
            }

            var solicitacao = new SolicitacaoTrabalho(
                nivel,
                Coluna: 32,
                Linha: 48,
                Tipo: "carga",
                DuracaoSegundos: duracaoSegundos);

            solicitacao.Validar();

            if (quantidade is < 1 or > 4 || ContarNaFila() + quantidade > LimiteFila)
            {
                throw new ArgumentException("Quantidade entre 1 e 4; a fila deve ter espaço.");
            }

            for (int indice = 0; indice < quantidade; indice++)
            {
                trabalhos.Add(new Trabalho { Solicitacao = solicitacao, Destino = id });
            }
        }
    }

    public void IniciarCenarioAssimetrico(int duracaoSegundos)
    {
        lock (trava)
        {
            EstadoPlaca[] participantes = placas.Where(placa => placa.Habilitada).Take(2).ToArray();
            if (participantes.Length != 2)
            {
                throw new InvalidOperationException("Reinclua duas placas para executar o cenário.");
            }

            if (limiteTarefas != 4)
            {
                throw new InvalidOperationException("Escolha o limite de quatro tarefas por placa para este cenário.");
            }

            new SolicitacaoTrabalho("leve", Tipo: "carga", DuracaoSegundos: duracaoSegundos).Validar();
            if (ContarNaFila() > LimiteFila - 3)
            {
                throw new InvalidOperationException("Fila cheia.");
            }

            LimparCargas();
            AdicionarCarga(participantes[0].Transporte.Id, "leve", 2, duracaoSegundos);
            AdicionarCarga(participantes[1].Transporte.Id, "pesado", 1, duracaoSegundos);
        }
    }

    public void LimparCargas()
    {
        lock (trava)
        {
            foreach (var trabalho in trabalhos.Where(trabalho => trabalho.Solicitacao.Tipo == "carga"))
            {
                Cancelar(trabalho.Id);
            }
        }
    }

    public Trabalho Enfileirar(SolicitacaoTrabalho solicitacao)
    {
        lock (trava)
        {
            if (cena?.Ativa == true)
            {
                throw new InvalidOperationException("Use os controles da cena compartilhada.");
            }

            return EnfileirarInterno(solicitacao);
        }
    }

    public bool Cancelar(string id)
    {
        lock (trava)
        {
            Trabalho? trabalho = trabalhos.Find(trabalho => trabalho.Id == id);
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

    public (byte[] Imagem, ResultadoTrabalho Resultado)? ObterImagem(string id)
    {
        lock (trava)
        {
            Trabalho? trabalho = trabalhos.Find(trabalho => trabalho.Id == id);
            return trabalho is { Imagem: not null, Resultado: not null }
                ? (trabalho.Imagem, trabalho.Resultado)
                : null;
        }
    }

    public object ObterEstado()
    {
        lock (trava)
        {
            return new
            {
                instante = DateTimeOffset.UtcNow,
                pausado,
                timeoutSegundos,
                limiteTarefas,
                intervaloAtribuicaoMs,
                decisoes = decisoes.Reverse().Take(MaximoDecisoesExpostas).ToArray(),
                avaliacaoAtual = AvaliarFila(),
                situacaoDistribuicao = SituacaoDistribuicao(),
                cena = cena?.ObterEstado(ObterProgresso),
                placas = placas.Select(placa => new
                {
                    id = placa.Transporte.Id,
                    placa.Online,
                    placa.Habilitada,
                    placa.Erro,
                    placa.Saude,
                    capacidade = Math.Min(limiteTarefas, placa.ObterVagas().Length),
                    historicoCpu = placa.HistoricoCpu.ToArray(),
                    cpuDisponivel = TelemetriaValida(placa),
                    atribuidos = placa.Atribuidos.Sum(),
                    ocupados = placa.ContarReservasOuVagasOcupadas()
                }).ToArray(),
                trabalhos = trabalhos.AsEnumerable().Reverse().Select(trabalho => new
                {
                    trabalho.Id,
                    trabalho.Solicitacao,
                    trabalho.Estado,
                    trabalho.Placa,
                    trabalho.Nucleo,
                    trabalho.Vaga,
                    trabalho.Destino,
                    trabalho.Erro,
                    trabalho.Resultado,
                    trabalho.CriadoEm,
                    trabalho.IniciadoEm,
                    imagemDisponivel = trabalho.Imagem is not null,
                    progresso = ObterProgresso(trabalho)
                }).ToArray()
            };
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
                cena?.Atualizar(identificador => Cancelar(identificador), pausado);
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
        string idPlaca = placa.Transporte.Id;

        foreach (var pendente in trabalhos.Where(trabalho =>
            trabalho.Destino == idPlaca && trabalho.Estado == EstadosTrabalho.NaFila))
        {
            pendente.Estado = EstadosTrabalho.Cancelado;
        }

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
                trabalho.EhTarefaDoPainel &&
                !trabalho.Substituido &&
                trabalho.Estado == EstadosTrabalho.Falhou &&
                trabalho.Tentativa < MaximoTentativasPainel)
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
                Solicitacao = anterior.Solicitacao,
                Numero = anterior.Numero,
                Tentativa = anterior.Tentativa + 1
            });
        }
    }

    private UltimaDecisaoPainel DescreverDecisao(DecisaoDistribuicao decisao)
    {
        string nome = placas.Find(placa => placa.Transporte.Id == decisao.Placa)?.Saude?.No ?? decisao.Placa;
        CandidataDistribuicao[] candidatas = decisao.Candidatas ?? [];
        CandidataDistribuicao[] disponiveis = candidatas
            .Where(candidata => candidata.Situacao == SituacaoDisponivel)
            .ToArray();
        CandidataDistribuicao? escolhida = disponiveis.FirstOrDefault(candidata => candidata.Placa == decisao.Placa);

        string motivo = "estava com menor carga disponível";
        if (disponiveis.Length == 1)
        {
            motivo = "era a única placa disponível";
        }
        else if (escolhida?.Cpu is double cpu && disponiveis.All(candidata => candidata.Cpu >= cpu))
        {
            bool cpusSemelhantes = disponiveis.All(candidata => Math.Abs(candidata.Cpu!.Value - cpu) < 0.5);
            motivo = cpusSemelhantes
                ? "as placas tinham uso de CPU semelhante; a escolha foi equilibrada"
                : "estava com menor uso de CPU";
        }

        return new(decisao.Numero!.Value, decisao.Nivel!, nome, $"Tarefa #{decisao.Numero} enviada para {nome}: {motivo}.");
    }

    private void Registrar(DecisaoDistribuicao registro)
    {
        decisoes.Enqueue(registro);
        while (decisoes.Count > MaximoDecisoesHistorico)
        {
            decisoes.Dequeue();
        }
    }

    private static void RegistrarCpu(EstadoPlaca placa, double? cpu)
    {
        DateTimeOffset agora = DateTimeOffset.UtcNow;
        if (agora - placa.UltimoRegistroCpu < TimeSpan.FromSeconds(1))
        {
            return;
        }

        placa.UltimoRegistroCpu = agora;
        placa.HistoricoCpu.Enqueue(new(agora, cpu));
        while (placa.HistoricoCpu.Count > MaximoAmostrasCpu)
        {
            placa.HistoricoCpu.Dequeue();
        }
    }

    private Trabalho EnfileirarInterno(SolicitacaoTrabalho solicitacao)
    {
        solicitacao.Validar();
        if (ContarNaFila() >= LimiteFila)
        {
            throw new InvalidOperationException("Fila cheia.");
        }

        var trabalho = new Trabalho { Solicitacao = solicitacao };
        trabalhos.Add(trabalho);
        return trabalho;
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
        if (saude is null)
        {
            return 0;
        }

        return saude.TrabalhoId == trabalho.Id ? saude.Progresso : 0;
    }

    private static bool TelemetriaValida(EstadoPlaca placa) =>
        placa.Online &&
        placa.Saude is { CpuValida: true, IdadeAmostraMs: >= 0 } saude &&
        saude.IdadeAmostraMs + (DateTimeOffset.UtcNow - placa.AtualizadoEm).TotalMilliseconds <= IdadeMaximaTelemetriaMs;

    private static double CpuProjetada(EstadoPlaca placa, int nucleo)
    {
        double cpu = placa.Saude!.Nucleos[nucleo].Cpu;
        DateTimeOffset instanteAmostra = placa.AtualizadoEm.AddMilliseconds(-placa.Saude.IdadeAmostraMs);
        SaudeVaga[] vagas = placa.ObterVagas();

        foreach (var reserva in placa.Reservas.OfType<Trabalho>().Where(trabalho => trabalho.Nucleo == nucleo))
        {
            bool reservaAindaNaoRefletidaNaAmostra =
                reserva.IniciadoEm >= instanteAmostra ||
                vagas.ElementAtOrDefault(reserva.Vaga!.Value)?.TrabalhoId != reserva.Id;

            if (reservaAindaNaoRefletidaNaAmostra)
            {
                cpu += PerfilRenderizacao.Obter(reserva.Solicitacao.Nivel).Intensidade;
            }
        }

        return Math.Min(100, cpu);
    }

    private static double CargaNucleo(EstadoPlaca placa, int nucleo, Trabalho trabalho) =>
        trabalho.EhTarefaDoPainel
            ? placa.Saude!.Nucleos[nucleo].Cpu
            : CpuProjetada(placa, nucleo);

    private static double CargaPlaca(EstadoPlaca placa, Trabalho trabalho) =>
        trabalho.EhTarefaDoPainel
            ? placa.Saude!.CpuTotal
            : (CargaNucleo(placa, 0, trabalho) + CargaNucleo(placa, 1, trabalho)) / 2;

    private string MotivoIndisponibilidade(EstadoPlaca placa, Trabalho trabalho)
    {
        if (!placa.Habilitada)
        {
            return "Retirada do balanceamento";
        }

        if (!placa.Online)
        {
            return "Offline";
        }

        if (!TelemetriaValida(placa))
        {
            return "Amostra de CPU indisponível ou vencida";
        }

        if (trabalho.Destino is not null && trabalho.Destino != placa.Transporte.Id)
        {
            return "Carga direcionada a outra placa";
        }

        if (!trabalho.EhCargaDirecionada && CargaPlaca(placa, trabalho) >= LimiteCpuAutomatica)
        {
            return trabalho.EhTarefaDoPainel ? "CPU atingiu 85%" : "CPU projetada atingiu 85%";
        }

        if (!placa.PossuiVagaLivre(limiteTarefas))
        {
            return "Limite de tarefas atingido";
        }

        return SituacaoDisponivel;
    }

    private CandidataDistribuicao[] AvaliarCandidatas(Trabalho trabalho) =>
        placas.Select(placa =>
        {
            bool telemetriaValida = TelemetriaValida(placa);
            SaudeVaga[] vagas = placa.ObterVagas();

            return new CandidataDistribuicao(
                placa.Transporte.Id,
                telemetriaValida ? placa.Saude?.CpuTotal : null,
                telemetriaValida ? CargaPlaca(placa, trabalho) : null,
                placa.ContarVagasOcupadas(vagas),
                MotivoIndisponibilidade(placa, trabalho));
        }).ToArray();

    private CandidataDistribuicao[] AvaliarFila()
    {
        Trabalho? proximo =
            trabalhos.FirstOrDefault(trabalho => trabalho.Estado == EstadosTrabalho.NaFila && trabalho.Solicitacao.Tipo == "bloco")
            ?? trabalhos.FirstOrDefault(trabalho => trabalho.Estado == EstadosTrabalho.NaFila);

        return proximo is null ? [] : AvaliarCandidatas(proximo);
    }

    private string SituacaoDistribuicao()
    {
        if (pausado)
        {
            return "Distribuição pausada";
        }

        if (!trabalhos.Any(trabalho => trabalho.Estado == EstadosTrabalho.NaFila))
        {
            return "Sem tarefas aguardando";
        }

        if (AguardandoIntervaloAutomatico())
        {
            return "Aguardando intervalo de demonstração";
        }

        return AvaliarFila().Any(candidata => candidata.Situacao == SituacaoDisponivel)
            ? "Próxima atribuição pronta"
            : "Aguardando CPU, vaga ou placa disponível";
    }

    private bool AguardandoIntervaloAutomatico() =>
        ultimaAtribuicaoAutomatica is { } ultima &&
        relogio.GetUtcNow() - ultima < TimeSpan.FromMilliseconds(intervaloAtribuicaoMs);

    private IEnumerable<int> ObterIndicesVagasLivres(EstadoPlaca placa)
    {
        SaudeVaga[] vagas = placa.ObterVagas();
        int limiteVerificacao = Math.Min(limiteTarefas, vagas.Length);

        for (int indice = 0; indice < limiteVerificacao; indice++)
        {
            if (!placa.VagaOcupada(indice, vagas))
            {
                yield return indice;
            }
        }
    }

    private (EstadoPlaca Placa, int Indice)? EscolherVaga(Trabalho trabalho)
    {
        var vagasOrdenadas = placas
            .Where(placa => MotivoIndisponibilidade(placa, trabalho) == SituacaoDisponivel)
            .SelectMany(placa => ObterIndicesVagasLivres(placa).Select(indice => (Placa: placa, Indice: indice)))
            .OrderBy(vaga => CargaPlaca(vaga.Placa, trabalho))
            .ThenBy(vaga => CargaNucleo(vaga.Placa, vaga.Indice % 2, trabalho))
            .ThenBy(vaga => vaga.Placa.Atribuidos.Sum())
            .ThenBy(vaga => vaga.Placa.Transporte.Id, StringComparer.Ordinal)
            .ThenBy(vaga => vaga.Indice);

        return vagasOrdenadas.Select(vaga => ((EstadoPlaca Placa, int Indice)?)vaga).FirstOrDefault();
    }

    private async Task DistribuirTrabalhosPendentesAsync(CancellationToken cancelamento)
    {
        while (true)
        {
            Trabalho? trabalho;
            EstadoPlaca? placa = null;
            int indiceVaga = 0;

            lock (trava)
            {
                (trabalho, placa, indiceVaga) = SelecionarProximoTrabalhoEVaga();
                if (trabalho is null || placa is null)
                {
                    break;
                }

                PrepararEnvioTrabalho(trabalho, placa, indiceVaga);
            }

            await EnviarTrabalhoParaPlacaAsync(trabalho, placa, indiceVaga, cancelamento);
        }
    }

    private (Trabalho? Trabalho, EstadoPlaca? Placa, int IndiceVaga) SelecionarProximoTrabalhoEVaga()
    {
        if (pausado)
        {
            return (null, null, 0);
        }

        IOrderedEnumerable<Trabalho> candidatos = trabalhos
            .Where(candidato => candidato.Estado == EstadosTrabalho.NaFila)
            .OrderBy(candidato => candidato.Solicitacao.Tipo == "carga" ? 0 : 1);

        bool intervaloAtivo = AguardandoIntervaloAutomatico();

        foreach (var candidato in candidatos)
        {
            if (candidato.Destino is null && intervaloAtivo)
            {
                continue;
            }

            var vaga = EscolherVaga(candidato);
            if (vaga is not null)
            {
                return (candidato, vaga.Value.Placa, vaga.Value.Indice);
            }
        }

        return (null, null, 0);
    }

    private void PrepararEnvioTrabalho(Trabalho trabalho, EstadoPlaca placa, int indiceVaga)
    {
        if (trabalho.EhTarefaDoPainel && placa.SuportaImagemProgressiva)
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
        }

        string motivo = trabalho.Destino is not null
            ? "Carga de teste direcionada pelo operador"
            : trabalho.EhTarefaDoPainel
                ? "Menor uso de CPU entre placas disponíveis"
                : "Menor CPU projetada entre placas disponíveis";

        Registrar(new(
            DateTimeOffset.UtcNow,
            "atribuicao",
            placa.Transporte.Id,
            trabalho.Id,
            trabalho.Solicitacao.Nivel,
            trabalho.Solicitacao.Tipo,
            indiceVaga % 2,
            motivo,
            AvaliarCandidatas(trabalho),
            trabalho.Numero));

        trabalho.Estado = EstadosTrabalho.Executando;
        trabalho.Placa = placa.Transporte.Id;
        trabalho.Nucleo = indiceVaga % 2;
        trabalho.Vaga = indiceVaga;
        trabalho.IniciadoEm = DateTimeOffset.UtcNow;

        if (trabalho.Destino is null)
        {
            ultimaAtribuicaoAutomatica = relogio.GetUtcNow();
        }

        placa.Reservas[indiceVaga] = trabalho;
        placa.Atribuidos[indiceVaga]++;
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
        if (saude.Versao is not (4 or 5))
        {
            throw new IOException("Protocolo incompatível. Atualize o firmware para v5.");
        }

        if (saude.Vagas is not null)
        {
            bool vagasInvalidas =
                saude.Vagas.Length is not (2 or 4) ||
                saude.Vagas.Where((vaga, indice) => vaga.Indice != indice || !PercentualValido(vaga.Progresso)).Any();

            if (vagasInvalidas)
            {
                throw new IOException("Vagas inválidas.");
            }
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
            (resultado.Vaga ?? resultado.Nucleo) == trabalho.Vaga;

        bool parametrosCorretos =
            resultado.Largura == SolicitacaoTrabalho.TamanhoBloco &&
            resultado.Altura == SolicitacaoTrabalho.TamanhoBloco &&
            resultado.Quadro == solicitacao.Quadro &&
            resultado.Passagem == solicitacao.Passagem &&
            resultado.Coluna == solicitacao.Coluna &&
            resultado.Linha == solicitacao.Linha &&
            resultado.LarguraImagem == solicitacao.LarguraImagem &&
            resultado.AlturaImagem == solicitacao.AlturaImagem &&
            resultado.Tipo == solicitacao.Tipo &&
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
                placa.AtualizadoEm = DateTimeOffset.UtcNow;
                placa.Erro = null;
                RegistrarCpu(placa, TelemetriaValida(placa) ? saude.CpuTotal : null);
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
                RegistrarCpu(placa, null);
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

            int prazoSegundos = trabalho.Solicitacao.Tipo == "carga"
                ? trabalho.Solicitacao.DuracaoSegundos + 5
                : timeoutSegundos;

            if (!EstadosTrabalho.EhTerminal(trabalho.Estado) &&
                DateTimeOffset.UtcNow - trabalho.IniciadoEm > TimeSpan.FromSeconds(prazoSegundos))
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

        if (trabalho.EhTarefaDoPainel && placa.SuportaImagemProgressiva)
        {
            await ProcessarPublicacaoImagemAsync(placa, trabalho, resultado, cancelamento);
        }

        byte[]? imagem = null;
        if (resultado.Estado == EstadosTrabalho.Concluido && trabalho.Solicitacao.Tipo == "bloco")
        {
            imagem = await TentarBaixarImagemBlocoAsync(placa, trabalho, cancelamento);
            if (imagem is null)
            {
                return;
            }
        }

        lock (trava)
        {
            trabalho.Resultado = resultado;
            trabalho.Imagem = imagem;
            trabalho.Erro = null;

            if (EstadosTrabalho.EhTerminal(resultado.Estado))
            {
                trabalho.Estado = trabalho.Estado == EstadosTrabalho.Cancelando
                    ? EstadosTrabalho.Cancelado
                    : resultado.Estado;

                if (trabalho.Estado == EstadosTrabalho.Cancelado)
                {
                    trabalho.Imagem = null;
                }

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

    private async Task<byte[]?> TentarBaixarImagemBlocoAsync(
        EstadoPlaca placa,
        Trabalho trabalho,
        CancellationToken cancelamento)
    {
        try
        {
            byte[] imagem = await placa.Transporte.ObterImagem(trabalho.Id, SolicitacaoTrabalho.BytesBloco, cancelamento);
            if (imagem.Length != SolicitacaoTrabalho.BytesBloco)
            {
                throw new IOException("Imagem incompleta.");
            }
            return imagem;
        }
        catch (Exception erro) when (erro is not OperationCanceledException || !cancelamento.IsCancellationRequested)
        {
            lock (trava)
            {
                trabalho.Erro = $"Aguardando imagem: {erro.Message}";
            }
            return null;
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
