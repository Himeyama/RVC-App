# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## プロジェクト概要

Retrieval-based Voice Conversion WebUI (RVC) — VITS アーキテクチャをベースにした音声変換フレームワーク。
HuBERT による特徴抽出、FAISS によるリトリーバル、RMVPE によるF0推定を組み合わせ、少量の学習データでも高品質な声質変換を実現する。

## 起動・実行コマンド

```bash
# Web UI 起動（メインインターフェース）
python infer-web.py
# オプション: --port 7865 --noautoopen --colab --dml

# CLI 推論
python tools/infer_cli.py \
  --input_path input.wav \
  --model_name model.pth \
  --index_path feature.index \
  --opt_path output.wav \
  --f0method rmvpe \
  --f0up_key 0

# FastAPI サーバー（最新版）
python api_240604.py

# 簡易 Gradio アプリ
python tools/app.py

# Windows バッチ
go-web.bat               # WebUI
go-realtime-gui.bat      # リアルタイムVC GUI
go-web-dml.bat           # DirectML（AMD/Intel）

# macOS / Linux
sh ./run.sh              # venv作成・モデルDL・起動を自動化
```

## 依存関係のインストール

```bash
# NVIDIA GPU
pip install -r requirements.txt

# AMD ROCm
pip install -r requirements-amd.txt

# Intel DirectML (Windows)
pip install -r requirements-dml.txt

# Intel IPEX
pip install -r requirements-ipex.txt

# Poetry
poetry install
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

- `infer-web.py` — Gradio WebUI（学習・推論・モデル管理・UVR5 を統合）
- `api_240604.py` — FastAPI リアルタイム推論サーバー
- `gui_v1.py` — Phase Vocoder 対応の代替 GUI
