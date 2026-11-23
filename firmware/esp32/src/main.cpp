#include <Arduino.h>
#include <WebServer.h>
#include <base64.h>
#include <uri/UriBraces.h>
#include "conexao_wifi.h"
#include "cena.h"
#include "telemetria_cpu.h"

#if CONFIG_FREERTOS_UNICORE
#error Este firmware exige um ESP32 com dois nucleos.
#endif

constexpr uint8_t pinoLed = 2;
constexpr uint8_t quantidadeVagas = 4;
constexpr uint8_t quantidadeNucleos = 2;
constexpr uint8_t quantidadePerfis = sizeof(perfis) / sizeof(perfis[0]);

constexpr uint32_t timeoutMaximoTrabalhoMs = 300000UL;
constexpr uint32_t janelaOrcamentoUs = 20000UL;
constexpr uint32_t intervaloWifiMs = 5000UL;
constexpr uint32_t quadroMaximo = 1000000UL;
constexpr uint32_t duracaoMaximaSegundos = 120;

enum class Situacao { Executando, Concluido, Cancelado, Falhou };

struct Trabalho {
  char id[33] = {};
  uint8_t perfil = 0;
  BlocoCena bloco;
  uint16_t duracaoSegundos = 0;
};

struct EstadoVaga {
  Trabalho trabalho;
  bool presente = false;
  bool ocupado = false;
  bool cancelar = false;
  Situacao situacao = Situacao::Executando;
  uint32_t inicioMs = 0;
  uint32_t tempoMs = 0;
  BlocoCena blocoAtual;
  uint32_t publicacao = 0;
  bool imagemPronta = false;
};

EstadoVaga estados[quantidadeVagas];
uint8_t imagens[quantidadeVagas][tamanhoMaximoImagem];
uint8_t imagensPublicadas[quantidadeVagas][tamanhoMaximoImagem];
QueueHandle_t filas[quantidadeVagas];
portMUX_TYPE travaEstados = portMUX_INITIALIZER_UNLOCKED;
WebServer servidor(80);
bool wifiPronto = false;
uint32_t trabalhosConcluidos = 0;

EstadoVaga consultarEstado(uint8_t vaga) {
  portENTER_CRITICAL(&travaEstados);
  EstadoVaga estado = estados[vaga];
  portEXIT_CRITICAL(&travaEstados);
  return estado;
}

bool trabalhoCancelado(uint8_t vaga) {
  portENTER_CRITICAL(&travaEstados);
  const bool cancelado = estados[vaga].cancelar;
  portEXIT_CRITICAL(&travaEstados);
  return cancelado;
}

const char* nomeSituacao(Situacao situacao) {
  switch (situacao) {
    case Situacao::Concluido: return "concluido";
    case Situacao::Cancelado: return "cancelado";
    case Situacao::Falhou: return "falhou";
    default: return "executando";
  }
}

float calcularProgresso(const EstadoVaga& estado) {
  if (estado.situacao == Situacao::Concluido) {
    return 100.0f;
  }

  const uint32_t tempo = estado.ocupado ? millis() - estado.inicioMs : estado.tempoMs;
  return estado.trabalho.duracaoSegundos
    ? min(100.0f, tempo / (estado.trabalho.duracaoSegundos * 10.0f))
    : 0.0f;
}

void responderErro(int codigo, const char* mensagem) {
  servidor.send(codigo, "application/json", String("{\"erro\":\"") + mensagem + "\"}");
}

bool identificadorValido(const String& identificador) {
  if (identificador.length() != 32) {
    return false;
  }

  for (size_t indice = 0; indice < identificador.length(); ++indice) {
    if (!isxdigit(static_cast<unsigned char>(identificador[indice]))) {
      return false;
    }
  }
  return true;
}

int localizarTrabalho(const String& identificador) {
  for (uint8_t vaga = 0; vaga < quantidadeVagas; ++vaga) {
    const EstadoVaga estado = consultarEstado(vaga);
    if (estado.presente && identificador == estado.trabalho.id) {
      return vaga;
    }
  }
  return -1;
}

void responderResultado(int codigo, uint8_t vaga) {
  const EstadoVaga estado = consultarEstado(vaga);
  const Trabalho& trabalho = estado.trabalho;
  const BlocoCena& bloco = trabalho.bloco;
  const uint32_t tempoMs = estado.ocupado ? millis() - estado.inicioMs : estado.tempoMs;

  char resposta[768];
  snprintf(
    resposta,
    sizeof(resposta),
    "{\"id\":\"%s\",\"estado\":\"%s\",\"nivel\":\"%s\",\"nucleo\":%u,\"vaga\":%u,"
    "\"progresso\":%.2f,\"largura\":16,\"altura\":16,\"tempoMs\":%lu,"
    "\"quadro\":%lu,\"passagem\":%u,\"coluna\":%u,\"linha\":%u,"
    "\"larguraImagem\":%u,\"alturaImagem\":%u,\"duracaoSegundos\":%u}",
    trabalho.id,
    nomeSituacao(estado.situacao),
    perfis[trabalho.perfil].nivel,
    vaga % quantidadeNucleos,
    vaga,
    calcularProgresso(estado),
    static_cast<unsigned long>(tempoMs),
    static_cast<unsigned long>(bloco.quadro),
    bloco.passagem,
    bloco.coluna,
    bloco.linha,
    bloco.largura,
    bloco.altura,
    trabalho.duracaoSegundos);

  servidor.send(codigo, "application/json", resposta);
}

void descansarSeOrcamentoEsgotado(uint32_t& inicioJanela, uint32_t orcamentoUs) {
  const uint32_t usadoUs = micros() - inicioJanela;
  if (usadoUs < orcamentoUs) {
    return;
  }

  const uint32_t descansoMs = usadoUs < janelaOrcamentoUs
    ? (janelaOrcamentoUs - usadoUs + 999) / 1000
    : 1;
  vTaskDelay(pdMS_TO_TICKS(descansoMs));
  inicioJanela = micros();
}

void publicarBlocoSePronto(
    uint8_t vaga,
    const EstadoVaga& estadoInicial,
    uint32_t pixels,
    bool cancelado,
    bool expirou) {
  if (cancelado || expirou || pixels != tamanhoBloco * tamanhoBloco) {
    return;
  }

  portENTER_CRITICAL(&travaEstados);
  if (estados[vaga].publicacao == estadoInicial.publicacao && !estados[vaga].imagemPronta) {
    memcpy(imagensPublicadas[vaga], imagens[vaga], tamanhoMaximoImagem);
    estados[vaga].imagemPronta = true;
  }
  portEXIT_CRITICAL(&travaEstados);
}

void executarTrabalhos(void* parametro) {
  const uint8_t vaga = reinterpret_cast<uintptr_t>(parametro);
  Trabalho trabalho;

  for (;;) {
    if (xQueueReceive(filas[vaga], &trabalho, portMAX_DELAY) != pdTRUE) {
      continue;
    }

    const PerfilRenderizacao& perfil = perfis[trabalho.perfil];
    const uint32_t inicio = millis();
    uint32_t inicioJanela = micros();
    const uint32_t orcamentoUs = janelaOrcamentoUs * perfil.intensidade / 100;
    bool cancelado = false;
    bool expirou = false;

    do {
      const EstadoVaga atual = consultarEstado(vaga);
      uint32_t pixels = 0;

      for (uint16_t linha = 0; linha < tamanhoBloco; ++linha) {
        cancelado = trabalhoCancelado(vaga);
        expirou = millis() - inicio >= timeoutMaximoTrabalhoMs;
        if (cancelado || expirou) {
          break;
        }

        for (uint16_t coluna = 0; coluna < tamanhoBloco; ++coluna) {
          const Vetor cor = calcularPixel(
            atual.blocoAtual.coluna + coluna,
            atual.blocoAtual.linha + linha,
            atual.blocoAtual,
            perfil);

          const uint32_t posicao = pixels * 3;
          imagens[vaga][posicao] = static_cast<uint8_t>(cor.x * 255);
          imagens[vaga][posicao + 1] = static_cast<uint8_t>(cor.y * 255);
          imagens[vaga][posicao + 2] = static_cast<uint8_t>(cor.z * 255);
          ++pixels;

          descansarSeOrcamentoEsgotado(inicioJanela, orcamentoUs);
        }
      }

      publicarBlocoSePronto(vaga, atual, pixels, cancelado, expirou);
    } while (!cancelado && !expirou && millis() - inicio < trabalho.duracaoSegundos * 1000UL);

    portENTER_CRITICAL(&travaEstados);
    cancelado = cancelado || estados[vaga].cancelar;
    estados[vaga].tempoMs = millis() - inicio;
    estados[vaga].situacao = expirou ? Situacao::Falhou : (cancelado ? Situacao::Cancelado : Situacao::Concluido);
    estados[vaga].ocupado = false;
    if (!cancelado && !expirou) {
      ++trabalhosConcluidos;
    }
    portEXIT_CRITICAL(&travaEstados);
  }
}

String escaparJson(const char* texto) {
  String resultado;
  for (const char* caractere = texto; *caractere; ++caractere) {
    if (*caractere == '"' || *caractere == '\\') {
      resultado += '\\';
    }
    if (static_cast<unsigned char>(*caractere) >= 32) {
      resultado += *caractere;
    }
  }
  return resultado;
}

void responderSaude() {
  const TelemetriaCpu cpu = consultarCpu();
  EstadoVaga instantaneos[quantidadeVagas];
  uint32_t concluidos = 0;

  portENTER_CRITICAL(&travaEstados);
  for (uint8_t vaga = 0; vaga < quantidadeVagas; ++vaga) {
    instantaneos[vaga] = estados[vaga];
  }
  concluidos = trabalhosConcluidos;
  portEXIT_CRITICAL(&travaEstados);

  char trecho[512];
  String resposta;
  resposta.reserve(1536);

  snprintf(
    trecho,
    sizeof(trecho),
    "{\"no\":\"%s\",\"cpuTotal\":%.2f,\"cpuValida\":%s,\"idadeAmostraMs\":%lu,\"nucleos\":[",
    escaparJson(NODE_NAME).c_str(),
    (cpu.nucleos[0] + cpu.nucleos[1]) / 2.0f,
    cpu.valida ? "true" : "false",
    static_cast<unsigned long>(cpu.idadeAmostraMs));
  resposta += trecho;

  for (uint8_t nucleo = 0; nucleo < quantidadeNucleos; ++nucleo) {
    const bool ocupado = instantaneos[nucleo].ocupado || instantaneos[nucleo + 2].ocupado;
    const EstadoVaga& estado = instantaneos[instantaneos[nucleo].ocupado ? nucleo : nucleo + 2];

    snprintf(
      trecho,
      sizeof(trecho),
      "%s{\"indice\":%u,\"ocupado\":%s,\"trabalhoId\":\"%s\",\"progresso\":%.2f,\"cpu\":%.2f}",
      nucleo ? "," : "",
      nucleo,
      ocupado ? "true" : "false",
      estado.trabalho.id,
      calcularProgresso(estado),
      cpu.nucleos[nucleo]);
    resposta += trecho;
  }

  resposta += "],\"vagas\":[";
  for (uint8_t vaga = 0; vaga < quantidadeVagas; ++vaga) {
    const EstadoVaga& estado = instantaneos[vaga];

    snprintf(
      trecho,
      sizeof(trecho),
      "%s{\"indice\":%u,\"ocupado\":%s,\"trabalhoId\":\"%s\",\"progresso\":%.2f}",
      vaga ? "," : "",
      vaga,
      estado.ocupado ? "true" : "false",
      estado.trabalho.id,
      calcularProgresso(estado));
    resposta += trecho;
  }

  snprintf(
    trecho,
    sizeof(trecho),
    "],\"trabalhosConcluidos\":%lu,\"tempoLigadoMs\":%lu,\"memoriaLivre\":%lu,\"rssi\":%d}",
    static_cast<unsigned long>(concluidos),
    static_cast<unsigned long>(millis()),
    static_cast<unsigned long>(ESP.getFreeHeap()),
    WiFi.RSSI());
  resposta += trecho;

  servidor.send(200, "application/json", resposta);
}

bool lerNumero(const char* campo, uint32_t maximo, uint32_t& valor) {
  const String texto = servidor.arg(campo);
  if (!texto.length() || texto.length() > 7) {
    return false;
  }

  valor = 0;
  for (size_t indice = 0; indice < texto.length(); ++indice) {
    if (texto[indice] < '0' || texto[indice] > '9') {
      return false;
    }

    valor = valor * 10 + (texto[indice] - '0');
    if (valor > maximo) {
      return false;
    }
  }
  return true;
}

bool resolucaoValida(uint32_t largura, uint32_t altura) {
  const bool larguraPermitida = largura == 128 || largura == 256 || largura == 384 || largura == 512;
  return larguraPermitida && altura == largura * 3 / 4;
}

int buscarIndicePerfil(const String& nivel) {
  for (uint8_t indice = 0; indice < quantidadePerfis; ++indice) {
    if (nivel == perfis[indice].nivel) {
      return indice;
    }
  }
  return -1;
}

bool lerTrabalhoDaRequisicao(Trabalho& trabalho, uint32_t& vaga, const char*& mensagemErro) {
  const String identificador = servidor.arg("id");
  const String nivel = servidor.arg("nivel");

  uint32_t quadro = 0;
  uint32_t passagem = 0;
  uint32_t coluna = 0;
  uint32_t linha = 0;
  uint32_t largura = 0;
  uint32_t altura = 0;
  uint32_t duracao = 0;

  if (!identificadorValido(identificador) ||
      !lerNumero("vaga", quantidadeVagas - 1, vaga) ||
      !lerNumero("quadro", quadroMaximo, quadro) ||
      !lerNumero("passagem", quantidadePassagens - 1, passagem) ||
      !lerNumero("larguraImagem", 512, largura) ||
      !lerNumero("alturaImagem", 384, altura) ||
      !resolucaoValida(largura, altura) ||
      !lerNumero("coluna", largura - tamanhoBloco, coluna) ||
      !lerNumero("linha", altura - tamanhoBloco, linha) ||
      coluna % tamanhoBloco != 0 ||
      linha % tamanhoBloco != 0 ||
      !lerNumero("duracaoSegundos", duracaoMaximaSegundos, duracao) ||
      duracao == 0) {
    mensagemErro = "Parametros invalidos.";
    return false;
  }

  const int perfil = buscarIndicePerfil(nivel);
  if (perfil < 0) {
    mensagemErro = "Nivel invalido.";
    return false;
  }

  identificador.toCharArray(trabalho.id, sizeof(trabalho.id));
  trabalho.perfil = static_cast<uint8_t>(perfil);
  trabalho.bloco.quadro = quadro;
  trabalho.bloco.passagem = static_cast<uint8_t>(passagem);
  trabalho.bloco.coluna = static_cast<uint16_t>(coluna);
  trabalho.bloco.linha = static_cast<uint16_t>(linha);
  trabalho.bloco.largura = static_cast<uint16_t>(largura);
  trabalho.bloco.altura = static_cast<uint16_t>(altura);
  trabalho.duracaoSegundos = static_cast<uint16_t>(duracao);
  return true;
}

bool mesmosParametros(const Trabalho& primeiro, const Trabalho& segundo) {
  return primeiro.perfil == segundo.perfil &&
    primeiro.duracaoSegundos == segundo.duracaoSegundos &&
    primeiro.bloco.quadro == segundo.bloco.quadro &&
    primeiro.bloco.passagem == segundo.bloco.passagem &&
    primeiro.bloco.coluna == segundo.bloco.coluna &&
    primeiro.bloco.linha == segundo.bloco.linha &&
    primeiro.bloco.largura == segundo.bloco.largura &&
    primeiro.bloco.altura == segundo.bloco.altura;
}

void receberTrabalho() {
  Trabalho trabalho;
  uint32_t vaga = 0;
  const char* mensagemErro = nullptr;

  if (!lerTrabalhoDaRequisicao(trabalho, vaga, mensagemErro)) {
    responderErro(400, mensagemErro);
    return;
  }

  const int existente = localizarTrabalho(trabalho.id);
  if (existente >= 0) {
    if (static_cast<uint32_t>(existente) != vaga || !mesmosParametros(consultarEstado(existente).trabalho, trabalho)) {
      responderErro(409, "ID reutilizado.");
    } else {
      responderResultado(202, static_cast<uint8_t>(existente));
    }
    return;
  }

  if (consultarEstado(static_cast<uint8_t>(vaga)).ocupado) {
    responderErro(409, "Vaga ocupada.");
    return;
  }

  portENTER_CRITICAL(&travaEstados);
  estados[vaga] = EstadoVaga{};
  estados[vaga].trabalho = trabalho;
  estados[vaga].blocoAtual = trabalho.bloco;
  estados[vaga].presente = true;
  estados[vaga].ocupado = true;
  estados[vaga].inicioMs = millis();
  portEXIT_CRITICAL(&travaEstados);

  if (xQueueSend(filas[vaga], &trabalho, 0) != pdTRUE) {
    portENTER_CRITICAL(&travaEstados);
    estados[vaga].ocupado = false;
    estados[vaga].situacao = Situacao::Falhou;
    portEXIT_CRITICAL(&travaEstados);
    responderErro(503, "Fila indisponivel.");
    return;
  }

  responderResultado(202, static_cast<uint8_t>(vaga));
}

void consultarPublicacao() {
  const int vaga = localizarTrabalho(servidor.pathArg(0));
  if (vaga < 0) {
    responderErro(404, "Trabalho desconhecido.");
    return;
  }

  static uint8_t dados[tamanhoMaximoImagem];
  EstadoVaga estado;

  portENTER_CRITICAL(&travaEstados);
  estado = estados[vaga];
  if (estado.imagemPronta) {
    memcpy(dados, imagensPublicadas[vaga], tamanhoMaximoImagem);
  }
  portEXIT_CRITICAL(&travaEstados);

  if (!estado.imagemPronta) {
    servidor.send(204);
    return;
  }

  char metadados[256];
  snprintf(
    metadados,
    sizeof(metadados),
    "{\"publicacao\":%lu,\"quadro\":%lu,\"passagem\":%u,\"coluna\":%u,\"linha\":%u,\"pixels\":\"",
    static_cast<unsigned long>(estado.publicacao),
    static_cast<unsigned long>(estado.blocoAtual.quadro),
    estado.blocoAtual.passagem,
    estado.blocoAtual.coluna,
    estado.blocoAtual.linha);

  servidor.send(200, "application/json", String(metadados) + base64::encode(dados, tamanhoMaximoImagem) + "\"}");
}

void avancarPublicacao() {
  const int vaga = localizarTrabalho(servidor.pathArg(0));
  if (vaga < 0) {
    responderErro(404, "Trabalho desconhecido.");
    return;
  }

  const EstadoVaga anterior = consultarEstado(static_cast<uint8_t>(vaga));
  uint32_t publicacao = 0;
  uint32_t quadro = 0;
  uint32_t passagem = 0;
  uint32_t coluna = 0;
  uint32_t linha = 0;

  if (!lerNumero("publicacao", quadroMaximo, publicacao) ||
      !lerNumero("quadro", quadroMaximo, quadro) ||
      !lerNumero("passagem", quantidadePassagens - 1, passagem) ||
      !lerNumero("coluna", anterior.trabalho.bloco.largura - tamanhoBloco, coluna) ||
      !lerNumero("linha", anterior.trabalho.bloco.altura - tamanhoBloco, linha) ||
      coluna % tamanhoBloco != 0 ||
      linha % tamanhoBloco != 0) {
    responderErro(400, "Bloco invalido.");
    return;
  }

  const BlocoCena& atual = anterior.blocoAtual;
  const bool repeticaoIdempotente =
    anterior.publicacao == publicacao + 1 &&
    atual.quadro == quadro &&
    atual.passagem == passagem &&
    atual.coluna == coluna &&
    atual.linha == linha;

  if (repeticaoIdempotente) {
    servidor.send(202);
    return;
  }

  bool aceito = false;
  portENTER_CRITICAL(&travaEstados);
  if (estados[vaga].ocupado &&
      estados[vaga].imagemPronta &&
      estados[vaga].publicacao == publicacao) {
    estados[vaga].blocoAtual.quadro = quadro;
    estados[vaga].blocoAtual.passagem = static_cast<uint8_t>(passagem);
    estados[vaga].blocoAtual.coluna = static_cast<uint16_t>(coluna);
    estados[vaga].blocoAtual.linha = static_cast<uint16_t>(linha);
    estados[vaga].publicacao++;
    estados[vaga].imagemPronta = false;
    aceito = true;
  }
  portEXIT_CRITICAL(&travaEstados);

  if (aceito) {
    servidor.send(202);
  } else {
    responderErro(409, "Publicacao substituida ou tarefa encerrada.");
  }
}

void configurarServidor() {
  servidor.on(UriBraces("/jobs/{}/tile"), HTTP_GET, consultarPublicacao);
  servidor.on(UriBraces("/jobs/{}/next"), HTTP_POST, avancarPublicacao);
  servidor.on("/health", HTTP_GET, responderSaude);
  servidor.on("/jobs", HTTP_POST, receberTrabalho);

  servidor.on("/jobs", HTTP_GET, [] {
    const int vaga = localizarTrabalho(servidor.arg("id"));
    if (vaga < 0) {
      responderErro(404, "Trabalho desconhecido.");
    } else {
      responderResultado(200, static_cast<uint8_t>(vaga));
    }
  });

  servidor.on("/cancel", HTTP_POST, [] {
    const int vaga = localizarTrabalho(servidor.arg("id"));
    if (vaga < 0) {
      responderErro(404, "Trabalho desconhecido.");
      return;
    }

    portENTER_CRITICAL(&travaEstados);
    if (estados[vaga].ocupado) {
      estados[vaga].cancelar = true;
    }
    portEXIT_CRITICAL(&travaEstados);

    servidor.send(202, "application/json", "{\"aceito\":true}");
  });

  servidor.onNotFound([] { responderErro(404, "Endpoint desconhecido."); });
  servidor.begin();
}

void setup() {
  Serial.begin(115200);
  pinMode(pinoLed, OUTPUT);
  delay(500);

  bool iniciou = true;
  for (uint8_t vaga = 0; vaga < quantidadeVagas; ++vaga) {
    filas[vaga] = xQueueCreate(1, sizeof(Trabalho));
    iniciou = iniciou && filas[vaga] &&
      xTaskCreatePinnedToCore(
        executarTrabalhos,
        "calculo",
        4096,
        reinterpret_cast<void*>(static_cast<uintptr_t>(vaga)),
        1,
        nullptr,
        vaga % quantidadeNucleos) == pdPASS;
  }

  for (uint8_t nucleo = 0; nucleo < quantidadeNucleos; ++nucleo) {
    iniciou = iniciou && xTaskCreatePinnedToCore(
      medirCpu,
      "cpu",
      3072,
      reinterpret_cast<void*>(static_cast<uintptr_t>(nucleo)),
      2,
      nullptr,
      nucleo) == pdPASS;
  }

  if (!iniciou) {
    Serial.println("ERRO: memoria insuficiente para iniciar os trabalhadores.");
    for (;;) {
      delay(1000);
    }
  }

  wifiPronto = conectarWifi();
  if (wifiPronto) {
    configurarServidor();
  }
}

void loop() {
  if (!wifiPronto) {
    delay(1000);
    return;
  }

  servidor.handleClient();

  bool ocupado = false;
  for (uint8_t vaga = 0; vaga < quantidadeVagas; ++vaga) {
    ocupado = ocupado || consultarEstado(vaga).ocupado;
  }
  digitalWrite(pinoLed, ocupado ? HIGH : LOW);

  static uint32_t ultimaVerificacao = 0;
  if (millis() - ultimaVerificacao > intervaloWifiMs) {
    ultimaVerificacao = millis();
    const wl_status_t situacao = WiFi.status();

    Serial.printf(
      "[WiFi] Status: %d%s\n",
      static_cast<int>(situacao),
      situacao == WL_CONNECTED ? " (WL_CONNECTED)" : " (desconectado)");

    if (situacao != WL_CONNECTED) {
      WiFi.reconnect();
    } else {
      Serial.print("[WiFi] IP: ");
      Serial.println(WiFi.localIP());
    }
  }

  delay(2);
}
