# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## プロジェクト概要

Retrieval-based Voice Conversion WebUI (RVC) — VITS アーキテクチャをベースにした音声変換フレームワーク。
HuBERT による特徴抽出、FAISS によるリトリーバル、RMVPE によるF0推定を組み合わせ、少量の学習データでも高品質な声質変換を実現する。

## 起動・実行コマンド

```bash
# FastAPI サーバー（WinUI3 アプリ連携用）
uv run python api_gui.py

# WinUI 3 デスクトップアプリ
dotnet run --project RvcRealtimeGui

# CLI 推論
python tools/infer_cli.py \
  --input_path input.wav \
  --model_name model.pth \
  --index_path feature.index \
  --opt_path output.wav \
  --f0method rmvpe \
  --f0up_key 0
```

学習パイプライン（前処理・特徴抽出・学習・インデックス作成）と UVR5 ボーカル分離・モデルマージ/情報表示/変更/抽出は、Gradio WebUI ではなく `api_gui.py` の FastAPI エンドポイント経由で WinUI3 アプリ（`RvcRealtimeGui/Pages/ModelTuningPage`）から操作する。

## 依存関係のインストール

```bash
uv sync --extra nvidia   # NVIDIA GPU
uv sync --extra dml      # Intel DirectML（AMD / Intel iGPU）
```

ffmpeg と ffprobe が PATH に必要。初回は `tools/download_models.py` で pretrained モデルを取得する。

## コードフォーマット

CI で自動チェックされる（`pull_format.yml` / `push_format.yml`）。フォーマッターのコマンドは `.github/workflows/` を参照。

## テスト

自動テストスイートなし。CI の `unitest.yml` が存在するが実コードは未収録。動作確認は WebUI か `tools/infer_cli.py` で手動実施。

## アーキテクチャ

### 推論パイプライン

```
音声入力
  └→ infer/modules/vc/modules.py (VC クラス)
       ├→ HuBERT で音声特徴を抽出
       ├→ infer/modules/vc/pipeline.py
       │    ├→ F0 推定 (PM / harvest / crepe / rmvpe)
       │    ├→ infer/lib/rmvpe.py  ← RMVPE ラッパー
       │    ├→ FAISS リトリーバルで学習セット特徴に置換（音色漏洩防止）
       │    └→ infer/lib/infer_pack/models.py (VITS Synthesizer v1/v2)
       └→ RMS マッチング → 音声出力
```

### 学習パイプライン

```
学習音声
  └→ infer/lib/train/data_utils.py  (DataLoader・バケットサンプラー)
       └→ infer/lib/train/train.py  (DDP メインループ)
            ├→ Generator / Discriminator 損失 (infer/lib/train/losses.py)
            ├→ Mel スペクトログラム (infer/lib/train/mel_processing.py)
            └→ チェックポイント保存 (infer/lib/train/process_ckpt.py)
```

### 主要モジュール

| パス | 役割 |
|------|------|
| `configs/config.py` | シングルトン Config — デバイス設定・引数解析・モデル設定読み込み |
| `configs/v1/*.json`, `configs/v2/*.json` | モデルハイパーパラメータ（v1=256dim, v2=768dim） |
| `infer/lib/infer_pack/models.py` | VITS Synthesizer 本体（v1/v2、F0あり/なし） |
| `infer/modules/uvr5/` | UVR5 ボーカル分離（MDXNet / AudioPre） |
| `infer/lib/rtrvc.py` | リアルタイム VC |
| `infer/lib/audio.py` | 音声 I/O・リサンプリング |
| `infer/lib/slicer2.py` | 推論用音声スライシング |
| `tools/train-index.py` / `tools/train-index-v2.py` | FAISS インデックス構築 |
| `tools/export_onnx.py` | ONNX エクスポート |
| `i18n/i18n.py` | 国際化サポート |

### モデルバージョン

- **v1**: HuBERT 256次元特徴、`configs/v1/` のコンフィグ
- **v2**: HuBERT 768次元特徴、`configs/v2/` のコンフィグ（32kHz/40kHz/48kHz）

### エントリーポイント

- `api_gui.py` — WinUI3 アプリ連携用 FastAPI サーバー（リアルタイム推論・学習パイプライン・UVR5・モデル管理）
- `RvcRealtimeGui/` — WinUI 3 デスクトップアプリ（C# / XAML）
