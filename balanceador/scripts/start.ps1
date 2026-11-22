param([ValidateSet('Simulator','Hardware')][string]$Mode = 'Simulator', [int]$Port = 5080)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet run --project Balanceador/Balanceador.csproj -- --Balancer:Mode $Mode --urls "http://127.0.0.1:$Port"
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao iniciar a aplicação.' }
} finally { Pop-Location }
