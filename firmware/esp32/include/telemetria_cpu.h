#pragma once
#include <Arduino.h>
#include <esp_timer.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>

#if !CONFIG_FREERTOS_USE_TRACE_FACILITY || !CONFIG_FREERTOS_GENERATE_RUN_TIME_STATS || !CONFIG_FREERTOS_RUN_TIME_STATS_USING_ESP_TIMER
#error Habilite as estatisticas FreeRTOS de sdkconfig.defaults e compile Arduino como componente ESP-IDF.
#endif

struct TelemetriaCpu {
  float nucleos[2] = {};
  bool valida = false;
  uint32_t idadeAmostraMs = 0;
};

portMUX_TYPE travaCpu = portMUX_INITIALIZER_UNLOCKED;
float cpuPorNucleo[2] = {};
uint32_t amostraPorNucleo[2] = {};
bool amostraValida[2] = {};

inline void medirCpu(void* parametro) {
  const uint32_t nucleo = reinterpret_cast<uintptr_t>(parametro);
  const TaskHandle_t tarefaOciosa = xTaskGetIdleTaskHandleForCPU(nucleo);

  TaskStatus_t estadoOcioso{};
  vTaskGetInfo(tarefaOciosa, &estadoOcioso, pdFALSE, eInvalid);

  uint32_t ociosoAnterior = estadoOcioso.ulRunTimeCounter;
  uint32_t instanteAnterior = static_cast<uint32_t>(esp_timer_get_time());
  TickType_t despertar = xTaskGetTickCount();

  for (;;) {
    vTaskDelayUntil(&despertar, pdMS_TO_TICKS(1000));

    vTaskGetInfo(tarefaOciosa, &estadoOcioso, pdFALSE, eInvalid);
    const uint32_t instante = static_cast<uint32_t>(esp_timer_get_time());
    const uint32_t intervalo = instante - instanteAnterior;
    const uint32_t ocioso = estadoOcioso.ulRunTimeCounter - ociosoAnterior;
    const float cpu = intervalo
      ? constrain(100.0f * (1.0f - static_cast<float>(ocioso) / intervalo), 0.0f, 100.0f)
      : 0.0f;

    portENTER_CRITICAL(&travaCpu);
    cpuPorNucleo[nucleo] = cpu;
    amostraPorNucleo[nucleo] = millis();
    amostraValida[nucleo] = intervalo != 0;
    portEXIT_CRITICAL(&travaCpu);

    instanteAnterior = instante;
    ociosoAnterior = estadoOcioso.ulRunTimeCounter;
  }
}

inline TelemetriaCpu consultarCpu() {
  TelemetriaCpu telemetria;

  portENTER_CRITICAL(&travaCpu);
  const uint32_t agora = millis();
  telemetria.nucleos[0] = cpuPorNucleo[0];
  telemetria.nucleos[1] = cpuPorNucleo[1];
  telemetria.valida = amostraValida[0] && amostraValida[1];
  telemetria.idadeAmostraMs = max(agora - amostraPorNucleo[0], agora - amostraPorNucleo[1]);
  portEXIT_CRITICAL(&travaCpu);

  return telemetria;
}
