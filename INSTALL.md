# インストール手順（Windows）

## 前提条件

- [uv](https://docs.astral.sh/uv/getting-started/installation/) インストール済み
- NVIDIA GPU + CUDA 12.x ドライバ（`nvidia-smi` で確認）
- ffmpeg が PATH に通っていること

## セットアップ

```powershell
uv sync --extra nvidia
```

Python 3.11 の仮想環境 (`.venv`) が作成され、全依存パッケージがインストールされます。

### GPU バリアント

| 環境 | コマンド |
|------|---------|
| NVIDIA CUDA（デフォルト） | `uv sync --extra nvidia` |
| Intel DirectML（AMD/Intel） | `uv sync --extra dml` |
| AMD ROCm | `uv sync --extra amd` |
| macOS | `uv sync --extra macos` |

### 事前学習モデルの取得

初回のみ実行：

```powershell
uv run python tools/download_models.py
```

## 起動

### Web UI（学習・推論・モデル管理）

```powershell
uv run python infer-web.py
```

ブラウザで http://localhost:7865 を開く。`--noautoopen` でブラウザ自動起動を抑制できる。

### リアルタイム音声変換 GUI

追加パッケージをインストールしてから起動：

```powershell
uv sync --extra nvidia --extra realtime
uv run python gui_v1.py
```

> `PySimpleGUI` 5.x はライセンス変更で有料化されたため、`FreeSimpleGUI` を使うこと：
> ```powershell
> uv run pip install FreeSimpleGUI
> ```

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

## トラブルシューティング

| エラー | 原因 | 対処 |
|--------|------|------|
| `No supported Nvidia GPU found` | GPU が認識されていない（CPU モードで動作） | `nvidia-smi` でドライバ確認 |
| `numpy.dtype size changed` | NumPy 2.x と pyworld の非互換 | `pyproject.toml` で `numpy<2.0` を維持 |
| `UnpicklingError: Weights only load failed` | PyTorch 2.6+ の `torch.load` デフォルト変更 | `configs/config.py` でモンキーパッチ済み。モデルファイルが壊れていないか確認 |
| `No module named 'FreeSimpleGUI'` | `gui_v1.py` は `FreeSimpleGUI` を import する | `uv run pip install FreeSimpleGUI` |

### CUDA が認識されない場合

```powershell
uv run python -c "import torch; print(torch.cuda.is_available(), torch.version.cuda)"
```

`False` が返る場合、torch が CUDA ビルドでインストールされていない。`uv sync --extra nvidia` を再実行する。
