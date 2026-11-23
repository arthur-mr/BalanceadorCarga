using System.Globalization;
using Balanceador.Contratos;
using Balanceador.Modelos;
using Balanceador.Servicos;

namespace Balanceador.Endpoints;

public static class PainelEndpoints
{
    public static void MapearEndpointsPainel(this WebApplication aplicacao, string modo)
    {
        aplicacao.MapGet("/api/info", () => new
        {
            modo,
            duracaoSegundos = BalanceadorDeCarga.DuracaoTarefaSegundos,
            perfis = PerfilRenderizacao.Todos
        });

        aplicacao.MapGet("/api/painel/state", (BalanceadorDeCarga balanceador) => balanceador.ObterPainel());

        aplicacao.MapGet("/api/painel/events", (
            HttpContext contexto,
            CancellationToken cancelamento,
            BalanceadorDeCarga balanceador) =>
            StreamingEventos.EnviarEventosAsync(contexto, cancelamento, balanceador.ObterPainel));

        aplicacao.MapPost("/api/tasks", (NovaTarefa tarefa, BalanceadorDeCarga balanceador) =>
        {
            Trabalho trabalho = balanceador.AdicionarTarefa(tarefa.Nivel);
            return Results.Accepted(value: new { trabalho.Numero });
        });

        aplicacao.MapPut("/api/boards/{id}/participation", (
            string id,
            Participacao participacao,
            BalanceadorDeCarga balanceador) =>
        {
            balanceador.AlterarParticipacao(id, participacao.Habilitada);
            return Results.NoContent();
        });

        aplicacao.MapPost("/api/painel/imagem/reset", (BalanceadorDeCarga balanceador) =>
        {
            balanceador.ResetarImagem();
            return Results.NoContent();
        });

        aplicacao.MapGet("/api/painel/imagem", (
            string sessao,
            HttpContext contexto,
            BalanceadorDeCarga balanceador) =>
        {
            var imagem = balanceador.ObterPixelsImagem(sessao);
            contexto.Response.Headers.CacheControl = "no-store";
            if (imagem is null)
            {
                return Results.NotFound();
            }

            contexto.Response.Headers["X-Revisao"] = imagem.Value.Revisao.ToString(CultureInfo.InvariantCulture);
            return Results.Bytes(imagem.Value.Pixels, "application/octet-stream");
        });
    }
}
