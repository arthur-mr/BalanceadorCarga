#pragma once
#include <Arduino.h>
#include <math.h>
#include <stddef.h>
#include <stdint.h>

constexpr uint16_t larguraCena = 128;
constexpr uint16_t alturaCena = 96;
constexpr uint8_t tamanhoBloco = 16;
constexpr uint8_t quantidadePassagens = 4;
constexpr size_t tamanhoMaximoImagem = tamanhoBloco * tamanhoBloco * 3;

constexpr uint32_t janelaDescansoUs = 20000UL;
constexpr uint32_t limiarDescansoUs = 18000UL;
constexpr float alturaChao = -0.85f;
constexpr float distanciaMinimaIntersecao = 0.001f;
constexpr float afastamentoSombra = 0.003f;
constexpr uint8_t quadrosPorVoltaLuz = 36;
constexpr float piCena = 3.14159265358979323846f;

struct PerfilRenderizacao {
  const char* nivel;
  uint16_t amostras;
  uint8_t intensidade;
};

constexpr PerfilRenderizacao perfis[] = {
  {"leve", 8, 25},
  {"medio", 32, 55},
  {"pesado", 96, 85}
};

struct BlocoCena {
  uint32_t quadro = 0;
  uint8_t passagem = 0;
  uint16_t coluna = 0;
  uint16_t linha = 0;
  uint16_t largura = 128;
  uint16_t altura = 96;
};

struct Vetor {
  float x;
  float y;
  float z;

  Vetor operator+(const Vetor& outro) const { return {x + outro.x, y + outro.y, z + outro.z}; }
  Vetor operator-(const Vetor& outro) const { return {x - outro.x, y - outro.y, z - outro.z}; }
  Vetor operator*(float fator) const { return {x * fator, y * fator, z * fator}; }
};

inline float produtoEscalar(const Vetor& primeiro, const Vetor& segundo) {
  return primeiro.x * segundo.x + primeiro.y * segundo.y + primeiro.z * segundo.z;
}

inline Vetor normalizar(const Vetor& vetor) {
  return vetor * (1.0f / sqrtf(produtoEscalar(vetor, vetor)));
}

inline float limitar(float valor) {
  return fminf(1.0f, fmaxf(0.0f, valor));
}

struct Esfera {
  Vetor centro;
  float raio;
  Vetor cor;
};

const Esfera esferas[] = {
  {{-0.85f, -0.15f, 0}, 0.7f, {0.95f, 0.25f, 0.08f}},
  {{0.75f, -0.3f, 0.5f}, 0.55f, {0.08f, 0.38f, 0.95f}},
  {{0.3f, 0.65f, 1}, 0.4f, {0.25f, 0.85f, 0.35f}}
};

const Vetor camera = {0, 0.5f, -4};

inline float sortear(uint32_t& estado) {
  estado = estado * 1664525UL + 1013904223UL;
  return (estado >> 8) / 16777216.0f;
}

inline float interceptar(const Vetor& origem, const Vetor& direcao, const Esfera& esfera) {
  const Vetor deslocamento = origem - esfera.centro;
  const float projecao = produtoEscalar(deslocamento, direcao);
  const float discriminante = projecao * projecao - produtoEscalar(deslocamento, deslocamento) + esfera.raio * esfera.raio;

  if (discriminante < 0) {
    return INFINITY;
  }

  const float distancia = -projecao - sqrtf(discriminante);
  return distancia > distanciaMinimaIntersecao ? distancia : INFINITY;
}

inline bool pontoEmSombra(const Vetor& ponto, const Vetor& normal, const Vetor& direcaoLuz, float distanciaLuz) {
  const Vetor origemSombra = ponto + normal * afastamentoSombra;
  for (const Esfera& esfera : esferas) {
    if (interceptar(origemSombra, direcaoLuz, esfera) < distanciaLuz) {
      return true;
    }
  }
  return false;
}

inline Vetor iluminar(const Vetor& direcao, const Vetor& luz) {
  float distancia = INFINITY;
  const Esfera* esferaAtingida = nullptr;

  for (const Esfera& esfera : esferas) {
    const float candidata = interceptar(camera, direcao, esfera);
    if (candidata < distancia) {
      distancia = candidata;
      esferaAtingida = &esfera;
    }
  }

  bool atingiuChao = false;
  const float distanciaChao = direcao.y < -distanciaMinimaIntersecao
    ? (alturaChao - camera.y) / direcao.y
    : INFINITY;

  if (distanciaChao < distancia) {
    distancia = distanciaChao;
    atingiuChao = true;
  }

  if (isinf(distancia)) {
    const float altura = limitar(direcao.y + 0.5f);
    return Vetor{0.12f, 0.19f, 0.3f} + Vetor{0.25f, 0.3f, 0.35f} * altura;
  }

  const Vetor ponto = camera + direcao * distancia;
  const Vetor normal = atingiuChao
    ? Vetor{0, 1, 0}
    : normalizar(ponto - esferaAtingida->centro);

  const bool quadradoClaro = ((static_cast<int>(floorf(ponto.x)) + static_cast<int>(floorf(ponto.z))) & 1) == 0;
  const float tonalidadeChao = quadradoClaro ? 0.65f : 0.3f;
  const Vetor cor = atingiuChao ? Vetor{tonalidadeChao, tonalidadeChao, tonalidadeChao} : esferaAtingida->cor;

  const Vetor deslocamentoLuz = luz - ponto;
  const float distanciaLuz = sqrtf(produtoEscalar(deslocamentoLuz, deslocamentoLuz));
  const Vetor direcaoLuz = deslocamentoLuz * (1.0f / distanciaLuz);

  const bool emSombra = pontoEmSombra(ponto, normal, direcaoLuz, distanciaLuz);
  const float difusa = emSombra ? 0 : fmaxf(0, produtoEscalar(normal, direcaoLuz));
  const Vetor reflexo = normal * (2 * produtoEscalar(normal, direcaoLuz)) - direcaoLuz;
  const float brilho = emSombra ? 0 : 0.3f * powf(fmaxf(0, produtoEscalar(reflexo, direcao * -1)), 24);

  const Vetor iluminada = cor * (0.13f + 0.85f * difusa) + Vetor{brilho, brilho, brilho};
  return {limitar(iluminada.x), limitar(iluminada.y), limitar(iluminada.z)};
}

inline void cederProcessadorNaJanela(uint16_t amostra) {
  if ((amostra & 7) != 0) {
    return;
  }

  const uint32_t faseUs = micros() % janelaDescansoUs;
  if (faseUs >= limiarDescansoUs) {
    vTaskDelay(pdMS_TO_TICKS((janelaDescansoUs - faseUs + 999) / 1000));
  }
}

inline Vetor calcularPixel(uint16_t coluna, uint16_t linha, const BlocoCena& bloco, const PerfilRenderizacao& perfil) {
  uint32_t aleatorio =
    2166136261UL ^
    bloco.quadro * 73856093UL ^
    bloco.passagem * 19349663UL ^
    coluna * 83492791UL ^
    linha * 2654435761UL;

  const float angulo = (bloco.quadro % quadrosPorVoltaLuz) * (2.0f * piCena / quadrosPorVoltaLuz);
  const Vetor centroLuz = {-2.4f * cosf(angulo), 3.5f, -2.0f + 1.5f * sinf(angulo)};
  Vetor acumulado = {0, 0, 0};

  for (uint16_t amostra = 0; amostra < perfil.amostras; ++amostra) {
    cederProcessadorNaJanela(amostra);

    const float deslocamentoHorizontal = sortear(aleatorio);
    const float deslocamentoVertical = sortear(aleatorio);
    const float horizontal = ((coluna + deslocamentoHorizontal) / bloco.largura - 0.5f) * 1.25f;
    const float vertical = (0.5f - (linha + deslocamentoVertical) / bloco.altura) * 0.94f - 0.08f;
    const Vetor direcao = normalizar({horizontal, vertical, 1});

    const float deslocamentoX = (sortear(aleatorio) - 0.5f) * 0.8f;
    const float deslocamentoZ = (sortear(aleatorio) - 0.5f) * 0.8f;
    const Vetor pontoLuz = centroLuz + Vetor{deslocamentoX, 0, deslocamentoZ};

    acumulado = acumulado + iluminar(direcao, pontoLuz);
  }

  return acumulado * (1.0f / perfil.amostras);
}
