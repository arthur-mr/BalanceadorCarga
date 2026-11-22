using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Balanceador.Contratos;
using Balanceador.Interfaces;

namespace Balanceador.Clientes;

public sealed class PlacaHttp(string id, HttpClient cliente) : IPlaca
{
    private const int LimiteBytesPublicacaoBloco = 4096;
    private static readonly TimeSpan TimeoutLeitura = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions OpcoesJsonWeb = new(JsonSerializerDefaults.Web);

    public string Id => id;

    public async Task<SaudePlaca> ConsultarSaude(CancellationToken cancelamento) =>
        await cliente.GetFromJsonAsync<SaudePlaca>("health", cancelamento)
        ?? throw new IOException("Saúde vazia.");

    public async Task<ResultadoTrabalho> Enviar(
        string identificador,
        SolicitacaoTrabalho solicitacao,
        int vaga,
        CancellationToken cancelamento)
    {
        using var corpo = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = identificador,
            ["nivel"] = solicitacao.Nivel,
            ["vaga"] = TextoInvariante(vaga),
            ["quadro"] = TextoInvariante(solicitacao.Quadro),
            ["passagem"] = TextoInvariante(solicitacao.Passagem),
            ["coluna"] = TextoInvariante(solicitacao.Coluna),
            ["linha"] = TextoInvariante(solicitacao.Linha),
            ["larguraImagem"] = TextoInvariante(solicitacao.LarguraImagem),
            ["alturaImagem"] = TextoInvariante(solicitacao.AlturaImagem),
            ["tipo"] = solicitacao.Tipo,
            ["duracaoSegundos"] = TextoInvariante(solicitacao.DuracaoSegundos)
        });

        using var resposta = await cliente.PostAsync("jobs", corpo, cancelamento);
        resposta.EnsureSuccessStatusCode();

        return await resposta.Content.ReadFromJsonAsync<ResultadoTrabalho>(cancelamento)
            ?? throw new IOException("Resposta vazia.");
    }

    public async Task<ResultadoTrabalho> ConsultarTrabalho(string identificador, CancellationToken cancelamento) =>
        await cliente.GetFromJsonAsync<ResultadoTrabalho>($"jobs?id={Uri.EscapeDataString(identificador)}", cancelamento)
        ?? throw new IOException("Resultado vazio.");

    public async Task<byte[]> ObterImagem(string identificador, int tamanho, CancellationToken cancelamento)
    {
        using var limite = CriarTimeoutLeitura(cancelamento);
        using var resposta = await cliente.GetAsync(
            $"jobs/{Uri.EscapeDataString(identificador)}/image",
            HttpCompletionOption.ResponseHeadersRead,
            limite.Token);

        resposta.EnsureSuccessStatusCode();

        long? tamanhoDeclarado = resposta.Content.Headers.ContentLength;
        if (tamanho != SolicitacaoTrabalho.BytesBloco || (tamanhoDeclarado.HasValue && tamanhoDeclarado.Value != tamanho))
        {
            throw new IOException("Tamanho de imagem inválido.");
        }

        using var fluxo = await resposta.Content.ReadAsStreamAsync(limite.Token);
        var imagem = new byte[tamanho];
        await fluxo.ReadExactlyAsync(imagem, limite.Token);

        var byteExcedente = new byte[1];
        if (await fluxo.ReadAsync(byteExcedente, limite.Token) != 0)
        {
            throw new IOException("Imagem excede o tamanho esperado.");
        }

        return imagem;
    }

    public async Task Cancelar(string identificador, CancellationToken cancelamento)
    {
        using var corpo = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = identificador
        });

        using var resposta = await cliente.PostAsync("cancel", corpo, cancelamento);
        resposta.EnsureSuccessStatusCode();
    }

    public async Task<PublicacaoBloco?> ConsultarBloco(string identificador, CancellationToken cancelamento)
    {
        using var limite = CriarTimeoutLeitura(cancelamento);
        using var resposta = await cliente.GetAsync(
            $"jobs/{Uri.EscapeDataString(identificador)}/tile",
            HttpCompletionOption.ResponseHeadersRead,
            limite.Token);

        if (resposta.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        resposta.EnsureSuccessStatusCode();

        byte[] dados = await LerBytesComLimiteAsync(
            resposta.Content,
            LimiteBytesPublicacaoBloco,
            "Publicação de imagem excede o limite.",
            limite.Token);

        var bloco = JsonSerializer.Deserialize<PublicacaoBloco>(dados, OpcoesJsonWeb);
        if (bloco is null || bloco.Publicacao < 0 || bloco.Pixels is not { Length: SolicitacaoTrabalho.BytesBloco })
        {
            throw new IOException("Publicação de imagem inválida.");
        }

        return bloco;
    }

    public async Task AvancarBloco(
        string identificador,
        int publicacao,
        BlocoImagem bloco,
        CancellationToken cancelamento)
    {
        using var corpo = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["publicacao"] = TextoInvariante(publicacao),
            ["quadro"] = TextoInvariante(bloco.Quadro),
            ["passagem"] = TextoInvariante(bloco.Passagem),
            ["coluna"] = TextoInvariante(bloco.Coluna),
            ["linha"] = TextoInvariante(bloco.Linha)
        });

        using var resposta = await cliente.PostAsync($"jobs/{Uri.EscapeDataString(identificador)}/next", corpo, cancelamento);
        resposta.EnsureSuccessStatusCode();
    }

    private static string TextoInvariante(int valor) => valor.ToString(CultureInfo.InvariantCulture);

    private static CancellationTokenSource CriarTimeoutLeitura(CancellationToken cancelamento)
    {
        var limite = CancellationTokenSource.CreateLinkedTokenSource(cancelamento);
        limite.CancelAfter(TimeoutLeitura);
        return limite;
    }

    private static async Task<byte[]> LerBytesComLimiteAsync(
        HttpContent conteudo,
        int limiteBytes,
        string mensagemExcesso,
        CancellationToken cancelamento)
    {
        using var fluxo = await conteudo.ReadAsStreamAsync(cancelamento);
        var buffer = new byte[limiteBytes + 1];
        int totalLido = 0;

        while (totalLido < buffer.Length)
        {
            int bytesLidos = await fluxo.ReadAsync(buffer.AsMemory(totalLido), cancelamento);
            if (bytesLidos == 0)
            {
                break;
            }
            totalLido += bytesLidos;
        }

        if (totalLido > limiteBytes)
        {
            throw new IOException(mensagemExcesso);
        }

        return buffer.AsSpan(0, totalLido).ToArray();
    }
}
