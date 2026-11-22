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
    public async Task ProtocoloHttpImagensConcorrenciaCancelamentoERecuperacao()
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
            Assert.Equal(5, (await cliente.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("protocolo").GetInt32());
            Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/app.js")).StatusCode);
            await Aguardar(async () => (await ObterEstado(cliente)).GetProperty("placas").EnumerateArray().All(placa => placa.GetProperty("online").GetBoolean()));
            Assert.Equal(HttpStatusCode.BadRequest, (await cliente.PostAsJsonAsync("/api/jobs", new { nivel = "invalido" })).StatusCode);
            (await cliente.PutAsJsonAsync("/api/settings", new { pausado = true, timeoutSegundos = 120, limiteTarefas = 2, intervaloAtribuicaoMs = 0 })).EnsureSuccessStatusCode();

            var transporte = new PlacaHttp("teste", CriarCliente(endereco + "/sim/0/"));
            string identificador = Guid.NewGuid().ToString("N");
            await transporte.Enviar(identificador, new("leve"), 0, default);
            await transporte.Enviar(identificador, new("leve"), 0, default);
            await Aguardar(async () => !(await transporte.ConsultarSaude(default)).Nucleos[0].Ocupado);
            var resultado = await transporte.ConsultarTrabalho(identificador, default);
            Assert.Equal(EstadosTrabalho.Concluido, resultado.Estado);
            var imagem = await transporte.ObterImagem(identificador, resultado.Largura * resultado.Altura * 3, default);
            Assert.Equal(768, imagem.Length);
            using (var invalido = await cliente.PostAsync("/sim/0/jobs", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["nivel"] = "leve",
                ["nucleo"] = "2"
            })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);
            }

            for (int indice = 0; indice < 2; indice++)
            {
                (await cliente.PutAsJsonAsync($"/api/simulator/{indice}/fault", new { offline = false, atrasoMs = 5000 })).EnsureSuccessStatusCode();
            }
            var identificadores = new List<string>();
            for (int indice = 0; indice < 5; indice++)
            {
                var resposta = await cliente.PostAsJsonAsync("/api/jobs", new { nivel = "pesado" });
                resposta.EnsureSuccessStatusCode();
                identificadores.Add((await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
            }
            (await cliente.PutAsJsonAsync("/api/settings", new { pausado = false, timeoutSegundos = 120, limiteTarefas = 2, intervaloAtribuicaoMs = 0 })).EnsureSuccessStatusCode();
            await Aguardar(async () => (await ObterEstado(cliente)).GetProperty("trabalhos").EnumerateArray()
                .Count(trabalho => trabalho.GetProperty("estado").GetString() == EstadosTrabalho.Executando) == 4);
            var estado = await ObterEstado(cliente);
            Assert.All(estado.GetProperty("placas").EnumerateArray(), placa => Assert.Equal(2, placa.GetProperty("ocupados").GetInt32()));
            Assert.Equal(EstadosTrabalho.NaFila, ObterEstado(estado, identificadores[4]));
            foreach (string id in identificadores)
            {
                (await cliente.PostAsync($"/api/jobs/{id}/cancel", null)).EnsureSuccessStatusCode();
            }
            await Aguardar(async () => (await ObterEstado(cliente)).GetProperty("trabalhos").EnumerateArray()
                .All(trabalho => trabalho.GetProperty("estado").GetString() == EstadosTrabalho.Cancelado));

            for (int indice = 0; indice < 2; indice++)
            {
                (await cliente.PutAsJsonAsync($"/api/simulator/{indice}/fault", new { offline = false, atrasoMs = 0 })).EnsureSuccessStatusCode();
            }
            var leve = await cliente.PostAsJsonAsync("/api/jobs", new { nivel = "leve" });
            string idLeve = (await leve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            await Aguardar(async () => ObterEstado(await ObterEstado(cliente), idLeve) == EstadosTrabalho.Concluido);
            var imagemRetida = await cliente.GetByteArrayAsync($"/api/jobs/{idLeve}/image");
            Assert.Equal(768, imagemRetida.Length);
            Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync("/api/jobs/desconhecido/image")).StatusCode);

            (await cliente.PutAsJsonAsync("/api/simulator/0/fault", new { offline = true, atrasoMs = 0 })).EnsureSuccessStatusCode();
            await Aguardar(async () => !(await ObterEstado(cliente)).GetProperty("placas")[0].GetProperty("online").GetBoolean());
            (await cliente.PutAsJsonAsync("/api/simulator/0/fault", new { offline = false, atrasoMs = 0 })).EnsureSuccessStatusCode();
            await Aguardar(async () => (await ObterEstado(cliente)).GetProperty("placas")[0].GetProperty("online").GetBoolean());

            (await cliente.PostAsJsonAsync("/api/render/start", new { nivel = "leve", continuo = false, largura = 128, altura = 96 })).EnsureSuccessStatusCode();
            await Aguardar(async () => (await ObterEstado(cliente)).GetProperty("cena").GetProperty("estado").GetString() == "concluida");
            var cena = (await ObterEstado(cliente)).GetProperty("cena");
            Assert.Equal(192, cena.GetProperty("blocosConcluidos").GetInt32());
            Assert.Equal(100, cena.GetProperty("progresso").GetDouble());
            Assert.Equal(2, cena.GetProperty("contribuicoes").GetArrayLength());
            string sessao = cena.GetProperty("id").GetString()!;
            var cenaRgb = await cliente.GetByteArrayAsync($"/api/render/image?sessao={sessao}&quadro=0");
            Assert.Equal(128 * 96 * 3, cenaRgb.Length);
            Assert.Contains(cenaRgb, valor => valor > 0);
            Assert.Equal(HttpStatusCode.BadRequest, (await cliente.PostAsJsonAsync("/api/render/start", new { nivel = "invalido" })).StatusCode);
            Assert.Equal(sessao, (await ObterEstado(cliente)).GetProperty("cena").GetProperty("id").GetString());
            (await cliente.PutAsJsonAsync("/api/settings", new { pausado = false, timeoutSegundos = 120, limiteTarefas = 4, intervaloAtribuicaoMs = 0 })).EnsureSuccessStatusCode();
            (await cliente.PostAsJsonAsync("/api/experiments/asymmetric", new { duracaoSegundos = 5 })).EnsureSuccessStatusCode();
            await Aguardar(async () => (await ObterEstado(cliente)).GetProperty("trabalhos").EnumerateArray().Count(trabalho =>
                trabalho.GetProperty("solicitacao").GetProperty("tipo").GetString() == "carga" &&
                trabalho.GetProperty("estado").GetString() == EstadosTrabalho.Executando) == 3);
            var novoBloco = await cliente.PostAsJsonAsync("/api/jobs", new { nivel = "leve" });
            novoBloco.EnsureSuccessStatusCode();
            string idBloco = (await novoBloco.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
            await Aguardar(async () => (await ObterEstado(cliente)).GetProperty("decisoes").EnumerateArray().Any(decisao =>
                decisao.GetProperty("tipo").GetString() == "atribuicao" && decisao.GetProperty("trabalho").GetString() == idBloco));
            var escolha = (await ObterEstado(cliente)).GetProperty("decisoes").EnumerateArray().Single(decisao =>
                decisao.GetProperty("tipo").GetString() == "atribuicao" && decisao.GetProperty("trabalho").GetString() == idBloco);
            Assert.Equal("SIM-A", escolha.GetProperty("placa").GetString());
            Assert.Equal(2, escolha.GetProperty("candidatas")[0].GetProperty("tarefas").GetInt32());
            Assert.Equal(1, escolha.GetProperty("candidatas")[1].GetProperty("tarefas").GetInt32());
            (await cliente.PutAsJsonAsync("/api/boards/SIM-B/participation", new { habilitada = false })).EnsureSuccessStatusCode();
            Assert.False((await ObterEstado(cliente)).GetProperty("placas")[1].GetProperty("habilitada").GetBoolean());
            (await cliente.PutAsJsonAsync("/api/boards/SIM-B/participation", new { habilitada = true })).EnsureSuccessStatusCode();
            (await cliente.PostAsync("/api/experiments/clear", null)).EnsureSuccessStatusCode();
            await Aguardar(async () => (await ObterEstado(cliente)).GetProperty("trabalhos").EnumerateArray().All(trabalho =>
                trabalho.GetProperty("solicitacao").GetProperty("tipo").GetString() != "carga" ||
                EstadosTrabalho.EhTerminal(trabalho.GetProperty("estado").GetString()!)));
            (await cliente.PostAsJsonAsync("/api/render/start", new { nivel = "medio", continuo = true, largura = 512, altura = 384 })).EnsureSuccessStatusCode();
            var ampliada = (await ObterEstado(cliente)).GetProperty("cena");
            Assert.Equal(3072, ampliada.GetProperty("totalBlocos").GetInt32());
            Assert.Equal(512 * 384 * 3, (await cliente.GetByteArrayAsync($"/api/render/image?sessao={ampliada.GetProperty("id").GetString()}&quadro=0")).Length);
            Assert.Equal(HttpStatusCode.NotFound, (await cliente.GetAsync($"/api/render/image?sessao={sessao}&quadro=0")).StatusCode);
            (await cliente.PostAsync("/api/render/stop", null)).EnsureSuccessStatusCode();
            Assert.Equal("parada", (await ObterEstado(cliente)).GetProperty("cena").GetProperty("estado").GetString());

            using var eventos = await cliente.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal("text/event-stream", eventos.Content.Headers.ContentType!.MediaType);
            using var leitura = new StreamReader(await eventos.Content.ReadAsStreamAsync());
            using var limite = new CancellationTokenSource(5000);
            Assert.StartsWith("data: ", await leitura.ReadLineAsync(limite.Token));
            using var origemExterna = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
            {
                Content = JsonContent.Create(new { nivel = "leve" })
            };
            origemExterna.Headers.Add("Origin", "https://exemplo.com");
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.SendAsync(origemExterna)).StatusCode);

            Assert.Equal("simples", (await cliente.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("painel").GetString());
            Assert.Equal(20, (await cliente.GetFromJsonAsync<JsonElement>("/api/info")).GetProperty("duracaoSegundos").GetInt32());
            (await cliente.PutAsJsonAsync("/api/settings", new { pausado = false, timeoutSegundos = 120, limiteTarefas = 4, intervaloAtribuicaoMs = 1000 })).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.BadRequest, (await cliente.PostAsJsonAsync("/api/tasks", new { nivel = "invalido" })).StatusCode);
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
            Assert.Contains((await ObterEstado(cliente)).GetProperty("trabalhos").EnumerateArray(), trabalho =>
                trabalho.GetProperty("solicitacao").GetProperty("duracaoSegundos").GetInt32() == 20 &&
                trabalho.GetProperty("estado").GetString() == EstadosTrabalho.Executando);
            (await cliente.PutAsJsonAsync($"/api/boards/{proprietaria}/participation", new { habilitada = false })).EnsureSuccessStatusCode();
            await Aguardar(async () => (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!.Placas
                .Any(placa => placa.Id != proprietaria && placa.Tarefas.Any(tarefa => tarefa.Numero == numero)));
            painel = (await cliente.GetFromJsonAsync<EstadoPainel>("/api/painel/state"))!;
            Assert.Single(painel.Placas.SelectMany(placa => placa.Tarefas));
            Assert.Contains("única placa disponível", painel.UltimaDecisao!.Explicacao);
            (await cliente.PutAsJsonAsync($"/api/boards/{proprietaria}/participation", new { habilitada = true })).EnsureSuccessStatusCode();
            using var eventosSimples = await cliente.GetAsync("/api/painel/events", HttpCompletionOption.ResponseHeadersRead);
            using var leituraSimples = new StreamReader(await eventosSimples.Content.ReadAsStreamAsync());
            using var limiteSimples = new CancellationTokenSource(5000);
            string linhaSimples = (await leituraSimples.ReadLineAsync(limiteSimples.Token))!;
            Assert.StartsWith("data: ", linhaSimples);
            var estadoSimples = JsonDocument.Parse(linhaSimples[6..]).RootElement;
            Assert.True(estadoSimples.TryGetProperty("fila", out _));
            Assert.False(estadoSimples.TryGetProperty("cena", out _));
            Assert.False(estadoSimples.TryGetProperty("decisoes", out _));
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

    private static async Task<JsonElement> ObterEstado(HttpClient cliente) =>
        await cliente.GetFromJsonAsync<JsonElement>("/api/state");

    private static string? ObterEstado(JsonElement estado, string id) =>
        estado.GetProperty("trabalhos").EnumerateArray()
            .Single(trabalho => trabalho.GetProperty("id").GetString() == id)
            .GetProperty("estado").GetString();

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
