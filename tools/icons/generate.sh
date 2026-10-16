#!/usr/bin/env bash
# Gera os ícones do app a partir de icon.svg / icon-macos.svg.
# Requer rsvg-convert (brew install librsvg) e python3; o .icns exige o iconutil do macOS.
set -euo pipefail
cd "$(dirname "$0")"
root=../..
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# Windows (.ico com PNGs embutidos) + ícone da janela (Avalonia).
for s in 16 24 32 48 64 128 256; do
  rsvg-convert -w $s -h $s icon.svg -o "$tmp/$s.png"
done
python3 - "$tmp" "$root/src/DotValet/Assets/icon.ico" <<'PY'
import struct, sys
tmp, out = sys.argv[1], sys.argv[2]
sizes = [16, 24, 32, 48, 64, 128, 256]
data = [open(f"{tmp}/{s}.png", "rb").read() for s in sizes]
offset = 6 + 16 * len(sizes)
header = struct.pack("<HHH", 0, 1, len(sizes))
entries = b""
for s, d in zip(sizes, data):
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(d), offset)
    offset += len(d)
open(out, "wb").write(header + entries + b"".join(data))
PY

# Linux (.desktop aponta para dotvalet.png).
rsvg-convert -w 512 -h 512 icon.svg -o "$root/packaging/linux/dotvalet.png"

# README / GitHub.
rsvg-convert -w 256 -h 256 icon.svg -o "$root/docs/icon.png"

# macOS (.icns).
if command -v iconutil >/dev/null; then
  set_dir="$tmp/DotValet.iconset"
  mkdir -p "$set_dir"
  for s in 16 32 128 256 512; do
    rsvg-convert -w $s -h $s icon-macos.svg -o "$set_dir/icon_${s}x${s}.png"
    rsvg-convert -w $((s * 2)) -h $((s * 2)) icon-macos.svg -o "$set_dir/icon_${s}x${s}@2x.png"
  done
  iconutil -c icns "$set_dir" -o "$root/packaging/macos/DotValet.icns"
else
  echo "iconutil não encontrado: DotValet.icns não foi atualizado (rode no macOS)." >&2
fi
echo "Ícones gerados."
