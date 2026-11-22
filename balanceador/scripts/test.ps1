$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet test Balanceador.sln --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Testes C# falharam.' }
    if (Get-Command node -ErrorAction SilentlyContinue) {
        node --check Balanceador/wwwroot/app.js
        if ($LASTEXITCODE -ne 0) { throw 'JavaScript inválido.' }
        node --test Balanceador.Testes/testes-painel.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Testes do painel falharam.' }
    }
} finally { Pop-Location }
