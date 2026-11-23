using System.Globalization;
using Balanceador.Contratos;
using Balanceador.Servicos;

namespace Balanceador.Endpoints;

public static class SimuladorEndpoints
{
    public static void MapearEndpointsSimulador(this WebApplication aplicacao)
    {
        Simulador[] simuladores = aplicacao.Services.GetServices<Simulador>().ToArray();

        Simulador ObterSimulador(int indice) =>
            indice >= 0 && indice < simuladores.Length
                ? simuladores[indice]
                : throw new KeyNotFoundException();

        aplicacao.MapGet("/sim/{indice:int}/health", (int indice) => ObterSimulador(indice).ConsultarSaude());

        aplicacao.MapPost("/sim/{indice:int}/jobs", async (
            int indice,
            HttpRequest requisicao,
            CancellationToken cancelamento) =>
        {
            IFormCollection formulario = await requisicao.ReadFormAsync(cancelamento);
            string destino = formulario["vaga"].ToString();
            if (destino is not ("0" or "1" or "2" or "3"))
            {
                throw new ArgumentException("Vaga inválida.");
            }

            int vaga = int.Parse(destino, CultureInfo.InvariantCulture);
            var solicitacao = new SolicitacaoTrabalho(
                formulario["nivel"].ToString(),
                LerNumeroFormulario(formulario, "quadro"),
                LerNumeroFormulario(formulario, "passagem"),
                LerNumeroFormulario(formulario, "coluna"),
                LerNumeroFormulario(formulario, "linha"),
                LerNumeroFormulario(formulario, "larguraImagem"),
                LerNumeroFormulario(formulario, "alturaImagem"),
                LerNumeroFormulario(formulario, "duracaoSegundos"));

            ResultadoTrabalho resultado = ObterSimulador(indice).Enviar(formulario["id"].ToString(), solicitacao, vaga);
            return Results.Json(resultado, statusCode: StatusCodes.Status202Accepted);
        });

        aplicacao.MapGet("/sim/{indice:int}/jobs", (int indice, string id) =>
            ObterSimulador(indice).ConsultarTrabalho(id));

        aplicacao.MapGet("/sim/{indice:int}/jobs/{id}/tile", (int indice, string id) =>
        {
            PublicacaoBloco? bloco = ObterSimulador(indice).ConsultarBloco(id);
            return bloco is null ? Results.NoContent() : Results.Json(bloco);
        });

        aplicacao.MapPost("/sim/{indice:int}/jobs/{id}/next", async (
            int indice,
            string id,
            HttpRequest requisicao,
            CancellationToken cancelamento) =>
        {
            IFormCollection formulario = await requisicao.ReadFormAsync(cancelamento);
            int publicacao = LerNumeroFormulario(formulario, "publicacao", "Coordenadas inválidas.");
            var proximoBloco = new BlocoImagem(
                LerNumeroFormulario(formulario, "quadro", "Coordenadas inválidas."),
                LerNumeroFormulario(formulario, "passagem", "Coordenadas inválidas."),
                LerNumeroFormulario(formulario, "coluna", "Coordenadas inválidas."),
                LerNumeroFormulario(formulario, "linha", "Coordenadas inválidas."));

            ObterSimulador(indice).AvancarBloco(id, publicacao, proximoBloco);
            return Results.Accepted();
        });

        aplicacao.MapPost("/sim/{indice:int}/cancel", async (
            int indice,
            HttpRequest requisicao,
            CancellationToken cancelamento) =>
        {
            IFormCollection formulario = await requisicao.ReadFormAsync(cancelamento);
            ObterSimulador(indice).Cancelar(formulario["id"].ToString());
            return Results.Accepted();
        });

        aplicacao.MapPut("/api/simulator/{indice:int}/fault", (int indice, FalhaSimulador falha) =>
        {
            ObterSimulador(indice).ConfigurarFalha(falha.Offline, falha.AtrasoMs, falha.CpuPorNucleo);
            return Results.NoContent();
        });
    }

    private static int LerNumeroFormulario(IFormCollection formulario, string campo, string? mensagemErro = null)
    {
        string texto = formulario[campo].ToString();
        bool apenasDigitos = texto.Length is >= 1 and <= 7 && texto.All(caractere => caractere is >= '0' and <= '9');

        if (!apenasDigitos || !int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out int valor))
        {
            throw new ArgumentException(mensagemErro ?? $"Campo {campo} inválido.");
        }

        return valor;
    }
}
