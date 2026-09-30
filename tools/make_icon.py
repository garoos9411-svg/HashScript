# -*- coding: utf-8 -*-
"""
Генератор иконки приложения (щит + лупа) в формате .ico — бонус из ТЗ.
Запускать один раз при изменении дизайна иконки:  python make_icon.py
Нужна библиотека Pillow:  pip install pillow
Результат: ../src/ForensicCollector/app.ico
"""
import math
from PIL import Image, ImageDraw

SIZE = 256
img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# ---- Щит (тёмно-синий с голубой обводкой) ----------------------------------
cx = SIZE / 2.0
top, mid_y, bot = 22.0, 120.0, 236.0
w_top, w_mid = 92.0, 74.0

shield = [
    (cx - w_top + 18, top),          # верхняя левая фаска
    (cx + w_top - 18, top),          # верхняя правая фаска
    (cx + w_top, top + 18),          # скос вправо
    (cx + w_top, mid_y),             # прямые бока до середины
    (cx + w_mid, bot - 46),          # сужение к низу
    (cx, bot),                       # остриё щита
    (cx - w_mid, bot - 46),
    (cx - w_top, mid_y),
    (cx - w_top, top + 18),
]
d.polygon(shield, fill=(0, 90, 158, 255))
d.line(shield + [shield[0]], fill=(120, 200, 255, 255), width=7, joint="curve")

# ---- Лупа (белая поверх щита) ------------------------------------------------
gx, gy, gr = cx - 12, 106, 44
d.ellipse([gx - gr, gy - gr, gx + gr, gy + gr],
          fill=(20, 24, 30, 220), outline=(240, 240, 240, 255), width=10)
# Блик на стекле
d.arc([gx - gr + 14, gy - gr + 14, gx + 4, gy + 4], 180, 300,
      fill=(150, 215, 255, 255), width=7)
# Ручка лупы
hx0, hy0 = gx + gr * 0.72, gy + gr * 0.72
d.line([hx0, hy0, hx0 + 54, hy0 + 54], fill=(240, 240, 240, 255), width=18)

# ---- Сохранение ICO со всеми размерами --------------------------------------
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
imgs = [img.resize(s, Image.LANCZOS) for s in sizes]
base = imgs[-1]
base.save("../src/ForensicCollector/app.ico", format="ICO", sizes=sizes)
print("OK: app.ico создан:", sizes)
