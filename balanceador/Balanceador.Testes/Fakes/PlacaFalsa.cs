using Balanceador.Contratos;
using Balanceador.Interfaces;
using Balanceador.Modelos;

namespace Balanceador.Testes.Fakes;

internal sealed class PlacaFalsa(string id) : IPlaca
{
    public string Id => id;
    public bool Offline { get; set; }
    public bool IgnorarCancelamento { get; set; }
    public bool PerderAceitacao { get; set; }
    public bool CpuValida { get; set; } = true;
    public int QuantidadeVagas { get; set; } = 4;
    public int Envios { get; private set; }
    public long IdadeAmostra { get; set; }
    public double[] Cpu { get; set; } = [0, 0];
    public Dictionary<int, ResultadoTrabalho> Resultados { get; } = [];

    private void Verificar()
    {
        if (Offline)
        {
            throw new IOException("Offline.");
        }
    }

    public Task<SaudePlaca> ConsultarSaude(CancellationToken cancelamento)
    {
        Verificar();
        var nucleos = Enumerable.Range(0, 2).Select(nucleo =>
        {
            var resultado = Resultados.Values.FirstOrDefault(r => r.Nucleo == nucleo && r.Estado == EstadosTrabalho.Executando);
            return new SaudeNucleo(nucleo, resultado is not null, resultado?.Id, resultado?.Progresso ?? 0, Cpu[nucleo]);
        }).ToArray();

        var vagas = Enumerable.Range(0, QuantidadeVagas).Select(indice =>
        {
            var resultado = Resultados.GetValueOrDefault(indice);
            return new SaudeVaga(indice, resultado?.Estado == EstadosTrabalho.Executando, resultado?.Id, resultado?.Progresso ?? 0);
        }).ToArray();

        return Task.FromResult(new SaudePlaca(id, Cpu.Average(), CpuValida, IdadeAmostra, nucleos, vagas, 0, 0, 200000, -45));
    }

    public Task<ResultadoTrabalho> Enviar(string identificador, SolicitacaoTrabalho solicitacao, int vaga, CancellationToken cancelamento)
    {
        Verificar();
        Envios++;
        var perfil = PerfilRenderizacao.Obter(solicitacao.Nivel);
        if (Resultados.GetValueOrDefault(vaga)?.Estado == EstadosTrabalho.Executando)
        {
            throw new InvalidOperationException("Ocupado.");
        }

        var resultado = new ResultadoTrabalho(
            identificador,
            EstadosTrabalho.Executando,
            perfil.Nivel,
            vaga % 2,
            vaga,
            0,
            16,
            16,
            0,
            solicitacao.Quadro,
            solicitacao.Passagem,
            solicitacao.Coluna,
            solicitacao.Linha,
            solicitacao.LarguraImagem,
            solicitacao.AlturaImagem,
            solicitacao.DuracaoSegundos);

        Resultados[vaga] = resultado;
        if (PerderAceitacao)
        {
            throw new IOException("Resposta perdida.");
        }

        return Task.FromResult(resultado);
    }

    public Task<ResultadoTrabalho> ConsultarTrabalho(string identificador, CancellationToken cancelamento)
    {
        Verificar();
        return Task.FromResult(Resultados.Values.Single(resultado => resultado.Id == identificador));
    }

    public Task Cancelar(string identificador, CancellationToken cancelamento)
    {
        Verificar();
        foreach (var (vaga, resultado) in Resultados.ToArray())
        {
            if (resultado.Id == identificador && !IgnorarCancelamento)
            {
                Resultados[vaga] = resultado with { Estado = EstadosTrabalho.Cancelado };
            }
        }

        return Task.CompletedTask;
    }

    public Task<PublicacaoBloco?> ConsultarBloco(string identificador, CancellationToken cancelamento)
    {
        Verificar();
        return Task.FromResult<PublicacaoBloco?>(null);
    }

    public Task AvancarBloco(string identificador, int publicacao, BlocoImagem bloco, CancellationToken cancelamento)
    {
        Verificar();
        return Task.CompletedTask;
    }

    public void Concluir(int vaga) =>
        Resultados[vaga] = Resultados[vaga] with { Estado = EstadosTrabalho.Concluido, Progresso = 100, TempoMs = 100 };
}
