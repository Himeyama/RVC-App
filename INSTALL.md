# インストール手順（Windows / Python 3.9）

このプロジェクトは **Python 3.9** が必要です。Python 3.10 以降では numba・pyworld 等でバイナリ非互換が発生します。

## 前提条件

- Python 3.9（`py -3.9` で起動できること）
- NVIDIA GPU + CUDA 12.x ドライバ（`nvidia-smi` で確認）
- CUDA Toolkit 12.x インストール済み（`nvcc --version` で確認）
- Git
- ffmpeg が PATH に通っていること

## セットアップ手順

### 1. 仮想環境を Python 3.9 で作成

```powershell
py -3.9 -m venv venv
```

### 2. fairseq 以外の依存をインストール

公式の `requirements.txt` は古い fairseq バージョンが pip 24+ と非互換のため、`requirements-py311.txt` から fairseq を除いてインストールします。

```powershell
# fairseq を除いた一時ファイルを作成
(Get-Content requirements-py311.txt) | Where-Object { $_ -notmatch 'fairseq' } | Set-Content requirements-nofairseq.txt

# インストール
.\venv\Scripts\pip.exe install -r requirements-nofairseq.txt
```

### 3. fairseq を C 拡張なしでインストール

fairseq の公式 PyPI 版・One-sixth fork ともに CUDA + MSVC が必要なため、C/CUDA 拡張を除去したローカルビルドを行います。

```powershell
# ソースを取得
git clone --depth 1 https://github.com/One-sixth/fairseq.git _fairseq_build
```

`_fairseq_build\setup.py` を以下の内容に書き換えます（C 拡張定義をすべて除去）：

```python
#!/usr/bin/env python3
import os
import subprocess
import sys

from setuptools import find_packages, setup

if sys.version_info < (3, 6):
    sys.exit("Sorry, Python >= 3.6 is required for fairseq.")


def write_version_py():
    with open(os.path.join("fairseq", "version.txt")) as f:
        version = f.read().strip()
    with open(os.path.join("fairseq", "version.py"), "w") as f:
        f.write('__version__ = "{}"\n'.format(version))
    return version


version = write_version_py()

with open("README.md") as f:
    readme = f.read()

extra_packages = []
if os.path.exists(os.path.join("fairseq", "model_parallel", "megatron", "mpu")):
    extra_packages.append("fairseq.model_parallel.megatron.mpu")


def get_files(path, relative_to="fairseq"):
    all_files = []
    for root, _dirs, files in os.walk(path, followlinks=True):
        root = os.path.relpath(root, relative_to)
        for file in files:
            if file.endswith(".pyc"):
                continue
            all_files.append(os.path.join(root, file))
    return all_files


def do_setup(package_data):
    setup(
        name="fairseq",
        version=version,
        description="Facebook AI Research Sequence-to-Sequence Toolkit",
        url="https://github.com/pytorch/fairseq",
        long_description=readme,
        long_description_content_type="text/markdown",
        install_requires=[
            "cffi",
            "cython",
            "numpy>=1.21.3",
            "regex",
            "torch>=1.13",
            "tqdm",
            "bitarray",
            "torchaudio>=0.8.0",
            "scikit-learn",
            "packaging",
        ],
        packages=find_packages(
            exclude=[
                "examples",
                "examples.*",
                "scripts",
                "scripts.*",
                "tests",
                "tests.*",
            ]
        ) + extra_packages,
        package_data=package_data,
        ext_modules=[],
        test_suite="tests",
        entry_points={
            "console_scripts": [
                "fairseq-eval-lm = fairseq_cli.eval_lm:cli_main",
                "fairseq-generate = fairseq_cli.generate:cli_main",
                "fairseq-hydra-train = fairseq_cli.hydra_train:cli_main",
                "fairseq-interactive = fairseq_cli.interactive:cli_main",
                "fairseq-preprocess = fairseq_cli.preprocess:cli_main",
                "fairseq-score = fairseq_cli.score:cli_main",
                "fairseq-train = fairseq_cli.train:cli_main",
                "fairseq-validate = fairseq_cli.validate:cli_main",
            ],
        },
        zip_safe=False,
    )


if __name__ == "__main__":
    try:
        fairseq_examples = os.path.join("fairseq", "examples")
        if "build_ext" not in sys.argv[1:] and not os.path.exists(fairseq_examples):
            os.symlink(os.path.join("..", "examples"), fairseq_examples)

        package_data = {
            "fairseq": (
                get_files(fairseq_examples)
                + get_files(os.path.join("fairseq", "config"))
            )
        }
        do_setup(package_data)
    finally:
        if "build_ext" not in sys.argv[1:] and os.path.islink(fairseq_examples):
            os.unlink(fairseq_examples)
```

```powershell
# パッチ済み fairseq をインストール
.\venv\Scripts\pip.exe install .\_fairseq_build --no-build-isolation
```

### 4. fairseq の追加依存をインストール

```powershell
.\venv\Scripts\pip.exe install omegaconf hydra-core sacrebleu
```

### 5. NumPy を 1.x にダウングレード

pyworld が NumPy 2.x とバイナリ非互換のため：

```powershell
.\venv\Scripts\pip.exe install "numpy<2.0"
```

## 起動

### Web UI（学習・推論・モデル管理）

```powershell
.\venv\Scripts\python.exe infer-web.py
```

ブラウザで http://localhost:7865 を開く。

`--noautoopen` を付けるとブラウザが自動で開かない：

```powershell
.\venv\Scripts\python.exe infer-web.py --noautoopen
```

### リアルタイム音声変換 GUI

`gui_v1.py` を使う。事前に GUI 用の追加パッケージが必要：

```powershell
.\venv\Scripts\pip.exe install FreeSimpleGUI "sounddevice<0.5.0"
```

`requirements-win-for-realtime_vc_gui.txt` には `PySimpleGUI` と書かれているが、PySimpleGUI 5.x はライセンス変更で有料化されたため、現行コード (`gui_v1.py`) は fork の `FreeSimpleGUI` を import する。上記コマンドのとおり `FreeSimpleGUI` を入れること。

起動：

```powershell
.\venv\Scripts\python.exe gui_v1.py
```

GUI の設定項目：

| 項目 | 内容 |
|------|------|
| モデルパス | `assets/weights/xxx.pth` |
| インデックスパス | `logs/xxx/added_xxx.index`（任意。音色漏れ防止） |
| 入力／出力デバイス | マイクとスピーカー（配信なら VB-CABLE 等） |
| 音高 (transpose) | 男声→女声なら +12 程度 |
| F0 推定方法 | `rmvpe` 推奨 |
| インデックスレート | 0〜1。高いほど学習音色に寄る |
| ブロック長 / クロスフェード / 追加時間 | レイテンシと品質のトレードオフ |

`assets/weights/` に `.pth` モデルが必要。なければ Web UI 側で学習するか、公開モデルを配置する。

また、初回は事前学習モデル（HuBERT / RMVPE 等）を取得しておく必要がある。これらがないと「変換開始」を押した瞬間に `Model file not found: assets/hubert/hubert_base.pt` で落ちる：

```powershell
.\venv\Scripts\python.exe tools\download_models.py
```

## トラブルシューティング

| エラー | 原因 | 対処 |
|--------|------|------|
| `numba` ビルド失敗 | Python 3.11+ を使用 | `py -3.9 -m venv venv` で作り直す |
| `CUDA was not found` | MSVC/CUDA が分離ビルド環境に見えない | 上記の `setup.py` パッチ手順を実施 |
| `numpy.dtype size changed` | NumPy 2.x と pyworld の非互換 | `pip install "numpy<2.0"` |
| `No module named 'omegaconf'` | fairseq の依存が不足 | `pip install omegaconf hydra-core sacrebleu` |
| `No supported Nvidia GPU found` | GPU が認識されていない（CPU モードで動作） | nvidia-smi でドライバ確認 |
| `UnpicklingError: Weights only load failed ... fairseq.data.dictionary.Dictionary` | PyTorch 2.6+ で `torch.load` の `weights_only` デフォルトが True に変わり、fairseq の HuBERT チェックポイントが読めない | `configs/config.py` の冒頭で `torch.load` を `weights_only=False` 既定にモンキーパッチ済み |
| `No module named 'FreeSimpleGUI'` | `gui_v1.py` は `FreeSimpleGUI` を import する | `pip install FreeSimpleGUI "sounddevice<0.5.0"` |

## GPU が認識されない場合

`No supported Nvidia GPU found` と出て CPU モードになる場合、torch が CUDA 版かどうかを確認：

```powershell
.\venv\Scripts\python.exe -c "import torch; print(torch.cuda.is_available(), torch.version.cuda)"
```

CUDA 版でない場合は PyTorch 公式サイトから CUDA 対応版を再インストール。
