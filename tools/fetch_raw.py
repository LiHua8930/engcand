#!/usr/bin/env python3
"""Download the raw lexical sources used by build_lexicon.py.

This script downloads the raw lexical sources into data/raw/. Those files are not
committed to the repository (size + upstream licenses): run this once, then build
the runtime dictionaries with tools/build_lexicon.py.

Usage:
    python tools/fetch_raw.py [--force] [--only NAME[,NAME...]]

Notes:
  * In the original environment plain `curl`/PowerShell web clients were blocked
    by the sandbox, but the bundled CPython urllib client works. This script
    therefore uses urllib only.
  * Every download is verified as UTF-8 decodable text and a minimum size.
"""
from __future__ import annotations

import argparse
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(ROOT, "data", "raw")

SOURCES = {
    # 50k English words with OpenSubtitles counts (FrequencyWords, hermitdave).
    # raw.githubusercontent.com is reset by this sandbox; the jsDelivr CDN mirror works.
    "en_50k_frequencywords.txt": (
        [
            "https://cdn.jsdelivr.net/gh/hermitdave/FrequencyWords@master/"
            "content/2018/en/en_50k.txt",
            "https://raw.githubusercontent.com/hermitdave/FrequencyWords/"
            "master/content/2018/en/en_50k.txt",
        ],
        400_000,
    ),
    # Google Web Trillion Word Corpus 1-grams (Peter Norvig).
    "norvig_count_1w.txt": (
        ["https://www.norvig.com/ngrams/count_1w.txt"],
        2_000_000,
    ),
    # Google Web Trillion Word Corpus 2-grams (Peter Norvig).
    "norvig_count_2w.txt": (
        ["https://www.norvig.com/ngrams/count_2w.txt"],
        4_000_000,
    ),
    # Google 10k words ordered by frequency (first20hours).
    "google-10000-english.txt": (
        [
            "https://cdn.jsdelivr.net/gh/first20hours/google-10000-english@master/"
            "google-10000-english.txt",
            "https://raw.githubusercontent.com/first20hours/google-10000-english/"
            "master/google-10000-english.txt",
        ],
        60_000,
    ),
}

USER_AGENT = "english-cand-lexicon-builder/1.0 (+local data pipeline)"

# Phrase / collocation lists from 2ndLA/english-phrases (CC BY-SA 4.0).
# One English phrase per line, no counts. Several CET/TEM/TOEFL/IELTS vocab levels.
_2NDLA_LISTS = [
    "cet4", "cet6", "gre", "ielts", "junior",
    "npee", "senior", "tem4", "tem8", "toefl",
]
for _list in _2NDLA_LISTS:
    SOURCES["phrases_2ndla_%s.txt" % _list] = (
        [
            "https://cdn.jsdelivr.net/gh/2ndLA/english-phrases@main/lists/%s.txt" % _list,
            "https://raw.githubusercontent.com/2ndLA/english-phrases/main/lists/%s.txt" % _list,
        ],
        1000,
    )

# ~500 English idioms (baiango/english_idioms), CSV with an "idiom" column.
SOURCES["idioms_baiango.csv"] = (
    [
        "https://cdn.jsdelivr.net/gh/baiango/english_idioms@main/idioms.csv",
        "https://raw.githubusercontent.com/baiango/english_idioms/main/idioms.csv",
    ],
    20_000,
)


def download(name: str, urls: list[str], min_size: int, force: bool) -> str:
    dest = os.path.join(RAW, name)
    if os.path.exists(dest) and not force and os.path.getsize(dest) >= min_size:
        return "keep   %-32s %9d bytes" % (name, os.path.getsize(dest))

    last_exc: Exception | None = None
    for url in urls:
        try:
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=60) as resp:
                body = resp.read()
            text = body.decode("utf-8")
            if len(body) < min_size:
                raise RuntimeError("downloaded only %d bytes (< %d)" % (len(body), min_size))
        except Exception as exc:  # noqa: BLE001 - try the next mirror
            last_exc = exc
            continue
        os.makedirs(RAW, exist_ok=True)
        with open(dest, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        return "fetch  %-32s %9d bytes" % (name, len(body))
    raise RuntimeError("all mirrors failed: %r" % (last_exc,))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--force", action="store_true", help="re-download even if the file exists")
    ap.add_argument("--only", default="", help="comma separated source file names")
    args = ap.parse_args()
    only = {s.strip() for s in args.only.split(",") if s.strip()}

    failures = 0
    for name, (urls, min_size) in SOURCES.items():
        if only and name not in only:
            continue
        try:
            print(download(name, urls, min_size, args.force))
        except Exception as exc:  # noqa: BLE001 - report and continue
            failures += 1
            print("FAIL   %-32s %s" % (name, exc), file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
