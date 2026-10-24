from PIL import Image

src = Image.open(r"D:\code\scrcpy-win64\assets\icon-src.png").convert("RGBA")
print("src", src.size)
src.thumbnail((256, 256), Image.LANCZOS)
sizes = [256, 128, 64, 48, 32, 24, 16]
src.save(r"D:\code\scrcpy-win64\assets\app.ico", format="ICO", sizes=[(s, s) for s in sizes])
print("saved ico")
