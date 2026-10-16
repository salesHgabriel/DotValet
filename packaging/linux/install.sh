#!/usr/bin/env bash
# Instala o Dot Valet para o usuário atual: binário em ~/.local/bin, atalho no menu de aplicativos e ícone.
#   ./install.sh            instala
#   ./install.sh --uninstall remove
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
bin="$HOME/.local/bin"
apps="$HOME/.local/share/applications"
icons="$HOME/.local/share/icons/hicolor/512x512/apps"

if [[ "${1:-}" == "--uninstall" ]]; then
  rm -f "$bin/DotValet" "$apps/dotvalet.desktop" "$icons/dotvalet.png"
  echo "Dot Valet removido."
  exit 0
fi

mkdir -p "$bin" "$apps" "$icons"
install -m 755 "$here/DotValet" "$bin/DotValet"
install -m 644 "$here/dotvalet.png" "$icons/dotvalet.png"
sed "s|^Exec=.*|Exec=$bin/DotValet|" "$here/dotvalet.desktop" > "$apps/dotvalet.desktop"
chmod 644 "$apps/dotvalet.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$apps" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
echo "Dot Valet instalado: procure por \"Dot Valet\" no menu de aplicativos (ou rode $bin/DotValet)."
