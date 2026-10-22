import os
from PIL import Image, ImageDraw, ImageFont

RAW_FRAME = r"D:\MailStudio\work\video\linkedin2\raw\C05-step\f00035.jpg"
OUT_THUMB = r"D:\MailStudio\work\video\linkedin2\thumbnail.jpg"

im = Image.open(RAW_FRAME).convert("RGB")
# Scale smoothly to 1920x1080
thumb = im.resize((1920, 1080), Image.Resampling.LANCZOS)
draw = ImageDraw.Draw(thumb)

# Load font if available or use default
try:
    font_title = ImageFont.truetype("segoeuib.ttf", 46)
    font_sub = ImageFont.truetype("segoeui.ttf", 24)
    font_tag = ImageFont.truetype("segoeuib.ttf", 20)
except Exception:
    font_title = font_sub = font_tag = ImageFont.load_default()

# Elegant top-left pill overlay
badge_x, badge_y = 60, 50
badge_w, badge_h = 580, 110

# Dark frosted background
overlay = Image.new("RGBA", thumb.size, (0, 0, 0, 0))
ov_draw = ImageDraw.Draw(overlay)
ov_draw.rounded_rectangle([badge_x, badge_y, badge_x + badge_w, badge_y + badge_h], radius=16, fill=(14, 15, 18, 220), outline=(255, 255, 255, 35), width=1)

# Tags pill in bottom-right
tag_w, tag_h = 320, 48
tag_x = 1920 - tag_w - 60
tag_y = 1080 - tag_h - 60
ov_draw.rounded_rectangle([tag_x, tag_y, tag_x + tag_w, tag_y + tag_h], radius=12, fill=(14, 15, 18, 220), outline=(59, 130, 246, 120), width=1)

thumb = Image.alpha_composite(thumb.convert("RGBA"), overlay).convert("RGB")
draw = ImageDraw.Draw(thumb)

# Title & subtitle
draw.text((badge_x + 28, badge_y + 18), "MailStudio ", font=font_title, fill=(255, 255, 255))
# Accent 1.8
ms_w = draw.textlength("MailStudio ", font=font_title)
draw.text((badge_x + 28 + ms_w, badge_y + 18), "1.8", font=font_title, fill=(59, 130, 246))

draw.text((badge_x + 28, badge_y + 72), "Native STEP B-Rep & Machining Stock Analysis", font=font_sub, fill=(209, 213, 219))

# Tag text
draw.text((tag_x + 24, tag_y + 12), "100% Offline · Open Source", font=font_tag, fill=(147, 197, 253))

thumb.save(OUT_THUMB, "JPEG", quality=95)
print(f"Generated {OUT_THUMB} (1920x1080, JPEG 95%) successfully.")
