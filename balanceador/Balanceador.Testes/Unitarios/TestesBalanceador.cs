using System.Text.Json;
using Balanceador.Contratos;
using Balanceador.Modelos;
using Balanceador.Servicos;
using Balanceador.Testes.Fakes;
using Xunit;

namespace Balanceador.Testes.Unitarios;

public class TestesBalanceador
{
    private static JsonElement ObterEstado(BalanceadorDeCarga balanceador) =>
        JsonSerializer.SerializeToElement(balanceador.ObterEstado(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task DistribuiQuatroTrabalhosEmDuasPlacasESeguraExcedenteNaFila()
    {
        var primeira = new PlacaFalsa("A");
        var segunda = new PlacaFalsa("B");
        var balanceador = new BalanceadorDeCarga([primeira, segunda]);
        var trabalhos = Enumerable.Range(0, 5).Select(_ => balanceador.Enfileirar(new("leve"))).ToArray();
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(2, trabalhos.Count(trabalho => trabalho.Placa == "A"));
        Assert.Equal(2, trabalhos.Count(trabalho => trabalho.Placa == "B"));
        Assert.Equal(EstadosTrabalho.NaFila, trabalhos[4].Estado);
        Assert.Equal(4, trabalhos.Take(4).Select(trabalho => (trabalho.Placa, trabalho.Nucleo)).Distinct().Count());
        primeira.Concluir(0);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Concluido, trabalhos[0].Estado);
        Assert.Equal("A", trabalhos[4].Placa);
        Assert.Equal(0, trabalhos[4].Nucleo);
    }

    [Fact]
    public async Task EscolheMenorCpuERecalculaProjecaoAposCadaReserva()
    {
        var carregada = new PlacaFalsa("A") { Cpu = [90, 90] };
        var livre = new PlacaFalsa("B") { Cpu = [2, 1] };
        var balanceador = new BalanceadorDeCarga([carregada, livre]);
        var primeiro = balanceador.Enfileirar(new("pesado"));
        var segundo = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal("B", primeiro.Placa);
        Assert.Equal(1, primeiro.Nucleo);
        Assert.Equal("B", segundo.Placa);
        Assert.Equal(0, segundo.Nucleo);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 3001)]
    public async Task NaoDistribuiSemCpuValidaOuComAmostraAntiga(bool valida, long idade)
    {
        var placa = new PlacaFalsa("A") { CpuValida = valida, IdadeAmostra = idade };
        var balanceador = new BalanceadorDeCarga([placa]);
        var trabalho = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.NaFila, trabalho.Estado);
        Assert.True(ObterEstado(balanceador).GetProperty("placas")[0].GetProperty("online").GetBoolean());
    }

    [Fact]
    public async Task RecusaProtocoloAntigoETelemetriaInvalida()
    {
        var placa = new PlacaFalsa("A") { Versao = 1 };
        var balanceador = new BalanceadorDeCarga([placa]);
        var trabalho = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.NaFila, trabalho.Estado);
        Assert.Contains("incompatível", ObterEstado(balanceador).GetProperty("placas")[0].GetProperty("erro").GetString());
        placa.Versao = 4;
        placa.Cpu = [101, 0];
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.NaFila, trabalho.Estado);
    }

    [Fact]
    public async Task CancelamentoDeUmNucleoNaoInterrompeOOutro()
    {
        var placa = new PlacaFalsa("A");
        var balanceador = new BalanceadorDeCarga([placa]);
        var primeiro = balanceador.Enfileirar(new("leve"));
        var segundo = balanceador.Enfileirar(new("medio"));
        await balanceador.ExecutarCiclo(default);
        balanceador.Cancelar(primeiro.Id);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Cancelado, primeiro.Estado);
        Assert.Equal(EstadosTrabalho.Executando, segundo.Estado);
        Assert.True((await placa.ConsultarSaude(default)).Nucleos[segundo.Nucleo!.Value].Ocupado);
    }

    [Fact]
    public async Task TimeoutConservaReservaAteConfirmarTermino()
    {
        var placa = new PlacaFalsa("A") { IgnorarCancelamento = true };
        var balanceador = new BalanceadorDeCarga([placa]);
        balanceador.Configurar(false, 1);
        var primeiro = balanceador.Enfileirar(new("leve"));
        var segundo = balanceador.Enfileirar(new("leve"));
        var terceiro = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        primeiro.IniciadoEm = DateTimeOffset.UtcNow.AddSeconds(-2);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Falhou, primeiro.Estado);
        Assert.Equal(EstadosTrabalho.NaFila, terceiro.Estado);
        placa.Concluir(segundo.Nucleo!.Value);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(segundo.Nucleo, terceiro.Nucleo);
        Assert.NotEqual(primeiro.Nucleo, terceiro.Nucleo);
    }

    [Fact]
    public async Task AceitacaoIncertaNaoReenviaTrabalhoNemReutilizaNucleoOcupado()
    {
        var placa = new PlacaFalsa("A") { PerderAceitacao = true, IgnorarCancelamento = true };
        var balanceador = new BalanceadorDeCarga([placa]);
        var primeiro = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Falhou, primeiro.Estado);
        placa.PerderAceitacao = false;
        var segundo = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        Assert.NotEqual(primeiro.Nucleo, segundo.Nucleo);
        Assert.Equal(2, placa.Envios);
        Assert.Equal(1, placa.Resultados.Values.Count(resultado => resultado.Id == primeiro.Id));
    }

    [Fact]
    public async Task ImagemDeveSerCopiadaAntesDeReutilizarVaga()
    {
        var placa = new PlacaFalsa("A") { FalharImagem = true };
        var balanceador = new BalanceadorDeCarga([placa]);
        var primeiro = balanceador.Enfileirar(new("leve"));
        balanceador.Enfileirar(new("leve"));
        var terceiro = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        placa.Concluir(primeiro.Nucleo!.Value);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.NaFila, terceiro.Estado);
        Assert.Null(balanceador.ObterImagem(primeiro.Id));
        placa.FalharImagem = false;
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Concluido, primeiro.Estado);
        Assert.Equal(primeiro.Nucleo, terceiro.Nucleo);
        Assert.Equal(768, balanceador.ObterImagem(primeiro.Id)!.Value.Imagem.Length);
        Assert.DoesNotContain(placa.Resultados.Values, resultado => resultado.Id == primeiro.Id);
    }

    [Fact]
    public async Task DesconexaoFalhaTrabalhosEReconciliacaoPermiteRecuperar()
    {
        var placa = new PlacaFalsa("A") { IgnorarCancelamento = true };
        var balanceador = new BalanceadorDeCarga([placa]);
        var trabalho = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        placa.Offline = true;
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Falhou, trabalho.Estado);
        placa.Offline = false;
        placa.Resultados.Clear();
        var seguinte = balanceador.Enfileirar(new("medio"));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Executando, seguinte.Estado);
    }

    [Fact]
    public async Task ResultadoIncompativelFalhaSemDisponibilizarImagem()
    {
        var placa = new PlacaFalsa("A");
        var balanceador = new BalanceadorDeCarga([placa]);
        var trabalho = balanceador.Enfileirar(new("leve"));
        var outro = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        placa.Resultados[trabalho.Nucleo!.Value] = placa.Resultados[trabalho.Nucleo.Value] with { Largura = 1 };
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Falhou, trabalho.Estado);
        Assert.Null(trabalho.Imagem);
        Assert.Equal(EstadosTrabalho.Executando, outro.Estado);
    }

    [Fact]
    public async Task PausaCancelamentoFilaELimitesDeHistorico()
    {
        var balanceador = new BalanceadorDeCarga([new PlacaFalsa("A")]);
        balanceador.Configurar(true, 120);
        var trabalho = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.NaFila, trabalho.Estado);
        balanceador.Cancelar(trabalho.Id);
        for (int indice = 0; indice < 220; indice++)
        {
            balanceador.Cancelar(balanceador.Enfileirar(new("leve")).Id);
            await balanceador.ExecutarCiclo(default);
        }
        Assert.Equal(200, ObterEstado(balanceador).GetProperty("trabalhos").GetArrayLength());
        for (int indice = 0; indice < 100; indice++) balanceador.Enfileirar(new("leve"));
        Assert.Throws<InvalidOperationException>(() => balanceador.Enfileirar(new("leve")));
        Assert.Throws<ArgumentException>(() => balanceador.Enfileirar(new("invalido")));
    }

    [Fact]
    public async Task DuasPlacasMontamUmaCenaComQuatroPassagensSemContarBlocoDuasVezes()
    {
        var primeira = new PlacaFalsa("A") { CorPorPassagem = true };
        var segunda = new PlacaFalsa("B") { CorPorPassagem = true };
        var balanceador = new BalanceadorDeCarga([primeira, segunda]);
        balanceador.IniciarCena("leve", false);
        await balanceador.ExecutarCiclo(default);
        for (int ciclo = 0; ciclo < 60; ciclo++)
        {
            foreach (var placa in new[] { primeira, segunda })
                foreach (int nucleo in placa.Resultados.Keys.ToArray()) placa.Concluir(nucleo);
            await balanceador.ExecutarCiclo(default);
            if (ObterEstado(balanceador).GetProperty("cena").GetProperty("estado").GetString() == "concluida") break;
        }
        var cena = ObterEstado(balanceador).GetProperty("cena");
        Assert.Equal("concluida", cena.GetProperty("estado").GetString());
        Assert.Equal(100, cena.GetProperty("progresso").GetDouble());
        Assert.Equal(192, cena.GetProperty("blocosConcluidos").GetInt32());
        Assert.Equal(2, cena.GetProperty("contribuicoes").GetArrayLength());
        Assert.Equal(192, cena.GetProperty("contribuicoes").EnumerateArray().Sum(parte => parte.GetProperty("blocos").GetInt32()));
        string sessao = cena.GetProperty("id").GetString()!;
        var imagem = balanceador.ObterImagemCena(sessao, 0)!.Value.Imagem;
        Assert.Equal(128 * 96 * 3, imagem.Length);
        Assert.All(imagem, valor => Assert.Equal(100, valor));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(192, ObterEstado(balanceador).GetProperty("cena").GetProperty("revisao").GetInt32());
        Assert.Null(balanceador.ObterImagemCena(sessao, 1));
    }

    [Fact]
    public async Task ReiniciarOuPararCenaDescartaResultadosDaSessaoAnterior()
    {
        var placa = new PlacaFalsa("A");
        var balanceador = new BalanceadorDeCarga([placa]);
        balanceador.IniciarCena("pesado", true);
        await balanceador.ExecutarCiclo(default);
        string anterior = ObterEstado(balanceador).GetProperty("cena").GetProperty("id").GetString()!;
        balanceador.IniciarCena("leve", false);
        placa.Concluir(0);
        placa.Concluir(1);
        await balanceador.ExecutarCiclo(default);
        var cena = ObterEstado(balanceador).GetProperty("cena");
        Assert.NotEqual(anterior, cena.GetProperty("id").GetString());
        Assert.Equal(0, cena.GetProperty("revisao").GetInt32());
        Assert.Null(balanceador.ObterImagemCena(anterior, 0));
        balanceador.PararCena();
        await balanceador.ExecutarCiclo(default);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal("parada", ObterEstado(balanceador).GetProperty("cena").GetProperty("estado").GetString());
        Assert.DoesNotContain(ObterEstado(balanceador).GetProperty("trabalhos").EnumerateArray(),
            trabalho => trabalho.GetProperty("estado").GetString() == EstadosTrabalho.NaFila);
    }

    [Fact]
    public async Task CenaRecalculaBlocoInvalidoSemDuplicarContribuicao()
    {
        var placa = new PlacaFalsa("A");
        var balanceador = new BalanceadorDeCarga([placa]);
        balanceador.IniciarCena("leve", false);
        await balanceador.ExecutarCiclo(default);
        placa.Resultados[0] = placa.Resultados[0] with { Quadro = 100 };
        await balanceador.ExecutarCiclo(default);
        for (int ciclo = 0; ciclo < 120; ciclo++)
        {
            foreach (int nucleo in placa.Resultados.Keys.ToArray()) placa.Concluir(nucleo);
            await balanceador.ExecutarCiclo(default);
            if (ObterEstado(balanceador).GetProperty("cena").GetProperty("estado").GetString() == "concluida") break;
        }
        var cena = ObterEstado(balanceador).GetProperty("cena");
        Assert.Equal("concluida", cena.GetProperty("estado").GetString());
        Assert.Equal(192, cena.GetProperty("contribuicoes")[0].GetProperty("blocos").GetInt32());
        Assert.Equal(193, placa.Envios);
    }

    [Fact]
    public async Task PlacaComDuasLevesRecebeTerceiraTarefaQuandoCpuEMenorQueUmaPesada()
    {
        var primeira = new PlacaFalsa("A") { QuantidadeVagas = 4 };
        var segunda = new PlacaFalsa("B") { QuantidadeVagas = 4 };
        var balanceador = new BalanceadorDeCarga([primeira, segunda]);
        balanceador.IniciarCenarioAssimetrico(30);
        await balanceador.ExecutarCiclo(default);
        primeira.Cpu = [20, 20];
        segunda.Cpu = [80, 0];
        var bloco = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal("A", bloco.Placa);
        Assert.Equal(3, primeira.Resultados.Values.Count(resultado => resultado.Estado == EstadosTrabalho.Executando));
        Assert.Single(segunda.Resultados);
        var decisao = ObterEstado(balanceador).GetProperty("decisoes")[0];
        Assert.Equal("A", decisao.GetProperty("placa").GetString());
        Assert.Equal(2, decisao.GetProperty("candidatas")[0].GetProperty("tarefas").GetInt32());
        Assert.Equal(1, decisao.GetProperty("candidatas")[1].GetProperty("tarefas").GetInt32());
        Assert.Equal(20, decisao.GetProperty("candidatas")[0].GetProperty("cpu").GetDouble());
        Assert.Equal(40, decisao.GetProperty("candidatas")[1].GetProperty("cpu").GetDouble());
        balanceador.IniciarCena("leve", false);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(3, ObterEstado(balanceador).GetProperty("trabalhos").EnumerateArray().Count(trabalho =>
            trabalho.GetProperty("solicitacao").GetProperty("tipo").GetString() == "carga" &&
            trabalho.GetProperty("estado").GetString() == EstadosTrabalho.Executando));
    }

    [Fact]
    public async Task RetirarPlacaConcentraNovasAtribuicoesNaOutraERetornoReequilibra()
    {
        var primeira = new PlacaFalsa("A");
        var segunda = new PlacaFalsa("B");
        var balanceador = new BalanceadorDeCarga([primeira, segunda]);
        balanceador.IniciarCena("leve", false);
        await balanceador.ExecutarCiclo(default);
        balanceador.AlterarParticipacao("A", false);
        for (int ciclo = 0; ciclo < 8; ciclo++)
        {
            foreach (int vaga in segunda.Resultados.Keys.ToArray()) segunda.Concluir(vaga);
            await balanceador.ExecutarCiclo(default);
        }
        var estado = ObterEstado(balanceador);
        Assert.False(estado.GetProperty("placas")[0].GetProperty("habilitada").GetBoolean());
        Assert.All(estado.GetProperty("decisoes").EnumerateArray().TakeWhile(decisao => decisao.GetProperty("tipo").GetString() != "retirada"),
            decisao => Assert.Equal("B", decisao.GetProperty("placa").GetString()));
        balanceador.AlterarParticipacao("A", true);
        await balanceador.ExecutarCiclo(default);
        Assert.Contains(ObterEstado(balanceador).GetProperty("trabalhos").EnumerateArray(), trabalho =>
            trabalho.GetProperty("placa").GetString() == "A" && trabalho.GetProperty("estado").GetString() == EstadosTrabalho.Executando);
    }

    [Fact]
    public async Task LimiteConfiguravelPermiteCompartilharNucleosESeguraNovasAdmissoesAoReduzir()
    {
        var placa = new PlacaFalsa("A") { QuantidadeVagas = 4 };
        var balanceador = new BalanceadorDeCarga([placa]);
        balanceador.Configurar(false, 120, 2);
        var trabalhos = Enumerable.Range(0, 5).Select(_ => balanceador.Enfileirar(new("leve"))).ToArray();
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(2, trabalhos.Count(trabalho => trabalho.Estado == EstadosTrabalho.Executando));
        balanceador.Configurar(false, 120, 4);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(4, trabalhos.Count(trabalho => trabalho.Estado == EstadosTrabalho.Executando));
        Assert.Equal(2, trabalhos.Count(trabalho => trabalho.Nucleo == 0));
        balanceador.Configurar(false, 120, 2);
        placa.Concluir(0);
        placa.Concluir(1);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.NaFila, trabalhos[4].Estado);
        placa.Concluir(2);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Executando, trabalhos[4].Estado);
    }

    [Fact]
    public async Task CpuAltaSeguraBlocosAutomaticosMasPermiteCargaDirecionadaDeTeste()
    {
        var placa = new PlacaFalsa("A") { QuantidadeVagas = 4, Cpu = [90, 90] };
        var balanceador = new BalanceadorDeCarga([placa]);
        var bloco = balanceador.Enfileirar(new("leve"));
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.NaFila, bloco.Estado);
        balanceador.AdicionarCarga("A", "pesado", 1, 30);
        await balanceador.ExecutarCiclo(default);
        Assert.Single(placa.Resultados);
        Assert.Equal("carga", placa.Resultados.Values.Single().Tipo);
        Assert.Equal(EstadosTrabalho.NaFila, bloco.Estado);
        balanceador.LimparCargas();
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(EstadosTrabalho.Cancelado, placa.Resultados.Values.Single().Estado);
        Assert.DoesNotContain(ObterEstado(balanceador).GetProperty("trabalhos").EnumerateArray(), trabalho =>
            trabalho.GetProperty("solicitacao").GetProperty("tipo").GetString() == "carga" && trabalho.GetProperty("imagemDisponivel").GetBoolean());
    }

    [Fact]
    public async Task ResolucaoAltaAbasteceFilaLimitadaEAtravessaPrimeiraJanelaDeBlocos()
    {
        var placa = new PlacaFalsa("A") { QuantidadeVagas = 4 };
        var balanceador = new BalanceadorDeCarga([placa]);
        balanceador.IniciarCena("leve", false, 512, 384);
        await balanceador.ExecutarCiclo(default);
        for (int ciclo = 0; ciclo < 25; ciclo++)
        {
            foreach (int vaga in placa.Resultados.Keys.ToArray()) placa.Concluir(vaga);
            await balanceador.ExecutarCiclo(default);
        }
        var estado = ObterEstado(balanceador);
        var cena = estado.GetProperty("cena");
        Assert.Equal(512, cena.GetProperty("largura").GetInt32());
        Assert.Equal(3072, cena.GetProperty("totalBlocos").GetInt32());
        Assert.True(cena.GetProperty("blocosConcluidos").GetInt32() > 64);
        Assert.True(estado.GetProperty("trabalhos").EnumerateArray().Count(trabalho => trabalho.GetProperty("estado").GetString() == EstadosTrabalho.NaFila) <= 64);
        Assert.Equal(512 * 384 * 3, balanceador.ObterImagemCena(cena.GetProperty("id").GetString()!, 0)!.Value.Imagem.Length);
    }

    [Fact]
    public async Task RitmoDeDemonstracaoEspacaBlocosSemAtrasarCargasDirecionadas()
    {
        var relogio = new RelogioTeste();
        var placa = new PlacaFalsa("A") { QuantidadeVagas = 4 };
        var balanceador = new BalanceadorDeCarga([placa], relogio);
        balanceador.Configurar(false, 120, 4, 1000);
        var blocos = Enumerable.Range(0, 3).Select(_ => balanceador.Enfileirar(new("leve"))).ToArray();
        await balanceador.ExecutarCiclo(default);
        Assert.Single(blocos, bloco => bloco.Estado == EstadosTrabalho.Executando);
        relogio.Avancar(999);
        await balanceador.ExecutarCiclo(default);
        Assert.Single(blocos, bloco => bloco.Estado == EstadosTrabalho.Executando);
        relogio.Avancar(1);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(2, blocos.Count(bloco => bloco.Estado == EstadosTrabalho.Executando));
        balanceador.AdicionarCarga("A", "leve", 2, 30);
        await balanceador.ExecutarCiclo(default);
        Assert.Equal(4, placa.Resultados.Count);
        Assert.Equal(EstadosTrabalho.NaFila, blocos[2].Estado);
    }

    [Fact]
    public void TarefaDoPainelTemDuracaoFixaENaoRecebeDestinoEscolhidoPeloUsuario()
    {
        var balanceador = new BalanceadorDeCarga([]);
        var primeira = balanceador.AdicionarTarefa("leve");
        Assert.Equal(1, primeira.Numero);
        Assert.Null(primeira.Destino);
        Assert.Equal("carga", primeira.Solicitacao.Tipo);
        Assert.Equal(20, primeira.Solicitacao.DuracaoSegundos);
        Assert.Throws<ArgumentException>(() => balanceador.AdicionarTarefa("invalido"));
        var segunda = balanceador.AdicionarTarefa("pesado");
        Assert.Equal(2, segunda.Numero);
        Assert.Equal(new long[] { 1, 2 }, balanceador.ObterPainel().Fila.Select(tarefa => tarefa.Numero));
    }

    [Fact]
    public async Task PainelEscolheCpuRealMesmoQuandoAPlacaJaTemMaisTarefasLeves()
    {
        var primeira = new PlacaFalsa("A") { QuantidadeVagas = 4 };
        var segunda = new PlacaFalsa("B") { QuantidadeVagas = 4, Cpu = [30, 30] };
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

    [Fact]
    public async Task RetiradaRedistribuiTarefaDoPainelPreservandoNumeroESemDuplicarNaTela()
    {
        var primeira = new PlacaFalsa("A") { QuantidadeVagas = 4 };
        var segunda = new PlacaFalsa("B") { QuantidadeVagas = 4 };
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
    public async Task TarefasDoPainelAguardamSemPlacasEFilasPreservamOrdemDeEnvio()
    {
        var placa = new PlacaFalsa("A") { QuantidadeVagas = 4 };
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
    public async Task PainelEncerraRecuperacaoComAvisoSemRepetirTarefaIndefinidamente()
    {
        var placa = new PlacaFalsa("A") { QuantidadeVagas = 4, PerderAceitacao = true };
        var balanceador = new BalanceadorDeCarga([placa]);
        balanceador.AdicionarTarefa("leve");
        for (int ciclo = 0; ciclo < 8; ciclo++) await balanceador.ExecutarCiclo(default);
        Assert.Equal(3, placa.Envios);
        Assert.Empty(balanceador.ObterPainel().Fila);
        Assert.Empty(balanceador.ObterPainel().Placas[0].Tarefas);
        Assert.Contains("tarefa #1", balanceador.ObterPainel().Aviso);
    }
}
