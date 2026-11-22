using System.Text.Json;

namespace Balanceador.Endpoints;

internal static class StreamingEventos
{
    private static readonly JsonSerializerOptions OpcoesJsonWeb = new(JsonSerializerDefaults.Web);

    public static async Task EnviarEventosAsync(
        HttpContext contexto,
        CancellationToken cancelamento,
        Func<object> obterEstado)
    {
        contexto.Response.ContentType = "text/event-stream";
        contexto.Response.Headers.CacheControl = "no-cache";

        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            do
            {
                string payload = JsonSerializer.Serialize(obterEstado(), OpcoesJsonWeb);
                await contexto.Response.WriteAsync($"data: {payload}\n\n", cancelamento);
                await contexto.Response.Body.FlushAsync(cancelamento);
            }
            while (await temporizador.WaitForNextTickAsync(cancelamento));
        }
        catch (OperationCanceledException) when (cancelamento.IsCancellationRequested)
        {
        }
    }
}
