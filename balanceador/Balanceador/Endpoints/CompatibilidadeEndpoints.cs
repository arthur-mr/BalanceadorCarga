using System.Globalization;
using Balanceador.Contratos;
using Balanceador.Modelos;
using Balanceador.Servicos;

namespace Balanceador.Endpoints;

public static class CompatibilidadeEndpoints
{
    public static void MapearEndpointsCompatibilidade(this WebApplication aplicacao)
    {
        aplicacao.MapGet("/api/state", (BalanceadorDeCarga balanceador) => balanceador.ObterEstado());

        aplicacao.MapGet("/api/events", (
            HttpContext contexto,
            CancellationToken cancelamento,
            BalanceadorDeCarga balanceador) =>
            StreamingEventos.EnviarEventosAsync(contexto, cancelamento, balanceador.ObterEstado));

        aplicacao.MapPost("/api/render/start", (InicioRenderizacao inicio, BalanceadorDeCarga balanceador) =>
        {
            balanceador.IniciarCena(inicio.Nivel, inicio.Continuo, inicio.Largura, inicio.Altura);
            return Results.Accepted();
        });

        aplicacao.MapPost("/api/render/stop", (BalanceadorDeCarga balanceador) =>
        {
            balanceador.PararCena();
            return Results.Accepted();
        });

        aplicacao.MapGet("/api/render/image", (
            string sessao,
            int quadro,
            HttpContext contexto,
            BalanceadorDeCarga balanceador) =>
        {
            var imagem = balanceador.ObterImagemCena(sessao, quadro);
            contexto.Response.Headers.CacheControl = "no-store";
            if (imagem is null)
            {
                return Results.NotFound();
            }

            contexto.Response.Headers["X-Revisao"] = imagem.Value.Revisao.ToString(CultureInfo.InvariantCulture);
            return Results.Bytes(imagem.Value.Imagem, "application/octet-stream");
        });

        aplicacao.MapPost("/api/experiments/load", (CargaTeste carga, BalanceadorDeCarga balanceador) =>
        {
            balanceador.AdicionarCarga(carga.Placa, carga.Nivel, carga.Quantidade, carga.DuracaoSegundos);
            return Results.Accepted();
        });

        aplicacao.MapPost("/api/experiments/asymmetric", (CenarioTeste cenario, BalanceadorDeCarga balanceador) =>
        {
            balanceador.IniciarCenarioAssimetrico(cenario.DuracaoSegundos);
            return Results.Accepted();
        });

        aplicacao.MapPost("/api/experiments/clear", (BalanceadorDeCarga balanceador) =>
        {
            balanceador.LimparCargas();
            return Results.Accepted();
        });

        aplicacao.MapPost("/api/jobs", (SolicitacaoTrabalho solicitacao, BalanceadorDeCarga balanceador) =>
        {
            Trabalho trabalho = balanceador.Enfileirar(solicitacao);
            return Results.Accepted(value: new { trabalho.Id });
        });

        aplicacao.MapPost("/api/jobs/{id}/cancel", (string id, BalanceadorDeCarga balanceador) =>
            balanceador.Cancelar(id) ? Results.Accepted() : Results.NotFound());

        aplicacao.MapGet("/api/jobs/{id}/image", (string id, BalanceadorDeCarga balanceador) =>
        {
            var imagem = balanceador.ObterImagem(id);
            return imagem is null
                ? Results.NotFound()
                : Results.Bytes(imagem.Value.Imagem, "application/octet-stream");
        });

        aplicacao.MapPut("/api/settings", (Configuracao configuracao, BalanceadorDeCarga balanceador) =>
        {
            balanceador.Configurar(
                configuracao.Pausado,
                configuracao.TimeoutSegundos,
                configuracao.LimiteTarefas,
                configuracao.IntervaloAtribuicaoMs);
            return Results.NoContent();
        });
    }
}
