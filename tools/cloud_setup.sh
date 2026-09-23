#!/bin/bash
# Окружение облачной сессии Claude Code для этого репо (Ubuntu 24.04, root).
# Вставить СОДЕРЖИМОЕ файла в поле «Setup script» облачного окружения
# (claude.ai/code → значок облака над полем ввода → шестерёнка окружения).
# Кэшируется ~7 дней; должно укладываться в ~5 минут и выходить с кодом 0.
export DEBIAN_FRONTEND=noninteractive
# mono — только для прогона окон ATTILE/ATFRAME (tools/*_ui); xvfb — экран без монитора
(apt-get update -qq && apt-get install -y -qq mono-complete xvfb poppler-utils fonts-dejavu-core) || true &
# Python: движки — только stdlib; синтетика/картинки/PDF — shapely, ezdxf, matplotlib, reportlab
(pip install -q --break-system-packages shapely ezdxf pyyaml openpyxl matplotlib reportlab pypdf pyinstxtractor-ng \
  || pip install -q shapely ezdxf pyyaml openpyxl matplotlib reportlab pypdf pyinstxtractor-ng) || true &
wait
exit 0
