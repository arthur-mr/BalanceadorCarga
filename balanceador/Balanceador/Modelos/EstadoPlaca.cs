using Balanceador.Contratos;
using Balanceador.Interfaces;

namespace Balanceador.Modelos;

public sealed class EstadoPlaca(IPlaca transporte)
{
    public const int MaximoVagas = 4;

    public IPlaca Transporte { get; } = transporte;
    public bool Online { get; set; }
    public string? Erro { get; set; }
    public SaudePlaca? Saude { get; set; }
    public DateTimeOffset AtualizadoEm { get; set; }
    public bool Habilitada { get; set; } = true;
    public Trabalho?[] Reservas { get; } = new Trabalho?[MaximoVagas];
    public long[] Atribuidos { get; } = new long[MaximoVagas];
    public Queue<AmostraCpu> HistoricoCpu { get; } = new();
    public DateTimeOffset UltimoRegistroCpu { get; set; }

    public bool SuportaImagemProgressiva => Saude?.Versao >= 5;

    public SaudeVaga[] ObterVagas() =>
        Saude?.Vagas
        ?? Saude?.Nucleos
            .Select(nucleo => new SaudeVaga(nucleo.Indice, nucleo.Ocupado, nucleo.TrabalhoId, nucleo.Progresso))
            .ToArray()
        ?? [];

    public bool VagaOcupada(int indice, SaudeVaga[] vagas) =>
        Reservas[indice] is not null || vagas[indice].Ocupado;

    public int ContarVagasOcupadas(SaudeVaga[] vagas)
    {
        int ocupadas = 0;
        for (int indice = 0; indice < vagas.Length; indice++)
        {
            if (VagaOcupada(indice, vagas))
            {
                ocupadas++;
            }
        }
        return ocupadas;
    }

    public int ContarReservasOuVagasOcupadas()
    {
        SaudeVaga[] vagas = ObterVagas();
        int ocupadas = 0;
        for (int indice = 0; indice < Reservas.Length; indice++)
        {
            if (Reservas[indice] is not null || vagas.ElementAtOrDefault(indice)?.Ocupado == true)
            {
                ocupadas++;
            }
        }
        return ocupadas;
    }

    public bool PossuiVagaLivre(int limiteTarefas)
    {
        SaudeVaga[] vagas = ObterVagas();
        if (ContarVagasOcupadas(vagas) >= limiteTarefas)
        {
            return false;
        }

        int limiteVerificacao = Math.Min(limiteTarefas, vagas.Length);
        for (int indice = 0; indice < limiteVerificacao; indice++)
        {
            if (!VagaOcupada(indice, vagas))
            {
                return true;
            }
        }

        return false;
    }
}
