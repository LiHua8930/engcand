#!/usr/bin/env python3
"""Deterministic builder for the english-cand runtime lexicon.

Inputs (plain text, see data/raw/README.md):
  data/raw/en_50k_frequencywords.txt   50k OpenSubtitles words + counts
  data/raw/google-10000-english.txt    Google 10k words, frequency order
  data/raw/norvig_count_1w.txt         Google Web 1-gram counts
  data/raw/norvig_count_2w.txt         Google Web 2-gram counts
  data/raw/phrases_2ndla_*.txt         English collocation / idiom lists
  data/raw/idioms_baiango.csv          {idiom}>>{meaning} CSV
  data/raw/phrases_curated.txt         hand-curated everyday phrases

Outputs (written to data/ unless --out-dir is given):
  words.tsv            word<TAB>score                2-30 chars, a-z ' -, score 1..1000000
  phrases.tsv          phrase<TAB>score              2-5 words, same charset, score 1..1000000
  word_bigrams.tsv     word<TAB>nextword<TAB>score   next-word prediction, score 1..1000000

The build is deterministic and idempotent: the same raw files always produce
byte-identical outputs.  Re-run with:

    python tools/build_lexicon.py

A debug view of rejected curated phrases is printed so the curated file can be
kept within the vocabulary of the accepted word list.
"""
from __future__ import annotations

import argparse
import csv
import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_RAW = os.path.join(ROOT, "data", "raw")
DEFAULT_OUT = os.path.join(ROOT, "data")

SCORE_MAX = 1_000_000

# ---------------------------------------------------------------------------
# Character / shape rules (shared with check_lexicon.py)
# ---------------------------------------------------------------------------
# Lowercase English word: first char a-z, then 1..29 chars from a-z ' -
# (total length 2..30). Rejects numbers, dots and unicode letters.
WORD_RE = re.compile(r"^[a-z][a-z'\-]{1,29}$")
PHRASE_RE = re.compile(r"^[a-z][a-z'\- ]{1,200}$")

# Single letters are allowed inside phrases/bigrams (a bit / i think) but not
# as standalone dictionary entries (spec requires word length 2..30).
SHORT_TOKENS = {"a", "i"}

# Word forms that come from the OpenSubtitles tokenizer splitting contractions.
# They are not words by themselves (the contraction is generated below instead).
CONTRACTION_FRAGMENTS = {
    "don", "doesn", "didn", "isn", "aren", "wasn", "weren", "couldn",
    "wouldn", "shouldn", "haven", "hasn", "hadn", "mustn", "needn", "ain",
}

# Contraction -> (fragment token, share of the fragment count).
# Estimates come from the subtitle tokenizer splitting "don't" into "don"+"'t";
# the share roughly matches how often the contraction dominates the fragment.
CONTRACTIONS = [
    ("don't", "don", 0.95), ("doesn't", "doesn", 0.90), ("didn't", "didn", 0.90),
    ("isn't", "isn", 0.85), ("aren't", "aren", 0.90), ("wasn't", "wasn", 0.90),
    ("weren't", "weren", 0.90), ("couldn't", "couldn", 0.90),
    ("wouldn't", "wouldn", 0.90), ("shouldn't", "shouldn", 0.90),
    ("haven't", "haven", 0.85), ("hasn't", "hasn", 0.90),
    ("hadn't", "hadn", 0.90), ("mustn't", "mustn", 0.50),
    ("needn't", "needn", 0.50), ("ain't", "ain", 0.90),
    # 's: it's / that's / let's ... ; the rest of "'s" is possessive and is not
    # listed as a separate dictionary word.
    ("it's", "'s", 0.28), ("that's", "'s", 0.14), ("let's", "'s", 0.10),
    ("he's", "'s", 0.07), ("she's", "'s", 0.05), ("what's", "'s", 0.07),
    ("there's", "'s", 0.06), ("here's", "'s", 0.035), ("who's", "'s", 0.03),
    ("where's", "'s", 0.02), ("how's", "'s", 0.02), ("when's", "'s", 0.005),
    # 're
    ("you're", "'re", 0.45), ("we're", "'re", 0.20), ("they're", "'re", 0.20),
    # 'll
    ("i'll", "'ll", 0.32), ("you'll", "'ll", 0.22), ("we'll", "'ll", 0.12),
    ("they'll", "'ll", 0.12), ("he'll", "'ll", 0.10), ("she'll", "'ll", 0.07),
    ("it'll", "'ll", 0.05),
    # 've
    ("i've", "'ve", 0.50), ("you've", "'ve", 0.18), ("we've", "'ve", 0.20),
    ("they've", "'ve", 0.12),
    # 'd
    ("i'd", "'d", 0.35), ("you'd", "'d", 0.18), ("we'd", "'d", 0.12),
    ("they'd", "'d", 0.12), ("he'd", "'d", 0.13), ("she'd", "'d", 0.10),
    # 'm
    ("i'm", "'m", 1.00),
    # can't comes from the "can" fragment, which also contains the modal "can".
    ("can't", "can", 0.35),
    # smaller apostrophe forms recovered from their own tokenizer fragments
    ("o'clock", "'clock", 0.95), ("ma'am", "'am", 0.90),
    ("y'all", "'all", 0.90),
    # won't is the dominant reading of the "won" fragment in subtitles;
    # the real verb "won" is kept below with a small share of that count.
    ("won't", "won", 0.85),
]

# OpenSubtitles annotation tokens (stage directions), not real dictionary words.
SUBTITLE_NOISE = {
    "sighs", "sigh", "laughs", "chuckles", "chuckling", "grunts", "grunting",
    "groans", "groaning", "gasps", "gasping", "mumbling", "muttering",
    "narrator", "narrating", "inaudible", "indistinct", "applause", "scoffs",
    "snorts", "sniffling", "whimpers", "whimpering", "sobs", "sobbing",
    "panting", "chattering", "clattering", "crackling", "squeaking",
    "squealing", "buzzing", "beeping", "hissing", "howling", "snarling",
    "whooping", "stammering", "clears",
}

# Tokenizer / subtitle artifacts and common merged-token noise.
SUBTITLE_NOISE |= {
    "ofthe", "inthe", "tothe", "onthe", "atthe", "forthe", "withthe",
    "andthe", "isthe", "youre", "em", "ya",
    # apostrophe-less contraction spellings (the contraction form is added below)
    "aint", "dont", "cant", "wont", "didnt", "isnt", "arent", "wasnt",
    "couldnt", "wouldnt", "shouldnt", "havent", "doesnt",
    # stutter / duplicated-token artifacts
    "y-you", "yeah-yeah",
}

# Obvious apostrophe-dropped -ing spellings that are not worth suggesting.
ING_DROPPED = {
    "goin", "sayin", "thinkin", "somethin", "nothin", "comin", "gettin",
    "givin", "takin", "makin", "lookin", "talkin", "waitin", "tryin", "doin",
    "bein", "havin", "feelin", "tellin", "callin", "askin", "playin",
    "workin", "runnin", "walkin", "eatin", "drinkin", "sleepin", "watchin",
    "livin", "lovin", "sittin", "standin", "holdin", "comin",
}
SUBTITLE_NOISE |= ING_DROPPED

# Approx. 60 hardcore pornographic / discriminatory terms removed for product
# decency.  Ordinary words such as "sex", "fuck", "shit", "ass" are kept.
BLOCKLIST = {
    "nigger", "niggers", "nigga", "niggas", "faggot", "faggots", "fag", "fags",
    "cunt", "cunts", "pussy", "pussies", "dick", "dicks", "cock", "cocks",
    "porn", "porno", "pornography", "blowjob", "blowjobs", "anal", "orgasm",
    "orgasms", "masturbate", "masturbation", "cum", "semen", "whore", "whores",
    "slut", "sluts", "twat", "twats", "penis", "vagina", "vaginal", "tranny",
    "trannies", "chink", "chinks", "spic", "spics", "kike", "kikes",
    "wetback", "wetbacks", "retard", "retards", "rape", "raped", "raping",
    "rapist", "rapists", "incest", "pedophile", "pedophiles", "bdsm", "milf",
    "shemale", "hentai", "lolita", "xxx", "sexvilla", "hooker", "hookers",
    # KKK / white-supremacist token fragments
    "klux", "kkk",
}

# Extra adult-ish words suppressed from *phrases and bigrams* only.  The words
# themselves may stay in words.tsv (they are normal English words).
ADULT_EXTRA = {
    "sex", "sexy", "sexual", "sexually", "nude", "naked", "erotic", "fetish",
    "condom", "condoms", "horny", "kinky", "escort", "boobs", "tits", "pimp",
    "stripper",
}
PHRASE_BLOCK = BLOCKLIST | ADULT_EXTRA

# Function words: pairs where both sides are function words are not useful
# Function words are used to measure how "content-like" a phrase is; phrases
# made only of function words ("in that", "and that") are not kept.
FUNCTION_WORDS = {
    "a", "an", "the", "and", "or", "but", "if", "of", "to", "in", "on", "at",
    "by", "for", "with", "from", "as", "is", "are", "was", "were", "be",
    "been", "being", "am", "do", "does", "did", "have", "has", "had", "will",
    "would", "can", "could", "shall", "should", "may", "might", "must", "i",
    "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them",
    "my", "your", "his", "its", "our", "their", "this", "that", "these",
    "those", "there", "here", "not", "no", "so", "than", "then", "too",
    "very", "just", "only", "also", "up", "down", "out", "off", "over",
    "under", "again", "more", "most", "some", "any", "all", "each", "every",
    "both", "few", "many", "much", "such", "own", "same", "other", "another",
    "what", "which", "who", "whom", "whose", "when", "where", "why", "how",
}

# Placeholder words in learner phrase lists ("do one's best", "someone's
# parade") make the phrase impossible to type as-is, so they are dropped.
PLACEHOLDER_WORDS = {
    "one's", "oneself", "someone", "somebody", "something", "somewhere",
    "anyone", "anything", "anybody", "everyone", "everything", "nobody",
    "nothing", "else's", "someone's", "somebody's", "people's",
}

# Collocation gate for the learner phrase lists (see build_phrases).
# A 2-word phrase needs a strong direct collocation; longer phrases need every
# adjacent pair attested and at least one strong (head) collocation.
SECOND_LA_MIN_PAIR_COUNT = 1000
SECOND_LA_TWO_PMI = 4.0
SECOND_LA_MIN_PMI = 2.0
SECOND_LA_HEAD_PMI = 3.5
SECOND_LA_MIN_CONTENT = 0.4

# Source weights used when ordering the merged phrase set.
# Curated phrases always outrank machine-extracted ones; the numeric part is
# a commonness signal derived from the words.tsv frequencies.
CURATED_BOOST = 3.0
IDIOM_BOOST = 0.45
SECOND_LA_BOOST = 0.25


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
def is_valid_word(word: str) -> bool:
    """True if *word* can be written to words.tsv."""
    if not WORD_RE.match(word):
        return False
    if word.endswith("-"):
        return False
    if "--" in word or "''" in word:
        return False
    return True


def is_valid_token(token: str) -> bool:
    """Token check used inside phrases/bigrams (allows a / i / word's)."""
    if token in SHORT_TOKENS:
        return True
    if is_valid_word(token):
        return True
    if token.endswith("'s") and is_valid_word(token[:-2]):
        return True
    return False


def normalize_phrase(text: str):
    """Lowercase / collapse whitespace and return a clean 2-5 word phrase or None."""
    s = text.strip().lower().replace("\u2019", "'").replace("\u2018", "'")
    s = re.sub(r"\s+", " ", s)
    if not s or not PHRASE_RE.match(s):
        return None
    tokens = s.split(" ")
    if not 2 <= len(tokens) <= 5:
        return None
    if not all(is_valid_token(t) for t in tokens):
        return None
    return " ".join(tokens)


def rank_scores(n: int) -> list[int]:
    """Rank-linear scores: first item SCORE_MAX, last item 1 (strictly non-increasing)."""
    if n <= 0:
        return []
    if n == 1:
        return [SCORE_MAX]
    return [1 + round((SCORE_MAX - 1) * (n - 1 - i) / (n - 1)) for i in range(n)]


def read_lines(path: str) -> list[str]:
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read().splitlines()


def die(message: str) -> None:
    print("error: " + message, file=sys.stderr)
    raise SystemExit(2)


# ---------------------------------------------------------------------------
# Raw loaders
# ---------------------------------------------------------------------------
def load_en_50k(raw_dir: str):
    """Return (all_counts, valid_words) from the OpenSubtitles frequency list.

    all_counts keeps tokenizer fragments such as "'s" / "don" because the
    contraction estimates need them.
    """
    path = os.path.join(raw_dir, "en_50k_frequencywords.txt")
    if not os.path.exists(path):
        die("missing %s (run tools/fetch_raw.py)" % path)
    all_counts: dict[str, int] = {}
    order: list[str] = []
    for line in read_lines(path):
        parts = line.rsplit(" ", 1)
        if len(parts) != 2 or not parts[1].isdigit():
            continue  # web_fetch spill header lines
        token, count = parts[0], int(parts[1])
        if token not in all_counts:
            order.append(token)
        all_counts[token] = count
    valid = [w for w in order if is_valid_word(w)]
    return all_counts, valid


def load_google_ranks(raw_dir: str) -> dict[str, int]:
    """1-based rank order of the Google 10k frequency list (validation only)."""
    path = os.path.join(raw_dir, "google-10000-english.txt")
    if not os.path.exists(path):
        die("missing %s (run tools/fetch_raw.py)" % path)
    ranks: dict[str, int] = {}
    rank = 0
    for line in read_lines(path):
        word = line.strip().lower()
        if not WORD_RE.match(word):
            continue
        rank += 1
        ranks.setdefault(word, rank)
    return ranks


def load_norvig(raw_dir: str):
    """Return (unigram_counts, bigram_counts) from the Google Web 1T extracts."""
    path1 = os.path.join(raw_dir, "norvig_count_1w.txt")
    path2 = os.path.join(raw_dir, "norvig_count_2w.txt")
    if not os.path.exists(path1) or not os.path.exists(path2):
        die("missing norvig_count_1w.txt / norvig_count_2w.txt (run tools/fetch_raw.py)")
    one: dict[str, int] = {}
    for line in read_lines(path1):
        parts = line.split("\t")
        if len(parts) == 2 and parts[1].isdigit():
            one[parts[0]] = int(parts[1])
    two: dict[tuple[str, str], int] = {}
    for line in read_lines(path2):
        parts = line.split("\t")
        if len(parts) != 2 or not parts[1].isdigit():
            continue
        pair = parts[0].split(" ")
        if len(pair) != 2:
            continue
        two[(pair[0], pair[1])] = int(parts[1])
    return one, two


def load_curated_phrases(raw_dir: str) -> list[str]:
    path = os.path.join(raw_dir, "phrases_curated.txt")
    if not os.path.exists(path):
        return []
    return [line for line in read_lines(path) if line.strip() and not line.lstrip().startswith("#")]


def load_2ndla_phrases(raw_dir: str) -> list[str]:
    phrases: list[str] = []
    for name in sorted(os.listdir(raw_dir)):
        if not (name.startswith("phrases_2ndla_") and name.endswith(".txt")):
            continue
        phrases.extend(line for line in read_lines(os.path.join(raw_dir, name)) if line.strip())
    return phrases


def load_idioms(raw_dir: str) -> list[str]:
    """Extract idioms from '{idiom}>>{meaning}' CSV rows."""
    path = os.path.join(raw_dir, "idioms_baiango.csv")
    if not os.path.exists(path):
        return []
    idioms: list[str] = []
    with open(path, encoding="utf-8", errors="replace") as fh:
        for row in csv.reader(fh):
            if not row:
                continue
            text = row[0].strip()
            if text.startswith('"') and text.endswith('"'):
                text = text[1:-1]
            if text.startswith("{") and "}>>{" in text:
                idioms.append(text[1:text.index("}>>{")])
    return idioms


# ---------------------------------------------------------------------------
# words.tsv
# ---------------------------------------------------------------------------
def build_words(raw_dir: str, max_words: int, report: dict):
    all_counts, valid = load_en_50k(raw_dir)
    google_rank = load_google_ranks(raw_dir)

    dropped_noise = [w for w in valid if w in SUBTITLE_NOISE or w in BLOCKLIST]
    dropped_fragment = [w for w in valid if w in CONTRACTION_FRAGMENTS]
    keep = [w for w in valid
            if w not in SUBTITLE_NOISE
            and w not in BLOCKLIST
            and w not in CONTRACTION_FRAGMENTS]

    counts: dict[str, int] = {w: all_counts[w] for w in keep}

    # Recover verb "won" from its fragment share (see CONTRACTIONS comment).
    if "won" in counts:
        counts["won"] = max(1, int(round(all_counts.get("won", 0) * 0.12)))

    # Add contractions whose subtitle fragment is frequent enough.
    added_contractions = []
    for word, fragment, share in CONTRACTIONS:
        if word in counts or not is_valid_word(word):
            continue
        fragment_count = all_counts.get(fragment, 0)
        if fragment_count <= 0:
            continue
        count = int(round(fragment_count * share))
        if count < 500:
            continue
        counts[word] = count
        added_contractions.append(word)

    provisional = sorted(
        counts.items(),
        key=lambda kv: (-kv[1], google_rank.get(kv[0], 10 ** 9), kv[0]),
    )
    provisional = provisional[:max_words]
    words = [w for w, _ in provisional]

    # Cross-validation with the Google 10k list (diagnostic only).
    top1000 = [w for w, _ in provisional[:1000]]
    report["our_top1000_in_google10k"] = sum(1 for w in top1000 if w in google_rank)
    report["google_top1000_in_ours"] = sum(
        1 for w, r in google_rank.items() if r <= 1000 and w in counts)
    report["google10k_words_present"] = sum(1 for w in google_rank if w in counts)
    report["google10k_total"] = len(google_rank)

    scores = rank_scores(len(words))
    report["words"] = len(words)
    report["dropped_noise"] = dropped_noise
    report["dropped_fragments"] = dropped_fragment
    report["contractions_added"] = added_contractions
    report["max_word_count"] = provisional[0][1] if provisional else 0
    return list(zip(words, scores)), counts


# ---------------------------------------------------------------------------
# phrases.tsv
# ---------------------------------------------------------------------------
def build_phrases(raw_dir, max_phrases, counts, report):
    max_count = max(counts.values()) if counts else 1
    log_max = math.log1p(max_count)
    vocab = set(counts)
    vocab |= SHORT_TOKENS

    def token_count(token: str) -> int:
        if token in SHORT_TOKENS:
            return max_count
        if token in counts:
            return counts[token]
        if token.endswith("'s") and token[:-2] in counts:
            return counts[token[:-2]]
        return 0

    def token_ok(token: str) -> bool:
        if token in SHORT_TOKENS:
            return True
        if token in vocab:
            return True
        if token.endswith("'s") and token[:-2] in vocab:
            return True
        return False

    def phrase_commonness(tokens) -> float:
        total = sum(math.log1p(token_count(t)) / log_max for t in tokens)
        return total / len(tokens)

    candidates: dict[str, dict] = {}

    def add(phrase: str, *, curated=False, curated_idx=None, idiom=False,
            second_la=False):
        tokens = phrase.split(" ")
        if any(t in PHRASE_BLOCK for t in tokens):
            return
        entry = candidates.get(phrase)
        if entry is None:
            entry = {"phrase": phrase, "curated": False, "idiom": False,
                     "second_la": False, "curated_idx": None}
            candidates[phrase] = entry
        if curated:
            entry["curated"] = True
            entry["curated_idx"] = curated_idx
        entry["idiom"] = entry["idiom"] or idiom
        entry["second_la"] = entry["second_la"] or second_la

    # -- 1. curated phrases (highest priority, file order preserved) ----------
    curated = load_curated_phrases(raw_dir)
    curated_valid, curated_rejected = [], []
    for idx, line in enumerate(curated):
        phrase = normalize_phrase(line)
        if phrase is None or any(t not in vocab and not
                                 (t.endswith("'s") and t[:-2] in vocab) for t in phrase.split(" ")):
            curated_rejected.append(line.strip())
            continue
        curated_valid.append(phrase)
    for idx, phrase in enumerate(dict.fromkeys(curated_valid)):
        add(phrase, curated=True, curated_idx=idx)

    # -- 2. idioms -----------------------------------------------------------
    idioms = load_idioms(raw_dir)
    idiom_valid = []
    for line in idioms:
        phrase = normalize_phrase(line)
        if not phrase:
            continue
        tokens = phrase.split(" ")
        if any(t in PLACEHOLDER_WORDS for t in tokens):
            continue
        if not all(token_ok(t) for t in tokens):
            continue
        idiom_valid.append(phrase)
    for phrase in dict.fromkeys(idiom_valid):
        add(phrase, idiom=True)

    # -- 3. English collocation lists (learned-vocabulary phrase lists) ------
    # A phrase is kept only when the Google Web corpus attests every adjacent
    # word pair and the collocation is strong enough (PMI), plus a content-word
    # shape check.  This removes grammar fragments ("in that", "and that") and
    # list typos while keeping real collocations ("account for", "in charge of").
    one, two = load_norvig(raw_dir)
    total_tokens = sum(one.values()) or 1

    def pmi(a: str, b: str) -> float:
        bc = two.get((a, b), 0)
        if bc <= 0 or a not in one or b not in one or one[a] <= 0 or one[b] <= 0:
            return -99.0
        return math.log2((bc * total_tokens) / (one[a] * one[b]))

    def collocation_ok(tokens) -> bool:
        pairs = list(zip(tokens, tokens[1:]))
        if not pairs:
            return False
        for a, b in pairs:
            if two.get((a, b), 0) < SECOND_LA_MIN_PAIR_COUNT:
                return False
        values = [pmi(a, b) for a, b in pairs]
        if len(tokens) == 2:
            return values[0] >= SECOND_LA_TWO_PMI
        return (min(values) >= SECOND_LA_MIN_PMI
                and max(values) >= SECOND_LA_HEAD_PMI)

    def content_fraction(tokens) -> float:
        content = sum(1 for t in tokens
                      if t not in FUNCTION_WORDS and t not in SHORT_TOKENS)
        return content / len(tokens)

    second_la = []
    for line in load_2ndla_phrases(raw_dir):
        phrase = normalize_phrase(line)
        if not phrase:
            continue
        tokens = phrase.split(" ")
        if any(t in PLACEHOLDER_WORDS for t in tokens):
            continue
        if not all(token_ok(t) for t in tokens):
            continue
        if content_fraction(tokens) < SECOND_LA_MIN_CONTENT:
            continue
        if not collocation_ok(tokens):
            continue
        if phrase in candidates:
            continue
        second_la.append(phrase)
    second_la = list(dict.fromkeys(second_la))
    second_la.sort(key=lambda p: (-phrase_commonness(p.split(" ")), p))
    second_la = second_la[:max_phrases]
    for phrase in second_la:
        add(phrase, second_la=True)

    # -- metrics / final ordering -------------------------------------------
    rows = []
    for phrase, entry in candidates.items():
        tokens = phrase.split(" ")
        base = phrase_commonness(tokens)
        metric = 0.1 * base
        if entry["curated"]:
            total = max(len(curated_valid), 1)
            metric = (CURATED_BOOST
                      + 0.5 * (1.0 - min(entry["curated_idx"], total - 1) / total)
                      + 0.005 * base)
        if entry["idiom"]:
            metric = max(metric, 0.1 * base + IDIOM_BOOST)
        if entry["second_la"]:
            metric = max(metric, 0.1 * base + SECOND_LA_BOOST)
        rows.append((metric, phrase))
    rows.sort(key=lambda x: (-x[0], x[1]))
    rows = rows[:max_phrases]
    phrases = [p for _, p in rows]
    scores = rank_scores(len(phrases))

    report["phrases"] = len(phrases)
    report["curated_kept"] = len(curated_valid)
    report["curated_rejected"] = curated_rejected
    report["idioms_kept"] = len(idiom_valid)
    report["second_la_kept"] = len(second_la)
    report["phrase_sources"] = {
        "curated": sum(1 for e in candidates.values() if e["curated"]),
        "idioms": sum(1 for e in candidates.values() if e["idiom"]),
        "second_la": sum(1 for e in candidates.values() if e["second_la"]),
    }
    return list(zip(phrases, scores))


# ---------------------------------------------------------------------------
# word_bigrams.tsv
# ---------------------------------------------------------------------------
def build_bigrams(raw_dir, max_bigrams, counts, report):
    one, two = load_norvig(raw_dir)
    vocab = set(counts) | SHORT_TOKENS
    rows = []
    for (a, b), bc in two.items():
        if bc < 2000:
            continue
        if not is_valid_token(a) or not is_valid_token(b):
            continue
        if a not in vocab or b not in vocab:
            continue
        if a in PHRASE_BLOCK or b in PHRASE_BLOCK:
            continue
        rows.append((bc, a, b))
    rows.sort(key=lambda x: (-x[0], x[1], x[2]))
    rows = rows[:max_bigrams]
    pairs = [(a, b) for _, a, b in rows]
    scores = rank_scores(len(pairs))
    report["bigrams"] = len(pairs)
    report["bigrams_max_count"] = rows[0][0] if rows else 0
    return [(a, b, s) for (a, b), s in zip(pairs, scores)]


# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------
def write_tsv(path: str, rows) -> None:
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        for row in rows:
            fh.write("\t".join(str(x) for x in row))
            fh.write("\n")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--raw-dir", default=DEFAULT_RAW)
    ap.add_argument("--out-dir", default=DEFAULT_OUT)
    ap.add_argument("--max-words", type=int, default=50000)
    ap.add_argument("--max-phrases", type=int, default=8000)
    ap.add_argument("--max-bigrams", type=int, default=50000)
    ap.add_argument("--skip-bigrams", action="store_true", help="do not write word_bigrams.tsv")
    args = ap.parse_args()

    report: dict = {}
    words, counts = build_words(args.raw_dir, args.max_words, report)
    phrases = build_phrases(args.raw_dir, args.max_phrases, counts, report)
    bigrams = [] if args.skip_bigrams else build_bigrams(
        args.raw_dir, args.max_bigrams, counts, report)

    write_tsv(os.path.join(args.out_dir, "words.tsv"), words)
    write_tsv(os.path.join(args.out_dir, "phrases.tsv"), phrases)
    if bigrams:
        write_tsv(os.path.join(args.out_dir, "word_bigrams.tsv"), bigrams)
    else:
        report.setdefault("bigrams", 0)
        report.setdefault("bigrams_max_count", 0)

    def pct(a, b):
        return "%.1f%%" % (100.0 * a / b) if b else "n/a"

    print("== words ==")
    print("  accepted           : %d" % report["words"])
    print("  top count          : %d" % report["max_word_count"])
    print("  noise removed      : %d  %s" % (len(report["dropped_noise"]),
                                             " ".join(sorted(report["dropped_noise"])[:12])))
    print("  contraction pieces : %d  %s" % (len(report["dropped_fragments"]),
                                             " ".join(sorted(report["dropped_fragments"]))))
    print("  contractions added : %d  %s" % (len(report["contractions_added"]),
                                             " ".join(report["contractions_added"])))
    for word, score in words[:10]:
        print("    %-12s %d" % (word, score))
    print("== google-10000 cross-check ==")
    print("  our top1000 present in google10k : %d/1000" % report["our_top1000_in_google10k"])
    print("  google top1000 present in ours   : %d/1000" % report["google_top1000_in_ours"])
    print("  google 10k covered               : %d/%d (%s)" % (
        report["google10k_words_present"], report["google10k_total"],
        pct(report["google10k_words_present"], report["google10k_total"])))
    print("== phrases ==")
    print("  accepted           : %d" % report["phrases"])
    print("  curated kept       : %d" % report["curated_kept"])
    print("  sources            : %s" % report["phrase_sources"])
    for phrase, score in phrases[:12]:
        print("    %-28s %d" % (phrase, score))
    if report["curated_rejected"]:
        print("  curated rejected   : %d" % len(report["curated_rejected"]))
        for line in report["curated_rejected"][:25]:
            print("    ! %s" % line)
    print("== word_bigrams ==")
    print("  accepted           : %d (max count %d)" % (
        report["bigrams"], report["bigrams_max_count"]))
    print("== wrote ==")
    for name in ("words.tsv", "phrases.tsv", "word_bigrams.tsv"):
        path = os.path.join(args.out_dir, name)
        if os.path.exists(path):
            print("  %-20s %8d bytes" % (name, os.path.getsize(path)))
    print("done")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
