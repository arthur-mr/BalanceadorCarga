namespace Balanceador.Middlewares;

public sealed class ValidarOrigemETratarErrosMiddleware(RequestDelegate proximo)
{
    public async Task InvokeAsync(HttpContext contexto)
    {
        if (contexto.Request.Method is "POST" or "PUT" or "DELETE")
        {
            string origem = contexto.Request.Headers.Origin.ToString();
            string origemEsperada = $"{contexto.Request.Scheme}://{contexto.Request.Host}";
            if (origem.Length > 0 && origem != origemEsperada)
            {
                contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        try
        {
            await proximo(contexto);
        }
        catch (Exception erro) when (erro is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            if (contexto.Response.HasStarted)
            {
                throw;
            }

            contexto.Response.StatusCode = erro switch
            {
                ArgumentException => StatusCodes.Status400BadRequest,
                KeyNotFoundException => StatusCodes.Status404NotFound,
                IOException => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status409Conflict
            };

            await contexto.Response.WriteAsJsonAsync(new { erro = erro.Message }, contexto.RequestAborted);
        }
    }
}
