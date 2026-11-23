using Balanceador.Endpoints;
using Balanceador.Middlewares;

var construtor = WebApplication.CreateBuilder(args);
string modo = construtor.Services.AdicionarBalanceador(construtor.Configuration);

var aplicacao = construtor.Build();
aplicacao.UseMiddleware<ValidarOrigemETratarErrosMiddleware>();
aplicacao.UseDefaultFiles();
aplicacao.UseStaticFiles();

aplicacao.MapearEndpointsPainel(modo);

if (modo == "Simulator")
{
    aplicacao.MapearEndpointsSimulador();
}

aplicacao.Run();
