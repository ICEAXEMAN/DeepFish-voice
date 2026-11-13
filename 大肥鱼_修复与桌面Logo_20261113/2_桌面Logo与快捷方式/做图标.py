# 把蜡笔贴纸 logo（白底方形 PNG）做成透明背景的 PNG + 多尺寸 ICO，给桌面快捷方式用。
# 只把和四个角连着的白底挖成透明：贴纸里面的白圈被深蓝描边包着，不会被挖到。
import sys

import numpy as np
from PIL import Image, ImageDraw

src, out_png, out_ico = sys.argv[1], sys.argv[2], sys.argv[3]

image = Image.open(src).convert("RGBA")
w, h = image.size

# 从四个角往里灌一个图片里没有的颜色（品红），只灌和白底相近、连在一起的像素
sentinel = (255, 0, 255)
for point in ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)):
    ImageDraw.floodfill(image, point, sentinel, thresh=40)

array = np.array(image)
painted = (array[:, :, 0] == 255) & (array[:, :, 1] == 0) & (array[:, :, 2] == 255)
array[painted] = (0, 0, 0, 0)
image = Image.fromarray(array, "RGBA")

image.save(out_png)
sizes = [(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)]
image.resize((256, 256), Image.LANCZOS).save(out_ico, format="ICO", sizes=sizes)
print("ok", out_png, out_ico, image.size)
