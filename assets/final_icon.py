from PIL import Image, ImageDraw

im = Image.open(r"D:\code\scrcpy-win64\assets\icon-matted.png")
# 裁方形主体：月牙+水面倒影（原图坐标）
crop = im.crop((640, 500, 1410, 1270))  # 770x770
print("crop", crop.size)

# 清理低透明度毛边
px = crop.load()
w, h = crop.size
for y in range(h):
    for x in range(w):
        a = px[x, y][3]
        if a < 40:
            px[x, y] = (0, 0, 0, 0)

# 圆角 mask（半径约 24%）
mask = Image.new("L", (w, h), 0)
d = ImageDraw.Draw(mask)
d.rounded_rectangle([0, 0, w - 1, h - 1], radius=int(w * 0.24), fill=255)
crop.putalpha(mask)

# 再次清理
px = crop.load()
for y in range(h):
    for x in range(w):
        a = px[x, y][3]
        if a < 40:
            px[x, y] = (0, 0, 0, 0)

crop.save(r"D:\code\scrcpy-win64\assets\icon-clean.png")
print("saved icon-clean")
a = crop.getchannel("A")
for pt in [(0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)]:
    print("corner", pt, a.getpixel(pt))
