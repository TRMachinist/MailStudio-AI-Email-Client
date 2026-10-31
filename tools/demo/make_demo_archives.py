"""Creates NW-1042-drawings.zip and NW-1042-drawings.rar demo archives from existing demo files."""
import os, subprocess, sys, zipfile
from pathlib import Path

def make_archives(demo_dir):
    demo_dir = Path(demo_dir).resolve()
    files = [
        "flange-DN100-revC.pdf",
        "flange-DN100-revC.dxf",
        "bracket-revC.stp",
        "OP20.nc"
    ]
    for f in files:
        fp = demo_dir / f
        if not fp.is_file():
            raise FileNotFoundError(f"Required demo file not found: {fp}")

    zip_path = demo_dir / "NW-1042-drawings.zip"
    print(f"Creating {zip_path}...")
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for f in files:
            zf.write(demo_dir / f, arcname=f)
    print(f"ZIP created: {zip_path.stat().st_size} bytes")

    rar_path = demo_dir / "NW-1042-drawings.rar"
    rar_exe = Path(r"C:\Program Files\WinRAR\rar.exe")
    if not rar_exe.is_file():
        # Check PATH
        import shutil
        rar_in_path = shutil.which("rar")
        if rar_in_path:
            rar_exe = Path(rar_in_path)

    if rar_exe.is_file():
        print(f"Creating {rar_path} using {rar_exe}...")
        if rar_path.exists():
            rar_path.unlink()
        cmd = [str(rar_exe), "a", "-ep", "-idq", str(rar_path)] + [str(demo_dir / f) for f in files]
        res = subprocess.run(cmd, capture_output=True, text=True)
        if res.returncode != 0:
            raise RuntimeError(f"rar.exe failed: {res.stderr}")
        print(f"RAR created: {rar_path.stat().st_size} bytes")
    else:
        print("Warning: rar.exe not found, cannot build .rar")

if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).resolve().parent.parent.parent / "src" / "MailStudio" / "Demo")
    make_archives(target)
