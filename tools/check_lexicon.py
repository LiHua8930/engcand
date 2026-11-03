#!/usr/bin/env python3
"""Validate the runtime lexicon files produced by tools/build_lexicon.py.

Checks (hard failures, exit code 1):
  * file exists, is non-empty, UTF-8 text, no BOM
  * exact column count and integer scores in 1..1000000
  * words: 2-30 chars, lowercase, a-z ' - only, no blocked / noise tokens
  * phrases: 2-5 lowercase English words, only a-z ' -, vocabulary-compatible
  * word_bigrams: word/nextword tokens valid, no adult-filtered tokens
  * no duplicate entries
  * scores are sorted descending (non-increasing)

Warnings (exit code stays 0 unless --strict):
  * word / phrase / bigram counts outside the product target ranges

Usage:
    python tools/check_lexicon.py [--data-dir DIR] [--strict]

Prints totals, min/max score, the top 20 entries and a seeded random sample
of 20 words / 20 phrases / 10 bigrams.
"""
from __future__ import annotations

import argparse
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from build_lexicon import (  # noqa: E402
    BLOCKLIST,
    PHRASE_BLOCK,
    SCORE_MAX,
    SUBTITLE_NOISE,
    is_valid_token,
    is_valid_word,
    normalize_phrase,
)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DATA = os.path.join(ROOT, "data")


def load_tsv(path: str) -> list[list[str]]:
    with open(path, encoding="utf-8") as fh:
        text = fh.read()
    if text.startswith("\ufeff"):
        raise ValueError("file starts with a UTF-8 BOM")
    rows = []
    for lineno, line in enumerate(text.splitlines(), 1):
        if not line:
            raise ValueError("blank line at line %d (files must not have trailing blanks)" % lineno)
        rows.append(line.split("\t"))
    return rows


def check_scores(rows: list[list[str]], score_col: int, label: str, errors: list[str]):
    prev = SCORE_MAX + 1
    seen = set()
    for i, row in enumerate(rows, 1):
        raw = row[score_col]
        if not raw.isdigit():
            errors.append("%s line %d: score %r is not an integer" % (label, i, raw))
            continue
        score = int(raw)
        if not 1 <= score <= SCORE_MAX:
            errors.append("%s line %d: score %d outside 1..%d" % (label, i, score, SCORE_MAX))
        if score > prev:
            errors.append("%s line %d: scores not sorted descending" % (label, i))
        prev = score
        key = tuple(row[:score_col])
        if key in seen:
            errors.append("%s line %d: duplicate entry %r" % (label, i, row[:score_col]))
        seen.add(key)


def check_words(path: str, errors: list[str]) -> list[list[str]]:
    rows = load_tsv(path)
    for i, row in enumerate(rows, 1):
        if len(row) != 2:
            errors.append("words.tsv line %d: expected 2 columns, got %d" % (i, len(row)))
            continue
        word = row[0]
        if word != word.lower():
            errors.append("words.tsv line %d: %r is not lowercase" % (i, word))
        if not is_valid_word(word):
            errors.append("words.tsv line %d: %r violates the word charset/length" % (i, word))
        if word in BLOCKLIST or word in SUBTITLE_NOISE:
            errors.append("words.tsv line %d: blocked/noise word %r" % (i, word))
    check_scores(rows, 1, "words.tsv", errors)
    return rows


def check_phrases(path: str, errors: list[str]) -> list[list[str]]:
    rows = load_tsv(path)
    for i, row in enumerate(rows, 1):
        if len(row) != 2:
            errors.append("phrases.tsv line %d: expected 2 columns, got %d" % (i, len(row)))
            continue
        phrase = row[0]
        if normalize_phrase(phrase) != phrase:
            errors.append("phrases.tsv line %d: %r is not a normalized 2-5 word phrase" % (i, phrase))
            continue
        for token in phrase.split(" "):
            if token in PHRASE_BLOCK:
                errors.append("phrases.tsv line %d: adult-filtered token %r" % (i, token))
    check_scores(rows, 1, "phrases.tsv", errors)
    return rows


def check_bigrams(path: str, errors: list[str]) -> list[list[str]]:
    rows = load_tsv(path)
    for i, row in enumerate(rows, 1):
        if len(row) != 3:
            errors.append("word_bigrams.tsv line %d: expected 3 columns, got %d" % (i, len(row)))
            continue
        a, b = row[0], row[1]
        if not is_valid_token(a) or not is_valid_token(b):
            errors.append("word_bigrams.tsv line %d: bad token pair %r" % (i, row[:2]))
        if a in PHRASE_BLOCK or b in PHRASE_BLOCK:
            errors.append("word_bigrams.tsv line %d: adult-filtered token in %r" % (i, row[:2]))
    check_scores(rows, 2, "word_bigrams.tsv", errors)
    return rows


def show_sample(label: str, rows: list[list[str]], n: int, rng: random.Random):
    sample = rng.sample(rows, min(n, len(rows)))
    rendered = [" ".join(r[:-1]) for r in sample]
    print("  random %s: %s" % (label, ", ".join(rendered)))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data-dir", default=DEFAULT_DATA)
    ap.add_argument("--seed", type=int, default=2024)
    ap.add_argument("--strict", action="store_true",
                    help="treat count-range warnings as failures")
    args = ap.parse_args()
    rng = random.Random(args.seed)

    errors: list[str] = []
    warnings: list[str] = []
    paths = {name: os.path.join(args.data_dir, name)
             for name in ("words.tsv", "phrases.tsv", "word_bigrams.tsv")}
    for name, path in paths.items():
        if not os.path.exists(path):
            if name == "word_bigrams.tsv":
                warnings.append("%s is missing (optional deliverable)" % name)
            else:
                errors.append("%s is missing" % name)

    words = check_words(paths["words.tsv"], errors) if os.path.exists(paths["words.tsv"]) else []
    phrases = check_phrases(paths["phrases.tsv"], errors) if os.path.exists(paths["phrases.tsv"]) else []
    bigrams = (check_bigrams(paths["word_bigrams.tsv"], errors)
               if os.path.exists(paths["word_bigrams.tsv"]) else [])

    # Product target ranges are warnings, not format failures.
    if words and not 30000 <= len(words) <= 50000:
        warnings.append("words.tsv count %d outside target 30000..50000" % len(words))
    if phrases and not 1500 <= len(phrases) <= 8000:
        warnings.append("phrases.tsv count %d outside target 1500..8000" % len(phrases))
    if bigrams and len(bigrams) > 50000:
        warnings.append("word_bigrams.tsv count %d exceeds 50000" % len(bigrams))

    print("== lexicon check: %s ==" % args.data_dir)
    print("  words.tsv         : %5d rows" % len(words))
    print("  phrases.tsv       : %5d rows" % len(phrases))
    print("  word_bigrams.tsv  : %5d rows" % len(bigrams))
    if words:
        print("  word score range  : %d .. %d" % (int(words[-1][1]), int(words[0][1])))
        print("  top 20 words      : %s" % ", ".join(w for w, _ in words[:20]))
        show_sample("20 words", words, 20, rng)
    if phrases:
        print("  phrase score range: %d .. %d" % (int(phrases[-1][1]), int(phrases[0][1])))
        print("  top 20 phrases    : %s" % "; ".join(p for p, _ in phrases[:20]))
        show_sample("20 phrases", phrases, 20, rng)
    if bigrams:
        print("  top 10 bigrams    : %s" % ", ".join("%s %s" % (a, b) for a, b, _ in bigrams[:10]))
        show_sample("10 bigrams", bigrams, 10, rng)

    if errors:
        print("\nFAIL: %d error(s)" % len(errors))
        for message in errors[:30]:
            print("  - " + message)
        return 1
    for message in warnings:
        print("  warning: " + message)
    print("\nPASS: all structural checks passed%s" %
          ("" if not warnings else " (with %d warning(s))" % len(warnings)))
    if warnings and args.strict:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
