#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

shopt -s nullglob

for f in *.pdf; do
    pdftotext -layout "$f" "$f.golden.txt"
done

for f in *.docx *.epub *.html *.htm; do
    pandoc -t plain "$f" -o "$f.golden.txt"
done

for f in *.pptx; do
    # pandoc has no pptx reader; extract text via OpenXML unzip.
    python3 - "$f" "$f.golden.txt" <<'PY'
import sys, zipfile, re
src, dst = sys.argv[1], sys.argv[2]
ns = '{http://schemas.openxmlformats.org/drawingml/2006/main}t'
out = []
with zipfile.ZipFile(src) as z:
    parts = sorted(n for n in z.namelist() if n.startswith('ppt/slides/slide') and n.endswith('.xml'))
    for p in parts:
        xml = z.read(p).decode('utf-8', 'replace')
        for m in re.findall(r'<a:t[^>]*>([^<]*)</a:t>', xml):
            out.append(m)
with open(dst, 'w', encoding='utf-8') as f:
    f.write('\n'.join(out) + '\n')
PY
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
