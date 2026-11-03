# english-cand / data/raw — 原始数据清单与来源说明

本目录保存词典构建所需的原始纯文本文件。`tools/fetch_raw.py` 可重新下载这些文件，
`tools/build_lexicon.py` 从本目录确定性地生成 `data/words.tsv`、`data/phrases.tsv`、
`data/word_bigrams.tsv`（生成结果均为 UTF-8 纯文本、LF 换行、无 BOM、无压缩）。

抓取时间：2026-11-03。所有文件均为 UTF-8 文本（`idioms_baiango.csv` 为 UTF-8 CSV）。

## 文件清单

| 文件 | 内容 / 用途 | 规模 | 来源 URL | 许可证 |
| --- | --- | --- | --- | --- |
| `en_50k_frequencywords.txt` | 英文 5 万词 + OpenSubtitles 词频，**words.tsv 的主词表** | 50,000 行 / 622,749 B | https://cdn.jsdelivr.net/gh/hermitdave/FrequencyWords@master/content/2018/en/en_50k.txt （上游仓库 https://github.com/hermitdave/FrequencyWords；语料为 OpenSubtitles2018, OPUS） | 仓库脚本 MIT；词表由 OpenSubtitles 语料派生，上游语料使用 CC BY-SA 4.0 |
| `google-10000-english.txt` | Google 1 万高频词，按频率排序，**仅用于排序交叉校验**（不作为补充词源） | 10,000 词 / 76,085 B | https://cdn.jsdelivr.net/gh/first20hours/google-10000-english@master/google-10000-english.txt | 仓库未附 LICENSE 文件；公开的 Google Web Trillion Word Corpus 派生词表，仅作校验输入 |
| `norvig_count_1w.txt` | Google Web 1T 1-gram 词频（333,333 词），用于 2-gram 的 PMI 计算 | 333,333 行 / 4,956,241 B | https://www.norvig.com/ngrams/count_1w.txt | 数据由 Google Web Trillion Word Corpus（LDC 发行）派生；Norvig 页面标注代码 MIT，数据仅作本地派生使用 |
| `norvig_count_2w.txt` | Google Web 1T 2-gram 词频（286,358 对），**word_bigrams.tsv 的来源** | 286,358 行 / 5,566,017 B | https://www.norvig.com/ngrams/count_2w.txt | 同上 |
| `phrases_2ndla_*.txt` | 10 个英语词组/搭配/习语表（CET4/6、TEM4/8、TOEFL、IELTS、GRE、NPEE、Junior、Senior），每行一条英文短语，**phrases.tsv 的搭配来源** | 合计 17,421 行（清洗后约 6,558 条有效候选） | https://cdn.jsdelivr.net/gh/2ndLA/english-phrases@main/lists/<name>.txt （https://github.com/2ndLA/english-phrases） | **CC BY-SA 4.0**（仓库 LICENSE 为 Attribution-ShareAlike 4.0 International） |
| `idioms_baiango.csv` | 1024 条英语习语，格式 `"{idiom}>>{meaning}"`，**phrases.tsv 的习语来源** | 1,024 行 / 49,818 B | https://cdn.jsdelivr.net/gh/baiango/english_idioms@main/idioms.csv （https://github.com/baiango/english_idioms） | The Unlicense（public domain，见仓库 LICENSE.md） |
| `phrases_curated.txt` | 人工整理的高频日常短语（问候、礼貌用语、问句、时间、搭配、短语动词），本仓库自写 | 703 行（含注释），698 条入选 | 无外部 URL（本项目原创） | 本仓库原创，随项目分发 |

> 说明：`raw.githubusercontent.com` 在本机沙箱中会被 TLS/连接重置，上面统一使用
> `cdn.jsdelivr.net` 的 GitHub 镜像；`www.norvig.com` 可直连。

## 抓取方式

1. 首选脚本（可重复执行，已存在的合格文件会跳过）：

   ```bash
   python tools/fetch_raw.py            # 全部下载/校验
   python tools/fetch_raw.py --force    # 强制重新下载
   python tools/fetch_raw.py --only norvig_count_2w.txt,en_50k_frequencywords.txt
   ```

   脚本使用捆绑 CPython 的 `urllib`。每个文件都有 URL 镜像列表和最小体积校验；
   下载后按 UTF-8 解码。本机 `curl` / PowerShell `Invoke-WebRequest` 访问 GitHub
   会被沙箱重置，而 Python `urllib` + jsDelivr 镜像可用。

2. 探索阶段也使用过 Harness 的 `web_fetch` 工具：工具输出超过约 100 KB 会给出
   “Full formatted result stored at: <临时 spill 文件>”，可用 `Copy-Item` 把该
   spill 文件复制进本目录（`en_50k_frequencywords.txt` 的第一次尝试即来自该方式，
   因 100 KB 截断只得到 7,867 行，随后被完整文件覆盖）。**该临时文件仅作兜底，
   最终入库文件均为完整文件。**

## 处理 / 过滤规则（实现见 tools/build_lexicon.py）

- **words.tsv**：以 `en_50k_frequencywords.txt` 为主。只保留长度 2–30、字符集
  `[a-z'-]`、不以 `-`/`'` 开头或结尾、无 `--`/`''` 的 token；去除 OpenSubtitles
  舞台提示噪声（sighs/laughs/applause/narrator/inaudible… 约 40 个）、分词残片
  （don/doesn/didn/isn/aren/wasn/weren/couldn/wouldn/shouldn/haven/hasn/hadn/
  mustn/needn/ain）、无撇号拼写（dont/didnt…）和 stutter 残片（y-you…）；
  由残片词频重建 54 个常用缩写（don't/it's/i'm/can't/won't/o'clock/ma'am/y'all…）。
  过滤约 60 个极端色情/歧视词（保持工具体面；sex/fuck/shit 等普通词保留）。
  `google-10000-english.txt` 仅用于交叉校验与频次并列时的 tie-break。
  排序按 OpenSubtitles 频次降序；score 由排名线性映射到 1..1,000,000（越大越常见）。
- **phrases.tsv**：2–5 个英文词，小写，字符集同上；三个来源合并后按“来源权重 +
  词频常见度”排序：人工整理短语（最高） > 习语 > 语料校验过的搭配。搭配表只保留
  相邻词对在 Google Web 2-gram 中有足够词频（≥1000）且搭配强度 PMI 达标
  （两词短语 PMI≥4.0；更长短语每个相邻对 PMI≥2.0 且至少一个≥3.5）的条目，并剔除
  含 one's/someone 等占位词、纯虚词片段（"in that"、"and that"）和成人词的行。
- **word_bigrams.tsv**：Google Web 1T 2-gram 中两侧词都在最终词表（单字母 a/i 也允许）
  且词频 ≥2000 的组合，按词频降序取前 50,000 条；剔除成人词对，不含 `<S>` 句首标记。
  用于“打完一个词后预测下一个词”。

## 复现

```bash
python tools/fetch_raw.py                 # 下载/校验原始文件
python tools/build_lexicon.py             # 生成 data/*.tsv（确定性、幂等）
python tools/check_lexicon.py             # 结构校验 + top20/随机20 抽样
```

同一份 raw 数据重复构建会得到逐字节相同的 tsv；构建脚本只写 `data/` 下三个输出文件，
不修改本目录的原始文件。
