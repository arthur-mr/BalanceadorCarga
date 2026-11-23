using System.Numerics;
using Balanceador.Contratos;
using Balanceador.Modelos;
using Balanceador.Servicos;
using Xunit;

namespace Balanceador.Testes.Unitarios;

public class TestesRenderizacao
{
    [Theory]
    [InlineData("leve", 8, 25)]
    [InlineData("medio", 32, 55)]
    [InlineData("pesado", 96, 85)]
    public void PerfisControlamAmostrasEIntensidade(string nivel, int amostras, int intensidade) =>
        Assert.Equal(new PerfilRenderizacao(nivel, amostras, intensidade), PerfilRenderizacao.Obter(nivel));

    [Theory]
    [InlineData(-1, 0, 0, 0, 20)]
    [InlineData(0, 4, 0, 0, 20)]
    [InlineData(0, 0, 1, 0, 20)]
    [InlineData(0, 0, 512, 0, 20)]
    [InlineData(0, 0, 0, 384, 20)]
    [InlineData(0, 0, 0, 0, 0)]
    public void RejeitaBlocosInvalidos(int quadro, int passagem, int coluna, int linha, int duracao) =>
        Assert.Throws<ArgumentException>(() =>
            new SolicitacaoTrabalho("leve", quadro, passagem, coluna, linha, 512, 384, duracao).Validar());

    [Fact]
    public void RaiosProduzemCeuAzuladoESuperficiesColoridas()
    {
        var solicitacao = new SolicitacaoTrabalho("medio", LarguraImagem: 128, AlturaImagem: 96);
        Vector3 ceu = RenderizadorCena.CalcularPixel(0, 0, solicitacao);
        Assert.True(ceu.Z > ceu.Y && ceu.Y > ceu.X);
        var cores = Enumerable.Range(30, 40).Select(coluna => RenderizadorCena.CalcularPixel(coluna, 56, solicitacao)).ToArray();
        Assert.Contains(cores, cor => cor.X > cor.Z * 2);
        Assert.All(cores, cor =>
        {
            Assert.InRange(cor.X, 0, 1);
            Assert.InRange(cor.Y, 0, 1);
            Assert.InRange(cor.Z, 0, 1);
        });
    }

    [Fact]
    public async Task BlocoRgbEDeterministicoEPassagensUsamAmostrasDiferentes()
    {
        var solicitacao = new SolicitacaoTrabalho("leve", 0, 0, 32, 48, 128, 96);
        var primeira = await RenderizadorCena.Renderizar(solicitacao, _ => { }, default);
        var repetida = await RenderizadorCena.Renderizar(solicitacao, _ => { }, default);
        var refinada = await RenderizadorCena.Renderizar(solicitacao with { Passagem = 1 }, _ => { }, default);
        Assert.Equal(768, primeira.Length);
        Assert.Equal(primeira, repetida);
        Assert.False(primeira.SequenceEqual(refinada));
    }

    [Fact]
    public void MudarQuadroMoveLuzSemAlterarGeometria()
    {
        var primeiro = new SolicitacaoTrabalho("medio", LarguraImagem: 128, AlturaImagem: 96);
        var segundo = primeiro with { Quadro = 9 };
        Assert.NotEqual(RenderizadorCena.CalcularPixel(48, 56, primeiro), RenderizadorCena.CalcularPixel(48, 56, segundo));
    }

    [Fact]
    public async Task DuasVagasIdempotentesComCancelamentoIsolado()
    {
        var simulador = new Simulador("A");
        simulador.ConfigurarFalha(false, 1000);
        string primeiro = Guid.NewGuid().ToString("N");
        string segundo = Guid.NewGuid().ToString("N");
        var solicitacao = new SolicitacaoTrabalho("leve", 1, 0, 16, 16, DuracaoSegundos: 5);
        var resultado = simulador.Enviar(primeiro, solicitacao, 0);
        simulador.Enviar(segundo, new("pesado", DuracaoSegundos: 5), 1);

        Assert.Equal(resultado, simulador.Enviar(primeiro, solicitacao, 0));
        Assert.All(simulador.ConsultarSaude().Nucleos, nucleo => Assert.True(nucleo.Ocupado));
        Assert.Throws<InvalidOperationException>(() => simulador.Enviar(Guid.NewGuid().ToString("N"), new("leve"), 0));
        Assert.Throws<InvalidOperationException>(() => simulador.Enviar(primeiro, solicitacao with { Coluna = 32 }, 0));
        Assert.Throws<InvalidOperationException>(() => simulador.Enviar(primeiro, solicitacao with { Passagem = 1 }, 0));
        Assert.Throws<InvalidOperationException>(() => simulador.Enviar(primeiro, solicitacao with { Quadro = 2 }, 0));

        simulador.Cancelar(primeiro);
        await Aguardar(() => simulador.ConsultarTrabalho(primeiro).Estado == EstadosTrabalho.Cancelado);
        Assert.True(simulador.ConsultarSaude().Nucleos[1].Ocupado);

        simulador.Cancelar(segundo);
        await Aguardar(() => !simulador.ConsultarSaude().Nucleos[1].Ocupado);
    }

    [Fact]
    public async Task TarefasDuramOPeriodoPedidoECompartilhamDoisNucleosEmQuatroVagas()
    {
        var primeira = new Simulador("A");
        var segunda = new Simulador("B");
        primeira.ConfigurarFalha(false, 0);
        segunda.ConfigurarFalha(false, 0);
        string[] ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        try
        {
            var leve = new SolicitacaoTrabalho("leve", DuracaoSegundos: 1);
            var pesada = leve with { Nivel = "pesado" };
            primeira.Enviar(ids[0], leve, 0);
            primeira.Enviar(ids[1], leve, 1);
            segunda.Enviar(ids[2], pesada, 0);
            Assert.True(primeira.ConsultarSaude().CpuTotal < segunda.ConsultarSaude().CpuTotal);
            primeira.Enviar(ids[3], leve, 2);
            Assert.Equal(2, primeira.ConsultarSaude().Nucleos.Length);
            Assert.Equal(3, primeira.ConsultarSaude().Vagas.Count(vaga => vaga.Ocupado));
            await Task.Delay(50);
            Assert.Equal(EstadosTrabalho.Executando, primeira.ConsultarTrabalho(ids[0]).Estado);
            await Aguardar(() => primeira.ConsultarSaude().Vagas.All(vaga => !vaga.Ocupado) &&
                segunda.ConsultarSaude().Vagas.All(vaga => !vaga.Ocupado));
            Assert.Equal(EstadosTrabalho.Concluido, primeira.ConsultarTrabalho(ids[0]).Estado);
            Assert.True(primeira.ConsultarTrabalho(ids[0]).TempoMs >= 1000);
            Assert.Equal(1, primeira.ConsultarSaude().TrabalhosConcluidos - 2);
        }
        finally
        {
            primeira.Cancelar(ids[0]);
            primeira.Cancelar(ids[1]);
            primeira.Cancelar(ids[3]);
            segunda.Cancelar(ids[2]);
        }
    }

    private static async Task Aguardar(Func<bool> condicao)
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condicao())
        {
            await Task.Delay(10, limite.Token);
        }
    }
}
