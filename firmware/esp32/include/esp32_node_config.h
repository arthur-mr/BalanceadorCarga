#pragma once
#if __has_include("config.local.h")
#include "config.local.h"
#endif

#ifndef WIFI_SSID
#define WIFI_SSID "configure-me"
#endif
#ifndef WIFI_PASS
#define WIFI_PASS ""
#endif
#ifndef NODE_NAME
#define NODE_NAME "ESP32-A"
#endif

#ifndef WIFI_FALLBACK_MAC
#define WIFI_FALLBACK_MAC ""
#endif
