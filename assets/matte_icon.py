from rembg import remove, new_session
from PIL import Image

session = new_session("isnet-general-use")
src = Image.open(r"D:\code\scrcpy-win64\assets\icon-src.png").convert("RGBA")
out = remove(src, session=session)  # returns RGBA with background removed
# 清理边缘：把几乎全透明的像素置为全透明，防止白色杂边
px = out.load()
w, h = out.size
for y in range(h):
    for x in range(w):
        a = px[x, y][3]
        if a < 12:
            px[x, y] = (0, 0, 0, 0)
out.save(r"D:\code\scrcpy-win64\assets\icon-matted.png")
print("matted", out.size)
# alpha 统计
alpha = out.getchannel("A")
print("alpha min/max/mean:", alpha.getextrema(), sum(alpha.getdata()) / (w * h))
