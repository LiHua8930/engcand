# 词典数据说明

本目录在构建时存放**运行时词典**（不纳入 Git，见根目录 `.gitignore`）：

| 文件 | 内容 |
| --- | --- |
| `words.tsv` | `word<TAB>score`，约 4.8 万英文单词，按词频打分（越大越常见） |
| `phrases.tsv` | `phrase<TAB>score`，约 1.9 千条常用短语（2~5 词） |
| `word_bigrams.tsv` | `word<TAB>nextword<TAB>score`，约 5 万词对（下一词联想） |

## 生成方式

```bash
python tools/fetch_raw.py        # 下载第三方语料到 data/raw/
python tools/build_lexicon.py    # 生成上面三个 tsv
python tools/check_lexicon.py --strict
```

`tools/build_lexicon.py` 支持参数（如 `--max-words` / `--max-phrases` / `--max-bigrams` / `--skip-bigrams`），构建是确定性的，重复运行结果一致。

`data/raw/` 中的原始语料体积较大，且各自有独立许可证，详见 [raw/README.md](raw/README.md)。**由它们生成的词典 tsv 也遵循上游许可证（CC BY-SA 4.0 等），不随本仓库的 MIT 许可证授权。**

## 程序如何使用

`EngCand.exe` 启动时从**可执行文件同目录的 `data\`** 加载这三个文件（`build.ps1` 会自动拷贝）。没有词典时程序会使用内置的少量兜底词，`--selftest` 会提示词典缺失。
