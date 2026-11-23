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

    public SaudeVaga[] ObterVagas() => Saude?.Vagas ?? [];

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

    public bool PossuiVagaLivre()
    {
        SaudeVaga[] vagas = ObterVagas();
        for (int indice = 0; indice < vagas.Length; indice++)
        {
            if (!VagaOcupada(indice, vagas))
            {
                return true;
            }
        }

        return false;
    }
}
