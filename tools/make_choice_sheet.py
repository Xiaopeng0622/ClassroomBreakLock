# -*- coding: utf-8 -*-
"""把两个候选图标并排拼成一张对比图，便于确认用哪一个。"""
import os
from PIL import Image

P = r"$PSScriptRoot\..\preview"

girl = Image.open(os.path.join(P, "dsh_icon_source.png")).convert("RGBA").resize((256, 256), Image.LANCZOS)
whale = Image.open(os.path.join(P, "dsh_exe_icon.png")).convert("RGBA").resize((256, 256), Image.LANCZOS)

pad = 24
sheet = Image.new("RGBA", (256 * 2 + pad * 3, 256 + pad * 2), (243, 243, 243, 255))
sheet.paste(girl, (pad, pad), girl)
sheet.paste(whale, (pad * 2 + 256, pad), whale)
sheet.save(os.path.join(P, "icon_choice.png"))
print("wrote icon_choice.png")
