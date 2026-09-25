# -*- coding: utf-8 -*-
"""用 DSH 启动器的蓝色小鲸鱼图标生成课间锁的 app.ico（多尺寸）。

用法：python make_icon.py
源：D:\\openclaw\\tools\\dsh-launcher\\DSH-Launcher.ico
输出：src/ClassroomBreakLock/Assets/app.ico
      preview/icon_preview.png（目检用）
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(HERE, ".."))
SRC = r"D:\openclaw\tools\dsh-launcher\DSH-Launcher.ico"
OUT_DIR = os.path.join(PROJ, "src", "ClassroomBreakLock", "Assets")
PREVIEW_DIR = os.path.join(PROJ, "preview")

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def main() -> None:
    os.makedirs(OUT_DIR, exist_ok=True)
    os.makedirs(PREVIEW_DIR, exist_ok=True)

    with Image.open(SRC) as im:
        frames = sorted(im.ico.sizes()) if hasattr(im, "ico") else [(im.width, im.height)]
        print("source frames:", frames)
        im.size = max(frames, key=lambda s: s[0] * s[1])
        master = im.convert("RGBA").copy()

    print("master:", master.size)

    ico_path = os.path.join(OUT_DIR, "app.ico")
    master.save(ico_path, format="ICO", sizes=[(s, s) for s in SIZES])

    # 目检图：256 放大 + 32 最近邻放大
    big = master.resize((256, 256), Image.LANCZOS) if master.width < 256 else master
    small = master.resize((32, 32), Image.LANCZOS).resize((256, 256), Image.NEAREST)
    sheet = Image.new("RGBA", (256 * 2 + 24, 256), (243, 243, 243, 255))
    sheet.paste(big, (0, 0), big)
    sheet.paste(small, (280, 0), small)
    sheet.save(os.path.join(PREVIEW_DIR, "icon_preview.png"))

    master.resize((256, 256), Image.LANCZOS).save(os.path.join(PREVIEW_DIR, "dsh_icon_source.png"))

    print("wrote:", ico_path, os.path.getsize(ico_path), "bytes")


if __name__ == "__main__":
    main()
