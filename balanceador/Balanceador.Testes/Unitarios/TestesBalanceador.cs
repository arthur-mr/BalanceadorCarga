using Balanceador.Contratos;
using Balanceador.Modelos;
using Balanceador.Servicos;
using Balanceador.Testes.Fakes;
using Xunit;

namespace Balanceador.Testes.Unitarios;

public class TestesBalanceador
{
    [Fact]
    public async Task DistribuiOitoTarefasEmDuasPlacasESeguraExcedenteNaFila()
    {
        var primeira = new PlacaFalsa("A");
        var segunda = new PlacaFalsa("B");
        var balanceador = new BalanceadorDeCarga([primeira, segunda]);
        var tarefas = Enumerable.Range(0, 9).Select(_ => balanceador.AdicionarTarefa("leve")).ToArray();

        await balanceador.ExecutarCiclo(default);

        Assert.Equal(4, tarefas.Count(tarefa => tarefa.Placa == "A"));
        Assert.Equal(4, tarefas.Count(tarefa => tarefa.Placa == "B"));
        Assert.Equal(EstadosTrabalho.NaFila, tarefas[8].Estado);
        Assert.Equal(8, tarefas.Take(8).Select(tarefa => (tarefa.Placa, tarefa.Vaga)).Distinct().Count());

        primeira.Concluir(0);
        await balanceador.ExecutarCiclo(default);

        Assert.Equal(EstadosTrabalho.Concluido, tarefas[0].Estado);
        Assert.Equal("A", tarefas[8].Placa);
        Assert.Equal(0, tarefas[8].Vaga);
    }

    [Fact]
    public async Task EscolheMenorCpuEDesempataPorNucleo()
    {
        var carregada = new PlacaFalsa("A") { Cpu = [80, 80] };
        var livre = new PlacaFalsa("B") { Cpu = [20, 10] };
        var balanceador = new BalanceadorDeCarga([carregada, livre]);

        var primeira = balanceador.AdicionarTarefa("pesado");
        await balanceador.ExecutarCiclo(default);

        Assert.Equal("B", primeira.Placa);
        Assert.Equal(1, primeira.Nucleo);
    }

    [Fact]
    public async Task PainelEscolheCpuRealMesmoQuandoAPlacaJaTemMaisTarefasLeves()
    {
        var primeira = new PlacaFalsa("A");
        var segunda = new PlacaFalsa("B") { Cpu = [30, 30] };
        var balanceador = new BalanceadorDeCarga([primeira, segunda]);

        var pesada = balanceador.AdicionarTarefa("pesado");
        await balanceador.ExecutarCiclo(default);
        Assert.Equal("A", pesada.Placa);

        primeira.Cpu = [80, 0];
        segunda.Cpu = [0, 0];
        balanceador.AdicionarTarefa("leve");
        await balanceador.ExecutarCiclo(default);
        balanceador.AdicionarTarefa("leve");
        await balanceador.ExecutarCiclo(default);

        segunda.Cpu = [20, 20];
        var nova = balanceador.AdicionarTarefa("leve");
        await balanceador.ExecutarCiclo(default);

        Assert.Equal("B", nova.Placa);
        var painel = balanceador.ObterPainel();
        Assert.Equal(3, painel.Placas[1].Tarefas.Length);
        Assert.Single(painel.Placas[0].Tarefas);
        Assert.Equal(20, painel.Placas[1].Cpu);
        Assert.Equal(4, painel.UltimaDecisao!.Numero);
        Assert.Contains("menor uso de CPU", painel.UltimaDecisao.Explicacao);
        Assert.Empty(painel.Fila);
    }

    [Theory]
    [InlineData(false, 0, 10)]
    [InlineData(true, 3001, 10)]
    [InlineData(true, 0, 85)]
    public async Task NaoDistribuiSemCpuValidaComAmostraAntigaOuCpuNoLimite(bool valida, long idade, double cpu)
    {
        var placa = new PlacaFalsa("A") { CpuValida = valida, IdadeAmostra = idade, Cpu = [cpu, cpu] };
        var balanceador = new BalanceadorDeCarga([placa]);
        var trabalho = balanceador.AdicionarTarefa("leve");

        await balanceador.ExecutarCiclo(default);

        Assert.Equal(EstadosTrabalho.NaFila, trabalho.Estado);
        Assert.True(balanceador.ObterPainel().Placas[0].Online);
    }

    [Fact]
    public async Task RecusaTelemetriaInvalidaSemDistribuir()
    {
        var placa = new PlacaFalsa("A") { Cpu = [101, 0] };
        var balanceador = new BalanceadorDeCarga([placa]);
        var trabalho = balanceador.AdicionarTarefa("leve");

        await balanceador.ExecutarCiclo(default);

        Assert.Equal(EstadosTrabalho.NaFila, trabalho.Estado);
        var placaPainel = balanceador.ObterPainel().Placas[0];
        Assert.False(placaPainel.Online);
        Assert.Contains("Telemetria inválida", placaPainel.Erro);
    }

    [Fact]
    public async Task CancelamentoDeUmaVagaNaoInterrompeAsDemais()
    {
        var placa = new PlacaFalsa("A");
        var balanceador = new BalanceadorDeCarga([placa]);
        var primeiro = balanceador.AdicionarTarefa("leve");
        var segundo = balanceador.AdicionarTarefa("medio");

        await balanceador.ExecutarCiclo(default);
        Assert.True(balanceador.Cancelar(primeiro.Id));
        await balanceador.ExecutarCiclo(default);

        Assert.Equal(EstadosTrabalho.Cancelado, primeiro.Estado);
        Assert.Equal(EstadosTrabalho.Executando, segundo.Estado);
        Assert.True((await placa.ConsultarSaude(default)).Vagas[segundo.Vaga!.Value].Ocupado);
    }

    [Fact]
    public async Task TimeoutConservaReservaAteConfirmarTermino()
    {
        var relogio = new RelogioTeste();
        var placa = new PlacaFalsa("A") { IgnorarCancelamento = true };
        var balanceador = new BalanceadorDeCarga([placa], relogio);

        var t1 = balanceador.AdicionarTarefa("leve");
        var t2 = balanceador.AdicionarTarefa("leve");
        var t3 = balanceador.AdicionarTarefa("leve");
        var t4 = balanceador.AdicionarTarefa("leve");
        var t5 = balanceador.AdicionarTarefa("leve");
        await balanceador.ExecutarCiclo(default);

        Assert.Equal(EstadosTrabalho.NaFila, t5.Estado);

        relogio.Avancar(26000);
        await balanceador.ExecutarCiclo(default);

        Assert.Equal(EstadosTrabalho.Falhou, t1.Estado);
        Assert.Equal(EstadosTrabalho.Falhou, t2.Estado);
        Assert.Equal(EstadosTrabalho.Falhou, t3.Estado);
        Assert.Equal(EstadosTrabalho.Falhou, t4.Estado);
        Assert.Equal(EstadosTrabalho.NaFila, t5.Estado);
        Assert.Equal(5, balanceador.ObterPainel().Fila.Length);

        placa.Concluir(t2.Vaga!.Value);
        await balanceador.ExecutarCiclo(default);

        var trabalhoAtendido = balanceador.ObterPainel().Placas[0].Tarefas.Single();
        Assert.Equal(t5.Numero, trabalhoAtendido.Numero);
    }

    [Fact]
    public async Task AceitacaoIncertaNaoReenviaTrabalhoNemReutilizaVagaOcupada()
    {
        var placa = new PlacaFalsa("A") { PerderAceitacao = true, IgnorarCancelamento = true };
        var balanceador = new BalanceadorDeCarga([placa]);
        var primeira = balanceador.AdicionarTarefa("leve");

        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Falhou, primeira.Estado);

        placa.PerderAceitacao = false;
        await balanceador.ExecutarCiclo(default);

        Assert.Equal(2, placa.Envios);
        var segundaTentativa = Assert.Single(balanceador.ObterPainel().Placas[0].Tarefas);
        Assert.Equal(primeira.Numero, segundaTentativa.Numero);
        var vagaEmExecucao = placa.Resultados.Single(par => par.Value.Id != primeira.Id);
        Assert.NotEqual(primeira.Vaga, vagaEmExecucao.Key);
        Assert.Equal(EstadosTrabalho.Executando, vagaEmExecucao.Value.Estado);
    }

    [Fact]
    public async Task DesconexaoFalhaTrabalhosEReconciliacaoPermiteRecuperar()
    {
        var placa = new PlacaFalsa("A") { IgnorarCancelamento = true };
        var balanceador = new BalanceadorDeCarga([placa]);
        var trabalho = balanceador.AdicionarTarefa("leve");

        await balanceador.ExecutarCiclo(default);
        placa.Offline = true;
        await balanceador.ExecutarCiclo(default);

        Assert.Equal(EstadosTrabalho.Falhou, trabalho.Estado);
        Assert.Single(balanceador.ObterPainel().Fila);

        placa.Offline = false;
        placa.Resultados.Clear();
        await balanceador.ExecutarCiclo(default);

        Assert.Empty(balanceador.ObterPainel().Fila);
        Assert.Equal(trabalho.Numero, Assert.Single(balanceador.ObterPainel().Placas[0].Tarefas).Numero);
    }

    [Fact]
    public async Task ResultadoIncompativelFalhaSemInterromperOutrasTarefas()
    {
        var placa = new PlacaFalsa("A");
        var balanceador = new BalanceadorDeCarga([placa]);
        var primeiro = balanceador.AdicionarTarefa("leve");
        var segundo = balanceador.AdicionarTarefa("leve");

        await balanceador.ExecutarCiclo(default);
        placa.Resultados[primeiro.Vaga!.Value] = placa.Resultados[primeiro.Vaga.Value] with { Largura = 1 };
        await balanceador.ExecutarCiclo(default);

        Assert.Equal(EstadosTrabalho.Falhou, primeiro.Estado);
        Assert.Equal(EstadosTrabalho.Executando, segundo.Estado);
    }

    [Fact]
    public async Task RetiradaRedistribuiTarefaDoPainelPreservandoNumeroESemDuplicarNaTela()
    {
        var primeira = new PlacaFalsa("A");
        var segunda = new PlacaFalsa("B");
        var balanceador = new BalanceadorDeCarga([primeira, segunda]);
        var original = balanceador.AdicionarTarefa("medio");

        await balanceador.ExecutarCiclo(default);
        Assert.Equal("A", original.Placa);

        balanceador.AlterarParticipacao("A", false);
        Assert.Equal(1, Assert.Single(balanceador.ObterPainel().Fila).Numero);

        await balanceador.ExecutarCiclo(default);
        var painel = balanceador.ObterPainel();
        Assert.Empty(painel.Fila);
        Assert.Empty(painel.Placas[0].Tarefas);
        Assert.Equal(1, Assert.Single(painel.Placas[1].Tarefas).Numero);
        Assert.Equal("B", painel.UltimaDecisao!.Placa);
        Assert.Contains("única placa disponível", painel.UltimaDecisao.Explicacao);
        Assert.True(original.Substituido);
        Assert.NotEqual(original.Id, segunda.Resultados.Values.Single().Id);

        primeira.Concluir(original.Vaga!.Value);
        await balanceador.ExecutarCiclo(default);
        Assert.Single(balanceador.ObterPainel().Placas.SelectMany(placa => placa.Tarefas));
    }

    [Fact]
    public async Task TarefasAguardamSemPlacasEFilasPreservamOrdemDeEnvio()
    {
        var placa = new PlacaFalsa("A");
        var balanceador = new BalanceadorDeCarga([placa]);
        var primeira = balanceador.AdicionarTarefa("leve");

        await balanceador.ExecutarCiclo(default);
        balanceador.AdicionarTarefa("pesado");
        balanceador.AlterarParticipacao("A", false);
        Assert.Equal(new long[] { 2, 1 }, balanceador.ObterPainel().Fila.Select(tarefa => tarefa.Numero));

        await balanceador.ExecutarCiclo(default);
        Assert.Equal(2, balanceador.ObterPainel().Fila.Length);

        balanceador.AlterarParticipacao("A", true);
        await balanceador.ExecutarCiclo(default);
        Assert.Empty(balanceador.ObterPainel().Fila);
        Assert.Equal(2, balanceador.ObterPainel().Placas[0].Tarefas.Length);
        Assert.Equal(20, placa.Resultados.Values.Single(resultado => resultado.Id != primeira.Id && resultado.Nivel == "leve").DuracaoSegundos);
    }

    [Fact]
    public async Task EncerraRecuperacaoComAvisoSemRepetirTarefaIndefinidamente()
    {
        var placa = new PlacaFalsa("A") { PerderAceitacao = true };
        var balanceador = new BalanceadorDeCarga([placa]);
        balanceador.AdicionarTarefa("leve");

        for (int ciclo = 0; ciclo < 8; ciclo++)
        {
            await balanceador.ExecutarCiclo(default);
        }

        Assert.Equal(3, placa.Envios);
        Assert.Empty(balanceador.ObterPainel().Fila);
        Assert.Empty(balanceador.ObterPainel().Placas[0].Tarefas);
        Assert.Contains("tarefa #1", balanceador.ObterPainel().Aviso);
    }

    [Fact]
    public async Task RitmoDeDemonstracaoEspacaAtribuicoesAutomaticas()
    {
        var relogio = new RelogioTeste();
        var placa = new PlacaFalsa("A");
        var balanceador = new BalanceadorDeCarga([placa], relogio, intervaloAtribuicaoMs: 1000);

        var t1 = balanceador.AdicionarTarefa("leve");
        var t2 = balanceador.AdicionarTarefa("leve");

        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Executando, t1.Estado);
        Assert.Equal(EstadosTrabalho.NaFila, t2.Estado);

        relogio.Avancar(999);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.NaFila, t2.Estado);

        relogio.Avancar(1);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Executando, t2.Estado);
    }

    [Fact]
    public void LimiteDeFilaEValidacaoDeNivel()
    {
        var balanceador = new BalanceadorDeCarga([]);
        for (int indice = 0; indice < 100; indice++)
        {
            var trabalho = balanceador.AdicionarTarefa("leve");
            Assert.Equal(indice + 1, trabalho.Numero);
            Assert.Equal(20, trabalho.Solicitacao.DuracaoSegundos);
        }

        Assert.Throws<InvalidOperationException>(() => balanceador.AdicionarTarefa("leve"));
        Assert.Throws<ArgumentException>(() => balanceador.AdicionarTarefa("invalido"));
    }
}
