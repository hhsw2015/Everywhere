#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
shopt -s nullglob

missing=0
for f in *.pdf *.docx *.xlsx *.pptx *.epub *.html *.htm *.txt *.md; do
    case "$f" in
        *.golden.txt) continue ;;
        SUMMARY.md) continue ;;
    esac
    [ -f "$f" ] || continue
    if [ ! -f "$f.golden.txt" ]; then
        echo "missing golden: $f"
        missing=1
    fi
done
exit $missing
