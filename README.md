# RVC-App



VITS ベースの音声変換フレームワーク [RVC-Project/Retrieval-based-Voice-Conversion-WebUI](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI) のフォークです。Windows 環境での利用に特化し、依存管理の現代化と UI の刷新を進めています。ライセンスは上流と同じ MIT を維持しています。

## このフォークでの主な変更

- **パッケージ管理を [uv](https://docs.astral.sh/uv/) に移行**: `requirements*.txt` / Poetry / `environment_dml.yaml` を廃止し、`pyproject.toml` に一本化
- **Python 3.10 に固定**: `fairseq` / `pkg_resources` まわりの非互換を回避
- **リアルタイム VC GUI を customtkinter に刷新**: 有償化された PySimpleGUI から `customtkinter` へ置き換え、UI を完全日本語化
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

初回のみ：

```powershell
uv run python tools/download_models.py
```

`assets/hubert/hubert_base.pt`、`assets/pretrained` / `assets/pretrained_v2`、`assets/uvr5_weights` が配置されます。RMVPE を使う場合は [rmvpe.pt](https://huggingface.co/lj1995/VoiceConversionWebUI/blob/main/rmvpe.pt) をリポジトリルートに置きます（DirectML 環境では `rmvpe.onnx`）。

### ffmpeg

[ffmpeg.exe](https://huggingface.co/lj1995/VoiceConversionWebUI/blob/main/ffmpeg.exe) と [ffprobe.exe](https://huggingface.co/lj1995/VoiceConversionWebUI/blob/main/ffprobe.exe) をリポジトリルートに置くか、PATH に通します。

## 起動

### Web UI（学習・推論・モデル管理・UVR5）

```powershell
uv run python infer-web.py
```

または `go-web.bat`（DirectML 環境は `go-web-dml.bat`）をダブルクリック。ブラウザで http://localhost:7865 が開きます。

### リアルタイム音声変換 GUI（customtkinter 版）

```powershell
uv run python gui_v1.py
```

または `go-realtime-gui.bat`（DirectML 環境は `go-realtime-gui-dml.bat`）。

主な設定項目：

| 項目 | 内容 |
|------|------|
| モデルパス | `assets/weights/xxx.pth` |
| インデックスパス | `logs/xxx/added_xxx.index`（任意。音色漏れ防止） |
| 入出力デバイス | マイクとスピーカー（配信なら VB-CABLE 等） |
| 音高 (transpose) | 男声→女声なら +12 程度 |
| F0 推定方法 | `rmvpe` 推奨 |
| インデックスレート | 0〜1。高いほど学習音色に寄る |
| ブロック長 / クロスフェード / 追加時間 | レイテンシと品質のトレードオフ |

### WinUI 3 デスクトップアプリ（`RvcRealtimeGui/`）

1. FastAPI サーバを起動

   ```powershell
   uv run python api_gui.py
   ```

   `http://127.0.0.1:6242` で待ち受けます。

2. 別のターミナルから WinUI 3 アプリを起動

   ```powershell
   dotnet run --project RvcRealtimeGui
   ```

WinUI 3 アプリから `/hostapis`、`/devices`、`/config`、`/start`、`/stop`、`/status`、`/metrics`（WebSocket）を呼び出してリアルタイム VC を制御します。

## ディレクトリ構成

| パス | 役割 |
|------|------|
| `infer-web.py` | Gradio WebUI（学習・推論・モデル管理・UVR5 統合） |
| `gui_v1.py` | リアルタイム VC GUI（customtkinter 版、日本語） |
| `api_gui.py` | WinUI 3 連携用 FastAPI サーバ |
| `RvcRealtimeGui/` | WinUI 3 デスクトップアプリ（C# / XAML） |
| `infer/` | 推論・学習パイプライン本体 |
| `tools/` | CLI 推論、モデルダウンロード、FAISS インデックス構築 |
| `configs/` | モデル設定（v1: 256dim, v2: 768dim） |

詳細なアーキテクチャは `CLAUDE.md` を参照。

## CLI 推論

```powershell
uv run python tools/infer_cli.py `
  --input_path input.wav `
  --model_name model.pth `
  --index_path feature.index `
  --opt_path output.wav `
  --f0method rmvpe `
  --f0up_key 0
```

## トラブルシューティング

| 症状 | 対処 |
|------|------|
| `No supported Nvidia GPU found` | `nvidia-smi` でドライバ確認。CPU フォールバックは動作する |
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
- [Gradio](https://github.com/gradio-app/gradio)
- [FFmpeg](https://github.com/FFmpeg/FFmpeg)
- [Ultimate Vocal Remover](https://github.com/Anjok07/ultimatevocalremovergui)
- [audio-slicer](https://github.com/openvpi/audio-slicer)
- [RMVPE](https://github.com/Dream-High/RMVPE)（事前学習モデルは [yxlllc](https://github.com/yxlllc/RMVPE) と [RVC-Boss](https://github.com/RVC-Boss) によるもの）
