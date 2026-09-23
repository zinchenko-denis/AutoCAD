# -*- coding: utf-8 -*-
"""Картинки к PDF сборки №25 — НАСТОЯЩИЕ прогоны движков: «было» — версия
движка до правок 23.09o (из git, коммит 16bc284), «стало» — текущая.
Запуск из AFrame/docs: python3 make_pics25.py"""
import os, sys, subprocess, importlib.util, tempfile
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Rectangle, FancyArrowPatch
HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(HERE, "build25"); os.makedirs(OUT, exist_ok=True)
plt.rcParams["font.family"] = "DejaVu Sans"

def load(relpath, rev=None, name="m"):
    if rev is None:
        path = os.path.join(ROOT, relpath)
    else:
        import shutil
        src = subprocess.check_output(["git", "-C", ROOT, "show", "%s:%s" % (rev, relpath)])
        tmp = os.path.join(tempfile.mkdtemp(), "eng")          # весь каталог движка (systems.json и т.п.)
        shutil.copytree(os.path.dirname(os.path.join(ROOT, relpath)), tmp)
        path = os.path.join(tmp, os.path.basename(relpath)); open(path, "wb").write(src)
    sys.path.insert(0, os.path.dirname(path))
    spec = importlib.util.spec_from_file_location(name, path); m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m); sys.path.pop(0); return m

def rect(x0, y0, x1, y1): return [[x0, y0], [x1, y0], [x1, y1], [x0, y1]]

# ── 1. ортогональная: кусок вертикали над окном, выше последнего ГП ──
req = {"system": "Ортогональная", "sub_type": "ortho",
       "contours": [{"outer": rect(0, 0, 4000, 6700), "holes": [rect(1500, 1000, 2500, 3305)]}],
       "joints_x": [600, 1200, 1800, 2400, 3000, 3600]}
old = load("AFrame/engine/frame_plan.py", "16bc284", "fp_old").frame_plan(dict(req))
new = load("AFrame/engine/frame_plan.py", None, "fp_new").frame_plan(dict(req))
fig, axs = plt.subplots(1, 2, figsize=(9.6, 7.6))
for ax, p, title in ((axs[0], old, "было (сборка №24)"), (axs[1], new, "стало (сборка №25)")):
    ax.add_patch(Rectangle((0, 0), 4000, 6700, fill=False, lw=1.2, ec="#555"))
    ax.add_patch(Rectangle((1500, 1000), 1000, 2305, fc="#dde7f0", ec="#557", lw=1))
    ax.text(2000, 2150, "окно", ha="center", va="center", fontsize=9, color="#335")
    oldy = {round(h["y"]) for h in old["hrails"]}
    for h in p["hrails"]:
        added = round(h["y"]) not in oldy
        ax.plot([h["x0"], h["x1"]], [h["y"], h["y"]], color="#d9480f" if added else "#8a8a8a", lw=3 if added else 1.3)
    for r in p["rails"]:
        hang = r["y0"] > 6000 and 1400 < r["x"] < 2600
        ax.plot([r["x"], r["x"]], [r["y0"], r["y1"]], color="#c92a2a" if (hang and p is old) else "#2b8a3e", lw=2.2)
    xs = [b["x"] for b in p["brackets"]]; ys = [b["y"] for b in p["brackets"]]
    ax.plot(xs, ys, "s", ms=3.2, color="#333")
    ax.set_xlim(-200, 4200); ax.set_ylim(-200, 6900); ax.set_aspect("equal"); ax.set_title(title, fontsize=11)
    ax.set_xticks([]); ax.set_yticks([0, 3305, 6300, 6700]); ax.tick_params(labelsize=8)
axs[0].annotate("кусок над окном 390 мм\nни на что не опирается", xy=(1800, 6500), xytext=(2350, 4700), fontsize=8.5,
                color="#c92a2a", bbox=dict(fc="white", ec="none", alpha=0.92), arrowprops=dict(arrowstyle="->", color="#c92a2a"))
axs[1].annotate("доп. ГП на 300 ниже верха\n(+ кронштейны по сетке)", xy=(3000, 6400), xytext=(2250, 4700), fontsize=8.5,
                color="#d9480f", bbox=dict(fc="white", ec="none", alpha=0.92), arrowprops=dict(arrowstyle="->", color="#d9480f"))
fig.tight_layout(); fig.savefig(os.path.join(OUT, "ortho_top.png"), dpi=150); plt.close(fig)
print("ortho_top: ГП было", sorted({round(h['y']) for h in old['hrails']})[-3:], "стало", sorted({round(h['y']) for h in new['hrails']})[-3:])

# ── 2. откос: бок окна частично на краю зоны (Г-образная зона) ──
L = [[0, 0], [9000, 0], [9000, 6000], [6000, 6000], [6000, 3000], [0, 3000]]
W = rect(6000, 2000, 7000, 4000)
def jambs(mod):
    zds, _ = mod.build_zones_from_contours([{"id": "W", "pts": L}, {"id": "O", "pts": W}])
    return mod.zone_report(mod.load_zone(zds[0]))["jambs_total_m"]
j_old = jambs(load("Facades/engine/facade_zones.py", "16bc284", "fz_old"))
j_new = jambs(load("Facades/engine/facade_zones.py", None, "fz_new"))
fig, axs = plt.subplots(1, 2, figsize=(9.6, 4.6))
for ax, title, full_left in ((axs[0], "было: откосы %.1f м" % j_old, False), (axs[1], "стало: откосы %.1f м" % j_new, True)):
    xs, ys = zip(*(L + [L[0]])); ax.fill(xs, ys, fc="#f3efe6", ec="#555", lw=1.2)
    ax.add_patch(Rectangle((6000, 2000), 1000, 2000, fc="white", ec="#999", lw=0.8))
    J, B, N = "#2b8a3e", "#1c7ed6", "#adb5bd"
    ax.plot([6000, 7000], [4000, 4000], color=J, lw=4)                  # верх — откос
    ax.plot([7000, 7000], [2000, 4000], color=J, lw=4)                  # правый бок — откос
    ax.plot([6000, 7000], [2000, 2000], color=B, lw=4)                  # низ — отлив
    ax.plot([6000, 6000], [2000, 3000], color=J, lw=4)                  # левый бок внутри зоны
    ax.plot([6000, 6000], [3000, 4000], color=J if full_left else N, lw=4)   # левый бок на краю зоны
    ax.set_aspect("equal"); ax.set_xlim(4500, 9500); ax.set_ylim(1000, 6300); ax.set_xticks([]); ax.set_yticks([])
    ax.set_title(title, fontsize=11)
axs[0].annotate("этот метр лежит на краю зоны —\nне считался откосом", xy=(6000, 3500), xytext=(4600, 5200), fontsize=8.5,
                arrowprops=dict(arrowstyle="->"))
axs[1].annotate("бок — откос целиком\n(ваш ответ)", xy=(6000, 3500), xytext=(4600, 5200), fontsize=8.5,
                arrowprops=dict(arrowstyle="->"))
fig.text(0.5, 0.02, "зелёный — откос, синий — отлив, серый — не считается", ha="center", fontsize=8.5)
fig.tight_layout(rect=(0, 0.05, 1, 1)); fig.savefig(os.path.join(OUT, "jamb_edge.png"), dpi=150); plt.close(fig)
print("откосы: было %.1f м, стало %.1f м" % (j_old, j_new))

# ── 3. COPY зоны с раскладкой: схема ──
fig, axs = plt.subplots(1, 2, figsize=(9.6, 4.0))
for ax, title, fixed in ((axs[0], "сборка №24", False), (axs[1], "сборка №25", True)):
    for yb, lab in ((2.4, "оригинал"), (0.2, "копия")):
        ax.add_patch(Rectangle((0, yb), 6, 1.6, fill=False, ls="--", ec="#888"))
        ax.text(0.15, yb + 1.35, lab, fontsize=9, color="#555")
        ax.add_patch(Rectangle((0.2, yb + 0.35), 1.5, 0.6, fc="#e9ecef", ec="#868e96"))
        ax.text(0.95, yb + 0.65, "метка", ha="center", va="center", fontsize=8.5)
        col = "#2b8a3e" if (yb > 1 or fixed) else "#c92a2a"
        for k in range(3):
            ax.add_patch(Rectangle((2.4 + 1.2 * k, yb + 0.3), 1.0, 0.7, fc=col, alpha=0.8, ec="none"))
        if yb > 1 or fixed:
            ax.add_patch(FancyArrowPatch((1.72, yb + 0.65), (2.35, yb + 0.65), arrowstyle="->", mutation_scale=10))
    if not fixed:
        ax.add_patch(FancyArrowPatch((0.95, 0.97), (2.9, 2.68), arrowstyle="->", ls="--", color="#c92a2a", mutation_scale=10))
        ax.text(3, -0.25, "скопированные плитки — ничьи, новая раскладка ложится сверху", ha="center", fontsize=8.2, color="#c92a2a")
    else:
        ax.text(3, -0.25, "метка копии ведёт к своим плиткам — они заменяются", ha="center", fontsize=8.2, color="#2b8a3e")
    ax.set_xlim(-0.2, 6.2); ax.set_ylim(-0.5, 4.3); ax.axis("off"); ax.set_title(title, fontsize=11)
fig.tight_layout(); fig.savefig(os.path.join(OUT, "copy_fix.png"), dpi=150); plt.close(fig)
print("готово:", sorted(os.listdir(OUT)))
