using Balanceador.Clientes;
using Balanceador.Servicos;

namespace Microsoft.Extensions.DependencyInjection;

public static class InstalarDependencias
{
    public static string AdicionarBalanceador(this IServiceCollection servicos, IConfiguration configuracao)
    {
        string modo = configuracao["Balancer:Mode"] ?? "Simulator";
        if (modo is not ("Simulator" or "Hardware"))
        {
            throw new InvalidOperationException("Mode: Simulator ou Hardware.");
        }

        Simulador[] simuladores = modo == "Simulator"
            ? [new Simulador("SIM-A"), new Simulador("SIM-B")]
            : [];

        foreach (Simulador simulador in simuladores)
        {
            servicos.AddSingleton(simulador);
        }

        PlacaHttp[] placas = CriarPlacas(configuracao, modo, simuladores.Length);
        int intervaloAtribuicaoMs = configuracao.GetValue("Balancer:IntervaloAtribuicaoMs", 1000);
        var balanceador = new BalanceadorDeCarga(placas, intervaloAtribuicaoMs: intervaloAtribuicaoMs);

        servicos.AddSingleton(balanceador);
        servicos.AddHostedService<TrabalhadorBalanceamento>();

        return modo;
    }

    private static PlacaHttp[] CriarPlacas(IConfiguration configuracao, string modo, int quantidadeSimuladores)
    {
        string enderecoSimulador = (configuracao["Balancer:SimulatorBaseUrl"]
            ?? configuracao["Urls"]?.Split(';')[0]
            ?? "http://127.0.0.1:5080").TrimEnd('/');

        string[] enderecos = modo == "Simulator"
            ? Enumerable.Range(0, quantidadeSimuladores).Select(indice => $"{enderecoSimulador}/sim/{indice}/").ToArray()
            : configuracao.GetSection("Balancer:Nodes").Get<string[]>() ?? [];

        if (enderecos.Length == 0)
        {
            throw new InvalidOperationException("Configure ao menos uma placa.");
        }

        return enderecos.Select((endereco, indice) =>
        {
            string id = modo == "Simulator" ? $"SIM-{(char)('A' + indice)}" : $"ESP32-{indice + 1}";
            var cliente = new HttpClient
            {
                BaseAddress = new Uri(endereco.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(2)
            };
            return new PlacaHttp(id, cliente);
        }).ToArray();
    }
}
