#!/usr/bin/env python3
"""SPEC §12.5 + lint Rule 6 — compute bundle size delta vs baseline.

Usage:
  python3 scripts/check-bundle-delta.py --rid osx-arm64 --publish <publishDir>

Compares total bytes of the publish directory tree to the per-platform
line in docs/specs/opencli-bundle-baseline.txt. Exits non-zero if the
delta exceeds the SPEC budget for that platform.
"""
from __future__ import annotations
import argparse, os, sys, pathlib

BUDGETS_MB = {
    'osx-arm64': 35,
    'osx-x64':   35,
    'linux-x64': 50,
    'win-x64':   25,
}

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASELINE = ROOT / 'docs' / 'specs' / 'opencli-bundle-baseline.txt'


def load_baseline() -> dict[str, int]:
    out: dict[str, int] = {}
    for line in BASELINE.read_text().splitlines():
        line = line.split('#', 1)[0].strip()
        if not line:
            continue
        parts = line.split()
        if len(parts) >= 2:
            try:
                out[parts[0]] = int(parts[1])
            except ValueError:
                pass
    return out


def dir_bytes(path: pathlib.Path) -> int:
    total = 0
    for p in path.rglob('*'):
        if p.is_file() and not p.is_symlink():
            try:
                total += p.stat().st_size
            except OSError:
                pass
    return total


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--rid', required=True)
    ap.add_argument('--publish', required=True)
    args = ap.parse_args()

    if args.rid not in BUDGETS_MB:
        print(f'unknown RID {args.rid}', file=sys.stderr)
        return 2

    publish_dir = pathlib.Path(args.publish)
    if not publish_dir.is_dir():
        print(f'publish dir not found: {publish_dir}', file=sys.stderr)
        return 2

    baseline = load_baseline().get(args.rid, 0)
    current = dir_bytes(publish_dir)
    delta_mb = (current - baseline) / (1024 * 1024)
    budget_mb = BUDGETS_MB[args.rid]
    print(f'{args.rid}: baseline={baseline:,} bytes  current={current:,} bytes  delta={delta_mb:+.2f} MB  budget={budget_mb} MB')

    if baseline == 0:
        print(f'{args.rid}: baseline unrecorded — skipping budget check', file=sys.stderr)
        return 0
    if delta_mb > budget_mb:
        print(f'{args.rid}: delta {delta_mb:+.2f} MB exceeds budget {budget_mb} MB', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
