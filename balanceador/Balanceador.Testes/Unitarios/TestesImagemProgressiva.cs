using Balanceador.Contratos;
using Balanceador.Modelos;
using Balanceador.Servicos;
using Balanceador.Testes.Fakes;
using Xunit;

namespace Balanceador.Testes.Unitarios;

public class TestesImagemProgressiva
{
    [Fact]
    public void ReservaBlocosDistintosPeloCentroELiberaBlocoInterrompido()
    {
        var imagem = new ImagemProgressiva();
        var primeiro = imagem.Reservar("A")!;
        var segundo = imagem.Reservar("B")!;
        Assert.NotEqual(primeiro, segundo);
        Assert.Equal(primeiro, imagem.Reservar("A"));
        Assert.InRange(primeiro.Coluna, 224, 272);
        Assert.InRange(primeiro.Linha, 160, 208);
        imagem.Liberar("A");
        Assert.Equal(primeiro, imagem.Reservar("C"));
    }

    [Fact]
    public void ResetDescartaReservasEPixelsAntigosSemAceitarIntegracaoAtrasada()
    {
        var imagem = new ImagemProgressiva();
        string antiga = imagem.ObterEstado().Sessao;
        var bloco = imagem.Reservar("A")!;
        var dados = Enumerable.Repeat((byte)80, 768).ToArray();
        Assert.True(imagem.Integrar("A", bloco, dados));
        Assert.False(imagem.Integrar("A", bloco, dados));
        Assert.Equal(1, imagem.ObterEstado().BlocosConcluidos);
        var pendente = imagem.Reservar("B")!;
        imagem.Resetar();
        Assert.NotEqual(antiga, imagem.ObterEstado().Sessao);
        Assert.Null(imagem.ObterPixels(antiga));
        Assert.False(imagem.Integrar("B", pendente, dados));
        Assert.Equal(0, imagem.ObterEstado().BlocosConcluidos);
        Assert.All(imagem.ObterPixels(imagem.ObterEstado().Sessao)!.Value.Pixels, valor => Assert.Equal(0, valor));
    }

    [Fact]
    public void QuatroPassagensPreenchemImagemGrandeEFazemMediaDosPixels()
    {
        var imagem = new ImagemProgressiva();
        int quantidade = 0;
        while (imagem.Reservar("A") is { } bloco)
        {
            var dados = Enumerable.Repeat((byte)((bloco.Passagem + 1) * 40), 768).ToArray();
            Assert.True(imagem.Integrar("A", bloco, dados));
            quantidade++;
        }
        Assert.Equal(3072, quantidade);
        Assert.Equal(3072, imagem.ObterEstado().BlocosConcluidos);
        var pixels = imagem.ObterPixels(imagem.ObterEstado().Sessao)!.Value.Pixels;
        Assert.Equal(512 * 384 * 3, pixels.Length);
        Assert.All(pixels, valor => Assert.Equal(100, valor));
        Assert.Null(imagem.Reservar("B"));
    }

    [Fact]
    public async Task ConfirmacaoPerdidaEResetNaoDuplicamBlocosNemInterrompemTarefa()
    {
        var placa = new PlacaSimulada();
        var balanceador = new BalanceadorDeCarga([placa]);
        var tarefa = balanceador.AdicionarTarefa("leve");
        try
        {
            await balanceador.ExecutarCiclo(default);
            await Aguardar(() => placa.Simulador.ConsultarBloco(tarefa.Id)?.Publicacao == 0);
            placa.PerderConfirmacao = true;
            await balanceador.ExecutarCiclo(default);
            Assert.Equal(1, balanceador.ObterPainel().Imagem!.BlocosConcluidos);
            await Aguardar(() => placa.Simulador.ConsultarBloco(tarefa.Id)?.Publicacao == 1);
            await balanceador.ExecutarCiclo(default);
            Assert.Equal(2, balanceador.ObterPainel().Imagem!.BlocosConcluidos);
            string sessao = balanceador.ObterPainel().Imagem!.Sessao;
            balanceador.ResetarImagem();
            await Aguardar(() => placa.Simulador.ConsultarBloco(tarefa.Id)?.Publicacao == 2);
            await balanceador.ExecutarCiclo(default);
            Assert.Equal(0, balanceador.ObterPainel().Imagem!.BlocosConcluidos);
            await Aguardar(() => placa.Simulador.ConsultarBloco(tarefa.Id)?.Publicacao == 3);
            await balanceador.ExecutarCiclo(default);
            Assert.Equal(1, balanceador.ObterPainel().Imagem!.BlocosConcluidos);
            Assert.NotEqual(sessao, balanceador.ObterPainel().Imagem!.Sessao);
            Assert.Equal(tarefa.Numero, Assert.Single(balanceador.ObterPainel().Placas[0].Tarefas).Numero);
            Assert.Equal(EstadosTrabalho.Executando, tarefa.Estado);
        }
        finally
        {
            placa.Simulador.Cancelar(tarefa.Id);
        }
    }

    [Fact]
    public async Task PublicacaoPermaneceImutavelEnquantoProcessadorContinuaCalculando()
    {
        var placa = new Simulador("A");
        placa.ConfigurarFalha(false, 0);
        string id = Guid.NewGuid().ToString("N");
        var solicitacao = new SolicitacaoTrabalho("leve", Coluna: 240, Linha: 176,
            LarguraImagem: 512, AlturaImagem: 384, DuracaoSegundos: 3);
        placa.Enviar(id, solicitacao, 0);
        try
        {
            await Aguardar(() => placa.ConsultarBloco(id) is not null);
            var primeira = placa.ConsultarBloco(id)!;
            var copia = primeira.Pixels.ToArray();
            await Task.Delay(30);
            Assert.Equal(copia, placa.ConsultarBloco(id)!.Pixels);
            var proximo = new BlocoImagem(1, 0, 256, 176);
            placa.AvancarBloco(id, primeira.Publicacao, proximo);
            placa.AvancarBloco(id, primeira.Publicacao, proximo);
            await Aguardar(() => placa.ConsultarBloco(id)?.Publicacao == 1);
            Assert.Equal(1, placa.ConsultarBloco(id)!.Quadro);
            Assert.Equal(0, placa.ConsultarTrabalho(id).Quadro);
            Assert.Equal(copia, primeira.Pixels);
            Assert.Throws<InvalidOperationException>(() => placa.AvancarBloco(id, primeira.Publicacao, proximo with { Coluna = 272 }));
        }
        finally
        {
            placa.Cancelar(id);
        }
    }

    private static async Task Aguardar(Func<bool> condicao)
    {
        using var limite = new CancellationTokenSource(5000);
        while (!condicao())
        {
            await Task.Delay(5, limite.Token);
        }
    }
}
