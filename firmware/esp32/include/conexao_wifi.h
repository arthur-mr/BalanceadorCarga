#pragma once
#include <Arduino.h>
#include <WiFi.h>
#include <esp_system.h>
#include <esp_wifi.h>
#include "esp32_node_config.h"

inline void registrarMac(const char* rotulo, const uint8_t* mac) {
  Serial.printf("[WiFi] %s: %02X:%02X:%02X:%02X:%02X:%02X\n",
    rotulo, mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

inline bool verificarWifi(esp_err_t codigo, const char* operacao) {
  if (codigo == ESP_OK) {
    return true;
  }

  Serial.printf("[WiFi] ERRO %s: %s (%d). Conexao nao iniciada.\n", operacao, esp_err_to_name(codigo), codigo);
  return false;
}

inline bool interpretarMac(const char* texto, uint8_t* mac) {
  if (strlen(texto) != 17) {
    return false;
  }

  for (size_t indice = 0; indice < 6; ++indice) {
    const size_t posicao = indice * 3;
    const bool parHexadecimal =
      isxdigit(static_cast<unsigned char>(texto[posicao])) &&
      isxdigit(static_cast<unsigned char>(texto[posicao + 1]));
    const bool separadorValido = indice == 5 || texto[posicao + 2] == ':';

    if (!parHexadecimal || !separadorValido) {
      return false;
    }

    char par[] = {texto[posicao], texto[posicao + 1], '\0'};
    mac[indice] = static_cast<uint8_t>(strtoul(par, nullptr, 16));
  }

  return (mac[0] & 0x03) == 0x02;
}

inline bool macEhZerado(const uint8_t* mac, size_t tamanho) {
  for (size_t indice = 0; indice < tamanho; ++indice) {
    if (mac[indice] != 0) {
      return false;
    }
  }
  return true;
}

inline bool aplicarMacAlternativo(const uint8_t* alternativo, uint8_t* efetivo) {
  WiFi.setAutoReconnect(false);

  if (!verificarWifi(esp_wifi_stop(), "parar interface STA") ||
      !verificarWifi(esp_wifi_set_mac(WIFI_IF_STA, alternativo), "aplicar MAC alternativo") ||
      !verificarWifi(esp_wifi_start(), "reiniciar interface STA") ||
      !verificarWifi(esp_wifi_get_mac(WIFI_IF_STA, efetivo), "confirmar MAC STA")) {
    return false;
  }

  if (memcmp(efetivo, alternativo, 6) != 0) {
    Serial.println("[WiFi] ERRO: MAC efetivo difere do configurado.");
    return false;
  }

  Serial.println("[WiFi] MAC de fabrica zerado: aplicado MAC alternativo em software.");
  return true;
}

inline bool conectarWifi() {
  uint8_t fabrica[6] = {};
  uint8_t efetivo[6] = {};
  uint8_t alternativo[6] = {};

  const esp_err_t leitura = esp_efuse_mac_get_default(fabrica);
  if (leitura != ESP_OK && leitura != ESP_ERR_INVALID_CRC) {
    return verificarWifi(leitura, "ler MAC de fabrica");
  }

  registrarMac("MAC de fabrica", fabrica);
  const bool zerado = macEhZerado(fabrica, sizeof(fabrica));
  if (leitura != ESP_OK && !zerado) {
    return verificarWifi(leitura, "validar MAC de fabrica");
  }

  if (zerado && !interpretarMac(WIFI_FALLBACK_MAC, alternativo)) {
    Serial.println("[WiFi] ERRO: configure WIFI_FALLBACK_MAC exclusivo para esta placa com MAC zerado.");
    return false;
  }

  if (!WiFi.mode(WIFI_STA)) {
    return false;
  }

  if (!verificarWifi(esp_wifi_get_mac(WIFI_IF_STA, efetivo), "ler MAC STA")) {
    return false;
  }

  if (zerado) {
    if (!aplicarMacAlternativo(alternativo, efetivo)) {
      return false;
    }
  } else {
    Serial.println("[WiFi] MAC de fabrica preservado; alternativa nao aplicada.");
  }

  registrarMac("MAC STA utilizado", efetivo);
  WiFi.setAutoReconnect(true);
  Serial.printf("[WiFi] No %s: iniciando conexao.\n", NODE_NAME);
  WiFi.begin(WIFI_SSID, WIFI_PASS);
  return true;
}
