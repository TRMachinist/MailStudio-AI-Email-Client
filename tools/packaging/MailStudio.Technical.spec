# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec of the technical worker (STEP / PDF / OCR / DXF-DWG).
#   python -m PyInstaller --noconfirm --distpath work\technical-dist --workpath work\technical-build tools\packaging\MailStudio.Technical.spec
import os
from PyInstaller.utils.hooks import collect_all

datas, binaries, hiddenimports = [], [], []
for package in ('OCP', 'rapidocr', 'onnxruntime', 'ezdxf', 'fontTools', 'pypdfium2'):
    d, b, h = collect_all(package)
    datas += d; binaries += b; hiddenimports += h
hiddenimports += ['extract']  # imported lazily by worker.py

a = Analysis(
    [os.path.join(SPECPATH, '..', '..', 'src', 'MailStudio.Technical', 'worker.py')],
    pathex=[], binaries=binaries, datas=datas, hiddenimports=hiddenimports,
    hookspath=[], hooksconfig={}, runtime_hooks=[], excludes=[], noarchive=False, optimize=0,
)
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, [], exclude_binaries=True, name='MailStudio.Technical', debug=False,
          bootloader_ignore_signals=False, strip=False, upx=True, console=True,
          disable_windowed_traceback=False, argv_emulation=False, target_arch=None,
          codesign_identity=None, entitlements_file=None)
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=True, upx_exclude=[], name='MailStudio.Technical')
