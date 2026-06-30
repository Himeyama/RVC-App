# RVC-App

VITS ベースの音声変換フレームワーク [RVC-Project/Retrieval-based-Voice-Conversion-WebUI](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI) のフォークです。Windows 環境でのリアルタイム VC に特化し、WinUI 3 ネイティブ GUI と FastAPI バックエンドを追加しています。ライセンスは上流と同じ MIT を維持しています。

## このフォークでの主な変更

- **パッケージ管理を [uv](https://docs.astral.sh/uv/) に移行**: `requirements*.txt` / Poetry を廃止し、`pyproject.toml` に一本化
- **Python 3.10 に固定**: `fairseq` / `pkg_resources` まわりの非互換を回避
- **WinUI 3 デスクトップアプリ `RvcRealtimeGui/` を追加**: ネイティブ Windows UI からリアルタイム VC を制御
- **FastAPI サーバ `api_gui.py` を追加**: WinUI 3 アプリとの連携用 HTTP/WebSocket バックエンド

## 動作環境

- Windows 10 / 11
- [uv](https://docs.astral.sh/uv/getting-started/installation/) インストール済み
- NVIDIA GPU + CUDA 12.x ドライバ（`nvidia-smi` で確認）
- `ffmpeg` / `ffprobe` が PATH に通っていること
- WinUI 3 アプリをビルドする場合は .NET SDK（`RvcRealtimeGui/RvcRealtimeGui.csproj` の `TargetFramework` を参照）

## セットアップ

```powershell
uv sync --extra nvidia
```

Python 3.10 の仮想環境 (`.venv`) が作成され、依存パッケージがインストールされます。

### GPU バリアント

| 環境 | コマンド |
|------|---------|
| NVIDIA CUDA（デフォルト） | `uv sync --extra nvidia` |
| Intel DirectML（AMD / Intel iGPU） | `uv sync --extra dml` |

### 事前学習モデルの取得

以下のファイルを手動で配置してください。

| ファイル | 配置先 | 入手先 |
|--------|-------|-------|
| `hubert_base.pt` | `assets/hubert/` | [lj1995/VoiceConversionWebUI](https://huggingface.co/lj1995/VoiceConversionWebUI) |
| `rmvpe.pt` | `assets/rmvpe/` | [rmvpe.pt](https://huggingface.co/lj1995/VoiceConversionWebUI/blob/main/rmvpe.pt) |
| pretrained モデル | `assets/pretrained/`, `assets/pretrained_v2/` | 同上 |
| 変換対象の `.pth` | `assets/weights/` | 自分で学習したもの |
| インデックス `.index` | 任意の場所 | 自分で構築したもの（任意） |

### ffmpeg

[ffmpeg.exe](https://ffmpeg.org/download.html) と `ffprobe.exe` を PATH に通すか、リポジトリルートに置きます。

## 起動

### WinUI 3 デスクトップアプリ

アプリ内の「サーバー起動」ボタンを押すと `api_gui.py` が自動起動します。手動で起動する場合：

```powershell
# FastAPI サーバ（http://127.0.0.1:6242）
uv run python api_gui.py

# WinUI 3 アプリ
dotnet run --project RvcRealtimeGui
```

WinUI 3 アプリは `/hostapis`、`/devices`、`/config`、`/start`、`/stop`、`/status`、`/metrics`（WebSocket）を通じてリアルタイム VC を制御します。

### 操作手順

1. **モデル (.pth)** と **インデックス (.index)** のパスを指定
2. **オーディオデバイス**（入力マイク・出力先）を選択
3. **サーバー起動** ボタンで `api_gui.py` を起動
4. **音声変換 開始** ボタンで変換を開始

主な設定項目：

| 項目 | 内容 |
|------|------|
| 音高 (transpose) | 半音単位。男声→女声なら +12 程度 |
| F0 推定方法 | `rmvpe` 推奨 |
| インデックス強度 | 0〜1。高いほど学習音色に寄る |
| バッファ長 / クロスフェード / 追加時間 | レイテンシと品質のトレードオフ |

## ディレクトリ構成

| パス | 役割 |
|------|------|
| `api_gui.py` | WinUI 3 連携用 FastAPI サーバ |
| `RvcRealtimeGui/` | WinUI 3 デスクトップアプリ（C# / XAML） |
| `infer/lib/rtrvc.py` | リアルタイム VC コア |
| `infer/lib/rmvpe.py` | RMVPE F0 推定 |
| `infer/lib/jit/` | HuBERT / Synthesizer JIT ローダ |
| `infer/lib/infer_pack/` | VITS Synthesizer 本体 |
| `tools/torchgate/` | ノイズゲート |
| `configs/` | モデル設定 JSON（v1: 256dim, v2: 768dim） |

## トラブルシューティング

| 症状 | 対処 |
|------|------|
| `No supported Nvidia GPU found` | `nvidia-smi` でドライバ確認 |
| `numpy.dtype size changed` | NumPy 2.x と pyworld の非互換。`pyproject.toml` で `numpy<2.0` を維持 |
| `UnpicklingError: Weights only load failed` | PyTorch 2.6+ の `torch.load` デフォルト変更。`configs/config.py` でモンキーパッチ済み |
| CUDA が認識されない | `uv run python -c "import torch; print(torch.cuda.is_available())"` で確認後、`uv sync --extra nvidia` を再実行 |

## ライセンス

MIT License。詳細は [LICENSE](./LICENSE) を参照。

このリポジトリは [RVC-Project/Retrieval-based-Voice-Conversion-WebUI](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI) のフォークです。

## 参考プロジェクト（上流由来）

- [Retrieval-based-Voice-Conversion-WebUI（上流）](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI)
- [ContentVec](https://github.com/auspicious3000/contentvec/)
- [VITS](https://github.com/jaywalnut310/vits)
- [HIFIGAN](https://github.com/jik876/hifi-gan)
- [FFmpeg](https://github.com/FFmpeg/FFmpeg)
- [RMVPE](https://github.com/Dream-High/RMVPE)（事前学習モデルは [yxlllc](https://github.com/yxlllc/RMVPE) と [RVC-Boss](https://github.com/RVC-Boss) によるもの）
