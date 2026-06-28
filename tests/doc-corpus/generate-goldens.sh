#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

shopt -s nullglob

for f in *.pdf; do
    pdftotext -layout "$f" "$f.golden.txt"
done

for f in *.docx *.pptx *.epub *.html *.htm; do
    pandoc -t plain "$f" -o "$f.golden.txt"
done

for f in *.xlsx; do
    xlsx2csv "$f" "$f.golden.txt"
done

for f in *.txt *.md; do
    case "$f" in
        *.golden.txt) continue ;;
    esac
    cp "$f" "$f.golden.txt"
done

echo "goldens generated:"
ls *.golden.txt
