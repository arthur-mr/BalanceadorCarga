# Distribuição de tarefas entre dois ESP32

O painel demonstra uma ideia: **a próxima tarefa vai para a placa disponível com menor uso de CPU**.

A tela contém:

- Três botões para adicionar uma tarefa **leve**, **média** ou **pesada**.
- Dois cartões com a CPU medida e as tarefas de cada ESP32.
- Uma fila de espera.
- A explicação da última decisão de distribuição.
- Um botão para retirar ou reincluir cada placa no balanceamento.
- Uma imagem de 512 × 384 pixels sendo montada pelas mesmas tarefas, com um botão **Resetar imagem**.

Todas as tarefas duram aproximadamente 20 segundos. O nível altera o esforço de processamento (8, 32 ou 96 amostras por pixel e intensidades alvo de 25%, 55% ou 85%). No modo de simulação, a CPU exibida é sintética e identificada no painel.

---

## Requisitos

- **SDK .NET 10** para compilar e executar o balanceador.
- **Node.js** (opcional) para validar sintaxe e executar os testes automatizados da interface.
- **PlatformIO** (extensão no VS Code ou CLI `pio`) e **ESP32 dual-core (`esp32dev`)** na mesma rede local do computador, caso utilize o modo `Hardware`.

---

## 1. Configurar e gravar o firmware nos ESP32

1. Pare o balanceador caso esteja rodando.
2. Na pasta `firmware/esp32`, crie o arquivo local de configuração (ignorado pelo Git) a partir do exemplo:

```powershell
cd .\firmware\esp32
if (!(Test-Path .\include\config.local.h)) {
    Copy-Item .\include\config.local.example.h .\include\config.local.h
}
```

3. Edite `firmware/esp32/include/config.local.h` com as credenciais e identidade de cada placa (`WIFI_SSID`, `WIFI_PASS`, `NODE_NAME` e `WIFI_FALLBACK_MAC`):
   - Na placa B já identificada com MAC de fábrica zerado, mantenha o endereço alternativo exclusivo `02:11:22:33:44:55` (não o reutilize em outros dispositivos).
   - Placas com MAC de fábrica válido preservam o MAC original automaticamente.
4. Conecte a placa via USB, descubra a porta serial, compile, grave e confira o IP no monitor serial:

```powershell
pio device list
pio run
pio run -t upload --upload-port COM3
pio device monitor --port COM3 -b 115200
```

*(A primeira compilação no PlatformIO exige acesso à internet para baixar o toolchain. Feche o monitor serial antes de gravar a segunda placa e ajuste a porta `COM` conforme seu ambiente.)*

### Funcionamento do firmware

- **Arduino + ESP-IDF:** o projeto em `firmware/esp32` compila o framework Arduino como componente ESP-IDF sobre FreeRTOS com estatísticas de tempo ocioso habilitadas (`sdkconfig.defaults`) para medir a CPU por núcleo a cada segundo.
- **Vagas e núcleos:** há 4 vagas lógicas (índices `0` a `3`) mapeadas aos 2 núcleos físicos por `vaga % 2`.
- **Buffers e proteção:** cada vaga aceita um trabalho por vez, usa buffers RGB de 768 bytes (cálculo e publicação), possui timeout local de 300 segundos e acende o LED interno (pino `2`) enquanto houver alguma vaga ocupada.

---

## 2. Iniciar o balanceador

Encerre qualquer execução anterior com `Ctrl+C`.

### Modo Hardware (com os ESP32 reais)

1. Confira os IPs exibidos no monitor serial das placas e atualize `Balancer:Nodes` em `balanceador/Balanceador/appsettings.json`:

```json
"Balancer": {
  "Mode": "Hardware",
  "Nodes": ["http://172.20.10.6", "http://172.20.10.7"],
  "JobTimeoutSeconds": 120
}
```

*(Ou deixe `Mode` padrão e informe `-Mode Hardware` no script abaixo.)*

2. Inicie o servidor:

```powershell
cd .\balanceador
.\scripts\start.ps1 -Mode Hardware
```

### Modo Simulator (sem placas físicas)

Para testar e demonstrar o funcionamento sem ESP32 físicos:

```powershell
cd .\balanceador
.\scripts\start.ps1 -Mode Simulator
```

Abra **http://127.0.0.1:5080** no navegador.

---

## 3. Usar o painel e demonstrar a distribuição

### Como funciona a imagem compartilhada

- As tarefas dos três botões executam ray tracing repetidamente em blocos de 16 × 16 pixels de uma cena 3D (esferas, sombras e piso quadriculado).
- Cada tarefa publica blocos concluídos e recebe a próxima posição da imagem enquanto continua executando durante os 20 segundos.
- A imagem de 512 × 384 pixels é preenchida do centro para as bordas em 4 passagens de refinamento (**3.072 blocos** no total). Sem tarefas em execução, a imagem pausa.
- **Resetar imagem** limpa os pixels e inicia uma nova sessão de imagem sem cancelar as tarefas em andamento nem alterar a distribuição por CPU.

### Roteiro de demonstração

1. Adicione uma tarefa **Pesada** e observe a CPU subir na placa escolhida.
2. Adicione duas tarefas **Leves**, aguardando as atribuições.
3. Adicione outra tarefa e leia o card **Última decisão** (que registra o critério do momento do envio).
4. Note que uma placa com mais tarefas leves pode receber a próxima tarefa se tiver menor CPU medida e capacidade disponível.
5. Clique em **Retirar placa** e observe novas tarefas e tarefas interrompidas seguindo para a outra placa.
6. Clique em **Reincluir placa** para que ela volte a receber tarefas.

Tarefas interrompidas por retirada ou falha voltam para a fila com o **mesmo número** e reiniciam o processamento em outra placa (até 3 tentativas; falhas persistentes exibem um aviso).

> Fila, tarefas e imagem ficam apenas em memória. Reiniciar o servidor inicia uma nova sessão. Utilize apenas um balanceador controlando o mesmo par de placas na rede local.

---

## 4. API principal

- `GET /api/info`: modo ativo, versão do protocolo, identificação do painel (`"simples"`), `imagemProgressiva` e duração das tarefas (`20` s).
- `POST /api/tasks`: JSON `{"nivel":"leve"}` (`leve`, `medio` ou `pesado`); retorna `202` com `numero`. Destino e duração são definidos no servidor.
- `GET /api/painel/state`: estado resumido com placas, fila, última decisão, situação, aviso e progresso da imagem.
- `GET /api/painel/events`: envia o mesmo estado via SSE (1 vez por segundo).
- `PUT /api/boards/{id}/participation`: JSON `{"habilitada":false}` para retirar ou reincluir uma placa (`204`).
- `GET /api/painel/imagem?sessao={id}`: bytes RGB lineares de 512 × 384 pixels, com cabeçalho `X-Revisao` e sem cache (`404` para sessão antiga).
- `POST /api/painel/imagem/reset`: limpa os pixels e troca a sessão da imagem (`204`).

---

## 5. Testes e diagnóstico

### Executar testes automatizados

Com o aplicativo fechado:

```powershell
cd .\balanceador
.\scripts\test.ps1
```

O script executa a suíte C# (xUnit), valida a sintaxe JavaScript (`node --check`) e executa os testes da interface (`node --test`).

### Diagnóstico direto em uma placa ESP32

Com o balanceador parado, você pode consultar `/health` ou enviar um trabalho diretamente pelo PowerShell:

```powershell
$placa = 'http://IP_DA_PLACA'
$id = [guid]::NewGuid().ToString('N')

Invoke-RestMethod "$placa/health" | ConvertTo-Json -Depth 6

Invoke-RestMethod "$placa/jobs" -Method Post -Body @{
    id=$id; nivel='leve'; vaga=0; quadro=0; passagem=0; coluna=32; linha=48
    larguraImagem=256; alturaImagem=192; tipo='bloco'; duracaoSegundos=0
}

Invoke-RestMethod "$placa/jobs?id=$id"

# Após concluir um trabalho do tipo 'bloco':
Invoke-WebRequest "$placa/jobs/$id/image" -OutFile bloco.rgb

# Para cancelar durante a execução:
Invoke-RestMethod "$placa/cancel" -Method Post -Body @{ id=$id }
```

O arquivo `bloco.rgb` contém 768 bytes RGB sem cabeçalho. Para testar uma carga contínua, envie `tipo='carga'` e `duracaoSegundos=30` (consulte blocos intermediários com `GET /jobs/{id}/tile` e confirme/avance com `POST /jobs/{id}/next`).
