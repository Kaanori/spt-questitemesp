#!/usr/bin/env bash
# ---------------------------------------------------------------
# Macht aus den SVG-Zeichnungen in symbole/svg die PNG-Dateien in
# symbole/png, die beim Bauen IN die DLL wandern.
#
# Muss nur laufen, wenn eine Zeichnung geaendert oder eine neue
# hinzugekommen ist - die fertigen PNGs liegen im Projekt und sind
# eingecheckt.
#
# Die Bilder sind WEISS auf durchsichtig. Eingefaerbt wird zur
# Laufzeit; siehe src/Symbole.cs.
#
# Braucht rsvg-convert (Paket librsvg).
# ---------------------------------------------------------------
set -eu

hier=$(cd "$(dirname "$0")/.." && pwd)
quelle="$hier/symbole/svg"
ziel="$hier/symbole/png"

if ! command -v rsvg-convert >/dev/null 2>&1; then
    echo "rsvg-convert fehlt - bitte das Paket librsvg installieren." >&2
    exit 1
fi

mkdir -p "$ziel"

# 128 Bildpunkte: fein genug fuer die duennen Striche, klein genug,
# dass alle acht zusammen keine 15 KB in der DLL belegen. Wer das
# aendert, muss SymbolPunkteJeEinheit in src/Symbole.cs mitziehen -
# dort steht 12800 = 128 / 0,01 Einheiten.
kante=128

anzahl=0
for datei in "$quelle"/*.svg; do
    name=$(basename "$datei" .svg)
    rsvg-convert -w "$kante" -h "$kante" "$datei" -o "$ziel/$name.png"
    anzahl=$((anzahl + 1))
done

echo "$anzahl Symbole nach $ziel geschrieben ($kante x $kante)."
