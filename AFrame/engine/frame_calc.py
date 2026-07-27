# -*- coding: utf-8 -*-
"""frame_calc — расчёт несущей способности подсистемы НВФ (этап 4).

Методика: 13 боевых статрасчётов ООО «Вектор фасад» 2022–2026
(atspec-testdata/docs/facades_calc/, конспект METHOD_CALC.md). Вся
арифметика каждого блока сверена руками с их PDF (сессия 27.07);
эталонные числа — в test_frame_calc.py.

Цепочка (их порядок блоков):
  нагрузки (вес, пиковая ветровая СП 20.13330.2016, гололёд)
  → сочетания (С1 = вес+ветер; С2 = вес+гололёд+0.6·ветра; берём max)
  → анкер:      N_a = N_п·e1/e2 + N_в·e3/e4 + N_в ≤ N_a_max/g
  → кронштейн:  σ11 = N_п·e1/Wx + N_в·e5/Wy + N_в/A ≤ R_y
                σ22 = N_в·e6/Wy22 ≤ R_y
  → удлинитель: σ = N_п·e4у/Wx + N_в·e5у/Wy + N_в/A ≤ R_y
  → профиль:    σ = c_M·W_p·b·L² /Wx + N_п/A ≤ R_y
                f = c_f·(W_p·b/1.4)·L⁴/(E·Jx) ≤ L/150
  → заклёпки:   срез  sqrt(N_п²+N_в²)/(n_зак·n_срез) ≤ 3100/(1.25·g)
                смятие sqrt(N_п²+N_в²)/(n_зак·d·t) ≤ R_з

Грузовые площади:
  vertical (Тип-1):    N_п=(q_обл·b+q_напр)·L1;  N_в=W_p·L1·b·k_нер
                       L1 = верт. шаг кронштейнов (ПОДБИРАЕТСЯ)
  interfloor (Тип-4/5): N_п=(q_обл·L2+q_напр)·L1; N_в=W_p·L1·L2·k_нер
                       L1 = длина направляющей (≈этаж, фикс),
                       L2 = гориз. шаг кронштейнов (ПОДБИРАЕТСЯ)
  ortho (Новгород):    N_п=(q_обл·b+q_напр)·L1;  N_в=W_p·L1·L2·k_нер
                       L1 = верт. шаг гориз. профиля (фикс),
                       L2 = гориз. шаг кронштейнов (ПОДБИРАЕТСЯ)

Константы схем = таблицы неразрезных балок, выбор по числу пролётов
n = max(2, floor(L_напр/пролёт)); n≥4 → «многопролётная»:
  2: (0.125, 1.25, 0.0052)   3: (0.100, 1.10, 0.00675)
  ≥4: (0.106, 1.132, 0.0063)
(interfloor — всегда многопролётная: направляющая через этажи).

Единицы: мм/м/кг/кг·см/кг/см² — как в их PDF; внутренние переводы
локальны в формулах. Только stdlib.
"""

import math

G = 9.8                     # м/с², как в их расчётах (не 9.80665)
E_STEEL = 2.1e6             # кг/см²
RY_DEFAULT = 2250.0         # кг/см² (08пс: 230 МПа /1.025)
RIVET_SHEAR_N = 3100.0      # Н, нормативное сопротивление на срез
RIVET_GAMMA = 1.25
RIVET_R_SMYAT = 2650.0      # кг/см²
RIVET_D_CM = 0.42           # диаметр отверстия, см
RIVET_T_CM = 0.12           # толщина пакета, см
F_LIMIT_DIV = 150.0         # f_max = L/150

# СП 20.13330.2016: w0 кг/м² по ветровым районам (табл. 11.1)
WIND_REGIONS = {"Ia": 17.0, "I": 23.0, "II": 30.0, "III": 38.0,
                "IV": 48.0, "V": 60.0, "VI": 73.0, "VII": 85.0}
# тип местности: (k10, zeta10, alpha) — ф. 11.4/11.6
TERRAIN = {"A": (1.0, 0.76, 0.15), "B": (0.65, 1.06, 0.2),
           "C": (0.4, 1.78, 0.25)}
# гололёд (гл. 12): толщина стенки b мм по районам (табл. 12.1)
ICE_REGIONS = {"I": 3.0, "II": 5.0, "III": 10.0, "IV": 15.0, "V": 20.0}
ICE_K = 1.6      # k(z) — в их расчётах фикс
ICE_MU2 = 0.6
ICE_RHO = 0.9    # г/см³
ICE_GAMMA = 1.8

# (c_M, k_нер, c_f) по числу пролётов
SPAN_CONST = {2: (0.125, 1.25, 0.0052),
              3: (0.100, 1.10, 0.00675),
              "multi": (0.106, 1.132, 0.0063)}

CP_ROW, CP_CORNER = 1.2, 2.2    # |c_p| рядовая/угловая (все расчёты)

# ---------------------------------------------------------------- справочник
# Кронштейны: сечение 1-1 (Wx,Wy,A мм³/мм²), 2-2 (Wy22), плечи мм.
# de1: e1 = вынос − de1. e2/e3/e4 — плечи анкера (Новгород при выносе
# 260: e3=12, e4=25 — переопределяются в입 input при нужде).
BRACKETS = {
    "КР2-70": dict(Wx=1978.0, Wy=70.0, A=158.0, Wy22=67.0,
                   e5=23.0, e6=5.0, de1=5.0, e2=38.0, e3=9.0, e4=23.0),
    "КР1-70": dict(Wx=1978.0, Wy=70.0, A=158.0, Wy22=67.0,   # в PDF ФЦП
                   e5=23.0, e6=5.0, de1=5.0, e2=38.0, e3=9.0, e4=23.0),
    "КР1-85": dict(Wx=2649.0, Wy=278.0, A=195.0, Wy22=265.0,
                   e5=23.0, e6=5.0, de1=12.5, e2=73.0, e3=11.0, e4=21.0),
    "КП-125": dict(Wx=10417.0, Wy=17268.0, A=500.0, Wy22=245.6,
                   e5=2.0, e6=27.0, de1=4.0, e2=103.0, e3=0.0, e4=21.0),
}
# Удлинители: Wx,Wy,A; плечи e4у (вес), e5у (ветер), мм
EXTENDERS = {
    "УК-70-1,2": dict(Wx=1908.0, Wy=60.0, A=109.0, e4=80.0, e5=10.0),
    "УК-85-1,2": dict(Wx=2589.0, Wy=63.0, A=128.0, e4=80.0, e5=9.0),
}
# Профили: Wx мм³, Jx мм⁴, A мм², вес кг/м
PROFILES = {
    "ГП-40-40-1,2":    dict(Wx=516.0,  Jx=15202.0,  A=93.0,  q=0.745),
    "ЗП-40-20-1,2":    dict(Wx=496.0,  Jx=6181.0,   A=91.0,  q=0.73),
    "НСП-95-70-1,2":   dict(Wx=4124.0, Jx=187469.0, A=295.0, q=2.31),
    "НСП-69-60-1,2":   dict(Wx=3434.0, Jx=123588.0, A=247.0, q=1.97),
    "ШП-90-20-1,2":    dict(Wx=973.0,  Jx=12653.0,  A=197.0, q=1.53),
    "ШП-60-20-1,2":    dict(Wx=941.0,  Jx=10845.0,  A=157.0, q=1.22),
    "ШП-60-20-20-1,2": dict(Wx=953.0,  Jx=10482.0,  A=151.0, q=1.21),
    "НШП-2":           dict(Wx=5543.0, Jx=243960.0, A=297.0, q=2.31),
    "НГП-60-50-1,5":   dict(Wx=None,   Jx=None,     A=None,  q=1.4),
}


# ---------------------------------------------------------------- нагрузки
def wind_peak(w0, terrain, h):
    """Пиковая расчётная ветровая, кг/м²: (рядовая, угловая).

    W_p = w0·k(h)·(1+ζ(h))·|c_p|·γ_кор(=1)·γ_в(=1.4);
    k=k10·(h/10)^(2α), ζ=ζ10·(h/10)^(−α).
    """
    k10, z10, a = TERRAIN[terrain]
    k = k10 * (h / 10.0) ** (2 * a)
    zeta = z10 * (h / 10.0) ** (-a)
    base = w0 * k * (1.0 + zeta) * 1.4
    return base * CP_ROW, base * CP_CORNER


def ice_load(region):
    """Расчётная гололёдная, кг/м²: i_p = b·k·μ2·ρ·γ (b мм, ρ→кг/м³)."""
    b_mm = ICE_REGIONS[region]
    i_n = (b_mm / 1000.0) * ICE_K * ICE_MU2 * (ICE_RHO * 1000.0)
    return i_n * ICE_GAMMA


def spans_const(scheme, rail_len, span):
    """(c_M, k_нер, c_f): n = max(2, floor(rail_len/span)); ≥4 → multi;
    interfloor всегда multi."""
    if scheme in ("interfloor", "interfloor_direct"):
        return SPAN_CONST["multi"]
    n = max(2, int(math.floor(rail_len / float(span))))
    if n >= 4:
        return SPAN_CONST["multi"]
    return SPAN_CONST[n]


# ---------------------------------------------------------------- проверки
def _checks_pack(name, value, limit):
    return dict(name=name, value=round(value, 1), limit=round(limit, 1),
                ok=value <= limit + 1e-9)


def calc_chain(inp, step, zone):
    """Полная цепочка для одного шага и зоны ('row'|'corner').

    inp — dict:
      scheme: vertical|interfloor|interfloor_direct|ortho
        (interfloor_direct = Тип-5: без удлинителя, кронштейн в торец)
      w0 | wind_region, terrain, height
      ice_region (опц., дефолт II)
      q_clad (кг/м² нормативный), gamma_clad
      q_rails (кг/м суммарный нормативный вес направляющих: верт+гориз)
      offset (вынос e, мм), na_max (Н)
      bracket, extender (None для interfloor_direct), profile (имя или
        dict), n_rivets (дефолт 2; тип-5 — 4)
      b_row, b_corner (гориз. шаг направляющих, мм)
      rail_len (длина направляющей, мм; vertical: 3000)
      висячие поля переопределения: e1..e4 (плечи анкера), ry
    step — подбираемый шаг, мм:
      vertical: верт. шаг кронштейнов L1; interfloor/ortho: гориз. шаг L2.
    Возвращает dict: checks[], n_p, n_w, w_p, passed.
    """
    scheme = inp["scheme"]
    ry = inp.get("ry", RY_DEFAULT)
    w0 = inp.get("w0") or WIND_REGIONS[inp["wind_region"]]
    wp_row, wp_corner = wind_peak(w0, inp["terrain"], inp["height"])
    w_p = wp_row if zone == "row" else wp_corner
    b = (inp["b_row"] if zone == "row" else inp["b_corner"]) / 1000.0
    q_clad = inp["q_clad"] * inp["gamma_clad"]
    q_rails = inp["q_rails"] * 1.05
    rail_len = inp["rail_len"]

    # сочетания: С1 = ветер; С2 = гололёд + 0.6·ветер (вес в обоих)
    ice = ice_load(inp.get("ice_region", "II"))
    w_eff = max(w_p, ice + 0.6 * w_p)

    if scheme == "vertical":
        span = step                      # пролёт = шаг кронштейнов
        l1_m, l2_m = step / 1000.0, None
        n_p = (q_clad * b + q_rails) * l1_m
        c_m, k_ner, c_f = spans_const(scheme, rail_len, span)
        n_w = w_eff * l1_m * b * k_ner
        prof_span, prof_b = span, b
    elif scheme in ("interfloor", "interfloor_direct"):
        span = rail_len                  # пролёт = направляющая (этаж)
        l1_m, l2_m = rail_len / 1000.0, step / 1000.0
        n_p = (q_clad * l2_m + q_rails) * l1_m
        c_m, k_ner, c_f = spans_const(scheme, rail_len, span)
        n_w = w_eff * l1_m * l2_m * k_ner
        prof_span, prof_b = span, b
    elif scheme == "ortho":
        span = inp["v_step"]             # пролёт ЗП = верт. шаг гориз. профиля
        l1_m, l2_m = span / 1000.0, step / 1000.0
        n_p = (q_clad * b + q_rails) * l1_m
        c_m, k_ner, c_f = spans_const(scheme, rail_len, span)
        n_w = w_eff * l1_m * l2_m * k_ner
        prof_span, prof_b = span, b
    else:
        raise ValueError("scheme")

    checks = []

    # --- анкер
    br = dict(BRACKETS[inp["bracket"]])
    e1 = inp.get("e1", inp["offset"] - br["de1"])
    e2 = inp.get("e2", br["e2"])
    e3 = inp.get("e3", br["e3"])
    e4 = inp.get("e4", br["e4"])
    n_a = n_p * e1 / e2 + n_w * e3 / e4 + n_w
    checks.append(_checks_pack("анкер, кг", n_a, inp["na_max"] / G))

    # --- кронштейн 1-1 (моменты кг·см; σ кг/см²: кг·мм/мм³·100)
    m_x = n_p * e1 / 10.0
    m_y = n_w * br["e5"] / 10.0
    s11 = (m_x * 10.0 / br["Wx"] + m_y * 10.0 / br["Wy"]
           + n_w / br["A"]) * 100.0
    checks.append(_checks_pack("кронштейн 1-1, кг/см²", s11, ry))
    # --- кронштейн 2-2 (по шайбе анкера)
    s22 = (n_w * br["e6"] / 10.0) * 10.0 / br["Wy22"] * 100.0
    checks.append(_checks_pack("кронштейн 2-2, кг/см²", s22, ry))

    # --- удлинитель
    if inp.get("extender"):
        ex = EXTENDERS[inp["extender"]]
        s_ud = ((n_p * ex["e4"] / 10.0) * 10.0 / ex["Wx"]
                + (n_w * ex["e5"] / 10.0) * 10.0 / ex["Wy"]
                + n_w / ex["A"]) * 100.0
        checks.append(_checks_pack("удлинитель, кг/см²", s_ud, ry))

    # --- профиль (прочность + прогиб)
    prof = inp["profile"] if isinstance(inp["profile"], dict) \
        else PROFILES[inp["profile"]]
    l_cm = prof_span / 10.0
    m_prof = c_m * w_eff * prof_b * (prof_span / 1000.0) ** 2 * 100.0  # кг·см
    n_p_prof = inp.get("n_p_profile", n_p)
    s_prof = (m_prof * 10.0 / prof["Wx"] + n_p_prof / prof["A"]) * 100.0
    checks.append(_checks_pack("профиль σ, кг/см²", s_prof, ry))
    q_n = w_eff * prof_b / 1.4 / 100.0            # кг/см (нормативная)
    f_cm = c_f * q_n * l_cm ** 4 / (E_STEEL * prof["Jx"] / 1e4)
    checks.append(_checks_pack("профиль f, мм", f_cm * 10.0,
                               prof_span / F_LIMIT_DIV))

    # --- заклёпки (оба соединения — одни N)
    n_riv = inp.get("n_rivets", 2)
    n_res = math.sqrt(n_p ** 2 + n_w ** 2)
    checks.append(_checks_pack("заклёпки срез, кг", n_res / n_riv,
                               RIVET_SHEAR_N / (RIVET_GAMMA * G)))
    checks.append(_checks_pack("заклёпки смятие, кг/см²",
                               n_res / (n_riv * RIVET_D_CM * RIVET_T_CM),
                               RIVET_R_SMYAT))

    return dict(zone=zone, step=step, w_p=round(w_p, 1),
                n_p=round(n_p, 1), n_w=round(n_w, 1),
                checks=checks, passed=all(c["ok"] for c in checks))


def pick_step(inp, zone, candidates=None):
    """Максимальный шаг из candidates, проходящий все проверки.

    candidates: список мм (убыв. не обязателен). Дефолт: 50-мм сетка
    от 200 до max_step (inp['max_step'], дефолт 800 — конструктивный
    предел системы). Возвращает (step|None, chain|None, отчёт по всем).
    """
    if candidates is None:
        top = int(round(float(inp.get("max_step", 800))))
        candidates = list(range(200, top + 1, 50))
    best, best_chain, log = None, None, []
    for s in sorted(candidates):
        ch = calc_chain(inp, s, zone)
        log.append(ch)
        if ch["passed"]:
            best, best_chain = s, ch
    return best, best_chain, log


def report(inp, candidates=None):
    """Отчёт по обеим зонам: {'row': {...}, 'corner': {...}}."""
    out = {}
    for zone in ("row", "corner"):
        step, chain, log = pick_step(inp, zone, candidates)
        out[zone] = dict(step=step, chain=chain,
                         tried=[(c["step"], c["passed"]) for c in log])
    return out
