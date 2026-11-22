using System.Text.Json;
using Balanceador.Contratos;
using Balanceador.Modelos;
using Balanceador.Servicos;
using Balanceador.Testes.Fakes;
using Xunit;

namespace Balanceador.Testes.Unitarios;

public class TestesCenaCompartilhada
{
    private static JsonElement ObterEstado(CenaCompartilhada cena) =>
        JsonSerializer.SerializeToElement(cena.ObterEstado(_ => 0), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static void Concluir(Trabalho trabalho)
    {
        var solicitacao = trabalho.Solicitacao;
        trabalho.Placa = "A";
        trabalho.Nucleo = 0;
        trabalho.Estado = EstadosTrabalho.Concluido;
        trabalho.Imagem = Enumerable.Repeat((byte)(solicitacao.Coluna + solicitacao.Linha), SolicitacaoTrabalho.BytesBloco).ToArray();
        trabalho.Resultado = new(trabalho.Id, EstadosTrabalho.Concluido, solicitacao.Nivel, 0, 100, 16, 16, 20,
            solicitacao.Quadro, solicitacao.Passagem, solicitacao.Coluna, solicitacao.Linha);
    }

    [Fact]
    public void AguardaTodosOsBlocosAntesDaPassagemSeguinteEPosicionaPixelsCorretamente()
    {
        var criados = new List<Trabalho>();
        var cena = new CenaCompartilhada("leve", false, solicitacao =>
        {
            var trabalho = new Trabalho { Solicitacao = solicitacao };
            criados.Add(trabalho);
            return trabalho;
        });
        foreach (var trabalho in criados.Take(47)) Concluir(trabalho);
        cena.Atualizar(_ => { });
        Assert.Equal(48, criados.Count);
        Assert.Equal(0, ObterEstado(cena).GetProperty("passagem").GetInt32());
        Assert.Equal(47, ObterEstado(cena).GetProperty("blocosConcluidos").GetInt32());
        Assert.Equal(16, cena.CopiarImagem()[16 * 3]);
        int ultimoBloco = (80 * 128 + 112) * 3;
        Assert.Equal(0, cena.CopiarImagem()[ultimoBloco]);
        Concluir(criados[47]);
        cena.Atualizar(_ => { });
        Assert.Equal(96, criados.Count);
        Assert.Equal(1, ObterEstado(cena).GetProperty("passagem").GetInt32());
        Assert.Equal(192, cena.CopiarImagem()[ultimoBloco]);
    }

    [Fact]
    public void ModoContinuoPreservaQuadroPorIntervaloELimpaAcumulacaoAoMoverLuz()
    {
        var relogio = new RelogioTeste(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        var cena = new CenaCompartilhada("leve", true, solicitacao =>
        {
            var trabalho = new Trabalho { Solicitacao = solicitacao };
            if (solicitacao.Quadro == 0) Concluir(trabalho);
            return trabalho;
        }, relogio);
        for (int passagem = 0; passagem < 4; passagem++) cena.Atualizar(_ => { });
        Assert.Equal("intervalo", ObterEstado(cena).GetProperty("estado").GetString());
        Assert.Equal(1, ObterEstado(cena).GetProperty("quadrosConcluidos").GetInt32());
        Assert.Contains(cena.CopiarImagem(), valor => valor > 0);
        relogio.Avancar(1499);
        cena.Atualizar(_ => { });
        Assert.Equal(0, cena.Quadro);
        relogio.Avancar(1);
        cena.Atualizar(_ => { }, distribuicaoPausada: true);
        Assert.Equal(0, cena.Quadro);
        cena.Atualizar(_ => { });
        Assert.Equal(1, cena.Quadro);
        Assert.Equal(0, ObterEstado(cena).GetProperty("blocosConcluidos").GetInt32());
        Assert.All(cena.CopiarImagem(), valor => Assert.Equal(0, valor));
    }

    [Fact]
    public void EncerraComErroAposTresTentativasDoMesmoBloco()
    {
        int criados = 0;
        var cena = new CenaCompartilhada("leve", true, solicitacao =>
        {
            criados++;
            return new Trabalho { Solicitacao = solicitacao, Estado = EstadosTrabalho.Falhou, Erro = "Falha persistente." };
        });
        cena.Atualizar(_ => { });
        cena.Atualizar(_ => { });
        cena.Atualizar(_ => { });
        Assert.Equal(144, criados);
        Assert.False(cena.Ativa);
        Assert.Equal("falhou", ObterEstado(cena).GetProperty("estado").GetString());
        Assert.Contains("três tentativas", ObterEstado(cena).GetProperty("erro").GetString());
        cena.Atualizar(_ => { });
        Assert.Equal(144, criados);
    }
}
