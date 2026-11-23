using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Balanceador.Clientes;
using Balanceador.Contratos;
using Balanceador.Modelos;
using Xunit;

namespace Balanceador.Testes.Integracoes;

public class TestesIntegracao
{
    [Fact]
    public async Task FluxoCompletoDoPainelEProtocoloHttpDasPlacas()
    {
        var raiz = new DirectoryInfo(AppContext.BaseDirectory);
        while (raiz is not null && !File.Exists(Path.Combine(raiz.FullName, "balanceador", "Balanceador", "Balanceador.csproj")))
        {
            raiz = raiz.Parent;
        }
        Assert.NotNull(raiz);

        var escuta = new TcpListener(IPAddress.Loopback, 0);
        escuta.Start();
        int porta = ((IPEndPoint)escuta.LocalEndpoint).Port;
        escuta.Stop();

        string endereco = $"http://127.0.0.1:{porta}";
        var inicio = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.Combine(raiz.FullName, "balanceador", "Balanceador"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        inicio.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Balanceador.dll"));
        foreach (var argumento in new[] { "--urls", endereco, "--Balancer:Mode", "Simulator", "--Balancer:SimulatorBaseUrl", endereco })
        {
            inicio.ArgumentList.Add(argumento);
        }

        using var processo = Process.Start(inicio)!;
        var saida = processo.StandardOutput.ReadToEndAsync();
        var erros = processo.StandardError.ReadToEndAsync();
        using var cliente = new HttpClient { BaseAddress = new Uri(endereco), Timeout = TimeSpan.FromSeconds(5) };

        try
        {
            await Aguardar(async () =>
            {
                try
                {
                    return (await cliente.GetAsync("/api/info")).IsSuccessStatusCode;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            });

            Assert.Contains("Distribuição de carga", await cliente.GetStringAsync("/"));
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/app.js")).StatusCode);

            var info = await cliente.GetFromJsonAsync<JsonElement>("/api/info");
            Assert.Equal("Simulator", info.GetProperty("modo").GetString());
            Assert.Equal(20, info.GetProperty("duracaoSegundos").GetInt32());
            Assert.False(info.TryGetProperty("protocolo", out _));

            await Aguardar(async () => (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Placas.All(placa => placa.Online));

            using var origemExterna = new HttpRequestMessage(HttpMethod.Post, "/api/tasks")
            {
                Content = JsonContent.Create(new { nivel = "leve" })
            };
            origemExterna.Headers.Add("Origin", "https://exemplo.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.SendAsync(origemExterna)).StatusCode);

            Assert.Equal(HttpStatusCode.BadRequest, (await cliente.PostAsJsonAsync("/api/tasks", new { nivel = "invalido" })).StatusCode);

            var transporte = new PlacaHttp("teste", CriarCliente(endereco + "/sim/0/"));
            string identificador = Guid.NewGuid().ToString("N");
            var solicitacaoSimulador = new SolicitacaoTrabalho("leve", DuracaoSegundos: 1);
            await transporte.Enviar(identificador, solicitacaoSimulador, 3, default);
            await transporte.Enviar(identificador, solicitacaoSimulador, 3, default);
            await Aguardar(async () => !(await transporte.ConsultarSaude(default)).Vagas[3].Ocupado);
            var resultado = await transporte.ConsultarTrabalho(identificador, default);
            Assert.Equal(EstadosTrabalho.Concluido, resultado.Estado);

            using (var invalido = await cliente.PostAsync("/sim/0/jobs", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["nivel"] = "leve",
                ["vaga"] = "9"
            })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
            }

            (await cliente.PutAsJsonAsync("/api/simulator/0/fault", new { offline = true, atrasoMs = 0 })).EnsureSuccessStatusCode();
            await Aguardar(async () => !(await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Placas[0].Online);
            (await cliente.PutAsJsonAsync("/api/simulator/0/fault", new { offline = false, atrasoMs = 0 })).EnsureSuccessStatusCode();
            await Aguardar(async () => (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Placas[0].Online);

            var adicionada = await cliente.PostAsJsonAsync("/api/tasks", new { nivel = "leve", duracaoSegundos = 1 });
            adicionada.EnsureSuccessStatusCode();
            long numero = (await adicionada.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("numero").GetInt64();

            await Aguardar(async () => (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Placas
                .Any(placa => placa.Tarefas.Any(tarefa => tarefa.Numero == numero)));

            var painel = (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!;
            await Aguardar(async () => (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Imagem!.BlocosConcluidos >= 3);

            var imagemPainel = (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Imagem!;
            var pixelsPainel = await cliente.GetByteArrayAsync($"/api/painel/imagem?sessao={imagemPainel.Sessao}");
            Assert.Equal(512 * 384 * 3, pixelsPainel.Length);
            Assert.Contains(pixelsPainel, valor => valor != 0);

            (await cliente.PostAsync("/api/painel/imagem/reset", null)).EnsureSuccessStatusCode();
            Assert.NotEqual(imagemPainel.Sessao, (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Imagem!.Sessao);
            Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/api/painel/imagem?sessao={imagemPainel.Sessao}")).StatusCode);

            string proprietaria = painel.Placas.Single(placa => placa.Tarefas.Any(tarefa => tarefa.Numero == numero)).Id;
            (await cliente.PutAsJsonAsync($"/api/boards/{proprietaria}/participation", new { habilitada = false })).EnsureSuccessStatusCode();

            await Aguardar(async () => (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Placas
                .Any(placa => placa.Id != proprietaria && placa.Tarefas.Any(tarefa => tarefa.Numero == numero)));

            painel = (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!;
            Assert.Single(painel.Placas.SelectMany(placa => placa.Tarefas));
            Assert.Contains("única placa disponível", painel.UltimaDecisao!.Explicacao);

            (await cliente.PutAsJsonAsync($"/api/boards/{proprietaria}/participation", new { habilitada = true })).EnsureSuccessStatusCode();

            using var eventosSimples = await cliente.GetAsync("/api/painel/events", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal("text/event-stream", eventosSimples.Content.Headers.ContentType!.MediaType);
            using var leituraSimples = new StreamReader(await eventosSimples.Content.ReadAsStreamAsync());
            using var limiteSimples = new CancellationTokenSource(5000);
            string linhaSimples = (await leituraSimples.ReadLineAsync(limiteSimples.Token))!;
            Assert.StartsWith("data: ", linhaSimples);
            var estadoSimples = JsonDocument.Parse(linhaSimples[6..]).RootElement;
            Assert.True(estadoSimples.TryGetProperty("fila", out _));
            Assert.True(estadoSimples.TryGetProperty("placas", out _));
        }
        finally
        {
            if (!processo.HasExited)
            {
                processo.Kill(entireProcessTree: true);
            }
            await processo.WaitForExitAsync();
            await saida;
            await erros;
        }

        static HttpClient CriarCliente(string enderecoBase) =>
            new() { BaseAddress = new Uri(enderecoBase), Timeout = TimeSpan.FromSeconds(2) };
    }

    private static async Task Aguardar(Func<Task<bool>> condicao)
    {
        var relogio = Stopwatch.StartNew();
        while (!await condicao())
        {
            if (relogio.Elapsed > TimeSpan.FromSeconds(45))
            {
                throw new TimeoutException("Condição de integração não atingida.");
            }
            await Task.Delay(25);
        }
    }
}
