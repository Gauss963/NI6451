from pathlib import Path
import shutil

import PyInstaller.__main__


ROOT = Path(__file__).resolve().parent

NAME = "NI6451"
ENTRY = ROOT / "main.py"
ICON = ROOT / "assets" / "icon.ico"

BUILD_DIR = ROOT / "build"
DIST_DIR = ROOT / "dist"


def main():
    if BUILD_DIR.exists():
        shutil.rmtree(BUILD_DIR)

    if DIST_DIR.exists():
        shutil.rmtree(DIST_DIR)

    args = [
        str(ENTRY),

        f"--name={NAME}",
        "--onefile",

        "--windowed",

        "--clean",
        "--noconfirm",

        # f"--icon={ICON}",

        f"--distpath={DIST_DIR}",
        f"--workpath={BUILD_DIR}",
    ]

    PyInstaller.__main__.run(args)

    print()
    print("=" * 50)
    print("Build completed!")
    print(f"EXE: {DIST_DIR / f'{NAME}.exe'}")
    print("=" * 50)


if __name__ == "__main__":
    main()