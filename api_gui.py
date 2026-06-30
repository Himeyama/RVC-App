"""WinUI3 GUI 用の FastAPI サーバ。

gui_v1.py の処理ロジックを流用し、HTTP/WebSocket で外部 GUI から制御できるようにする。

エンドポイント:
  GET  /hostapis                  - ホスト API 一覧
  GET  /devices?hostapi=<name>    - 指定ホスト API の入出力デバイス一覧
  GET  /config                    - 現在の設定を取得
  POST /config                    - 設定を保存
  POST /start                     - 音声変換開始
  POST /stop                      - 音声変換停止
  GET  /status                    - 実行状態を取得
  WS   /metrics                   - 推論時間などをリアルタイム配信
"""

import asyncio
import copy
import json
import logging
import os
import re
import shutil
import sys
import time
from multiprocessing import Process, Queue, cpu_count, freeze_support
from typing import Optional

import numpy as np
import sounddevice as sd
import uvicorn
from uvicorn.config import LOGGING_CONFIG
from dotenv import load_dotenv
from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel

logger = logging.getLogger("api_gui")

CONFIG_PATH = "configs/inuse/config.json"
DEFAULT_CONFIG_PATH = "configs/config.json"

# ── プロセスプール (Harvest) ──────────────────────────────────────────


class Harvest(Process):
    def __init__(self, inp_q, opt_q):
        super().__init__()
        self.inp_q = inp_q
        self.opt_q = opt_q

    def run(self):
        import numpy as np
        import pyworld

        while True:
            idx, x, res_f0, n_cpu, ts = self.inp_q.get()
            f0, _ = pyworld.harvest(
                x.astype(np.double),
                fs=16000,
                f0_ceil=1100,
                f0_floor=50,
                frame_period=10,
            )
            res_f0[idx] = f0
            if len(res_f0.keys()) >= n_cpu:
                self.opt_q.put(ts)


# ── データモデル ─────────────────────────────────────────────────


class ConfigPayload(BaseModel):
    pth_path: str = ""
    index_path: str = ""
    sg_hostapi: str = ""
    sg_wasapi_exclusive: bool = False
    sg_input_device: str = ""
    sg_output_device: str = ""
    sr_type: str = "sr_model"
    threhold: int = -60
    pitch: int = 0
    formant: float = 0.0
    index_rate: float = 0.0
    rms_mix_rate: float = 0.0
    block_time: float = 0.25
    crossfade_length: float = 0.05
    extra_time: float = 2.5
    n_cpu: int = 4
    f0method: str = "fcpe"
    use_pv: bool = False
    I_noise_reduce: bool = False
    O_noise_reduce: bool = False


class StatusResponse(BaseModel):
    running: bool
    samplerate: int = 0
    delay_ms: int = 0


# ── オーディオサーバ本体 ────────────────────────────────────────


class AudioServer:
    def __init__(self):
        self.config = None
        self.gui_config = ConfigPayload()
        self.flag_vc = False
        self.function = "vc"
        self.delay_time = 0.0
        self.rvc = None
        self.stream = None
        self.inp_q: Optional[Queue] = None
        self.opt_q: Optional[Queue] = None
        self.n_cpu_workers = min(cpu_count(), 8)
        self.metrics_queue: asyncio.Queue = asyncio.Queue(maxsize=64)
        self.loop: Optional[asyncio.AbstractEventLoop] = None
        self.samplerate = 0

    def init_workers(self):
        self.inp_q = Queue()
        self.opt_q = Queue()
        for _ in range(self.n_cpu_workers):
            p = Harvest(self.inp_q, self.opt_q)
            p.daemon = True
            p.start()

    # ── デバイス管理 ─────────────────────────────────────────

    def query_hostapis(self) -> list[str]:
        return [h["name"] for h in sd.query_hostapis()]

    def query_devices(self, hostapi_name: Optional[str] = None) -> dict:
        sd._terminate()
        sd._initialize()
        devices = sd.query_devices()
        hostapis = sd.query_hostapis()
        for hostapi in hostapis:
            for device_idx in hostapi["devices"]:
                devices[device_idx]["hostapi_name"] = hostapi["name"]
        names = [h["name"] for h in hostapis]
        if hostapi_name not in names:
            hostapi_name = names[0] if names else None
        inputs = [d["name"] for d in devices
                  if d["max_input_channels"] > 0 and d.get("hostapi_name") == hostapi_name]
        outputs = [d["name"] for d in devices
                   if d["max_output_channels"] > 0 and d.get("hostapi_name") == hostapi_name]
        in_indices = [d["index"] if "index" in d else d["name"]
                      for d in devices
                      if d["max_input_channels"] > 0 and d.get("hostapi_name") == hostapi_name]
        out_indices = [d["index"] if "index" in d else d["name"]
                       for d in devices
                       if d["max_output_channels"] > 0 and d.get("hostapi_name") == hostapi_name]
        return {
            "hostapi": hostapi_name,
            "inputs": inputs,
            "outputs": outputs,
            "input_indices": in_indices,
            "output_indices": out_indices,
        }

    def set_devices(self, input_device: str, output_device: str, hostapi_name: str):
        info = self.query_devices(hostapi_name)
        if input_device not in info["inputs"]:
            raise HTTPException(400, f"入力デバイスが見つかりません: {input_device}")
        if output_device not in info["outputs"]:
            raise HTTPException(400, f"出力デバイスが見つかりません: {output_device}")
        sd.default.device[0] = info["input_indices"][info["inputs"].index(input_device)]
        sd.default.device[1] = info["output_indices"][info["outputs"].index(output_device)]

    def get_device_samplerate(self) -> int:
        return int(sd.query_devices(device=sd.default.device[0])["default_samplerate"])

    def get_device_channels(self) -> int:
        max_in = sd.query_devices(device=sd.default.device[0])["max_input_channels"]
        max_out = sd.query_devices(device=sd.default.device[1])["max_output_channels"]
        return min(max_in, max_out, 2)

    # ── 設定 ─────────────────────────────────────────────────

    def load_config(self) -> dict:
        try:
            if not os.path.exists(CONFIG_PATH):
                os.makedirs(os.path.dirname(CONFIG_PATH), exist_ok=True)
                if os.path.exists(DEFAULT_CONFIG_PATH):
                    shutil.copy(DEFAULT_CONFIG_PATH, CONFIG_PATH)
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return ConfigPayload().dict()

    def save_config(self, payload: ConfigPayload):
        os.makedirs(os.path.dirname(CONFIG_PATH), exist_ok=True)
        with open(CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(payload.dict(), f, ensure_ascii=False, indent=2)

    def validate(self, payload: ConfigPayload):
        if not payload.pth_path.strip():
            raise HTTPException(400, "PTH ファイルを指定してください")
        non_ascii = re.compile(r"[^\x00-\x7F]+")
        if non_ascii.findall(payload.pth_path):
            raise HTTPException(400, "PTH ファイルのパスに非 ASCII 文字を含めないでください")
        if payload.index_path.strip() and non_ascii.findall(payload.index_path):
            raise HTTPException(400, "INDEX ファイルのパスに非 ASCII 文字を含めないでください")

    # ── 音声変換 ────────────────────────────────────────────

    def start_vc(self, payload: ConfigPayload):
        if self.flag_vc:
            raise HTTPException(400, "既に音声変換が実行中です")
        self.validate(payload)
        self.set_devices(payload.sg_input_device, payload.sg_output_device, payload.sg_hostapi)
        self.gui_config = payload
        self.config.use_jit = False

        import torch

        torch.cuda.empty_cache()
        self.rvc = rvc_for_realtime.RVC(
            payload.pitch,
            payload.formant,
            payload.pth_path,
            payload.index_path,
            payload.index_rate,
            payload.n_cpu,
            self.inp_q,
            self.opt_q,
            self.config,
            self.rvc if self.rvc else None,
        )
        self.samplerate = (
            self.rvc.tgt_sr if payload.sr_type == "sr_model" else self.get_device_samplerate()
        )
        self._setup_buffers()
        self._start_stream(payload)
        self.flag_vc = True
        self.delay_time = (
            (self.stream.latency[-1] if self.stream else 0)
            + payload.block_time
            + payload.crossfade_length
            + 0.01
        )
        if payload.I_noise_reduce:
            self.delay_time += min(payload.crossfade_length, 0.04)

    def _setup_buffers(self):
        import torch

        from tools.torchgate import TorchGate
        import torchaudio.transforms as tat

        cfg = self.gui_config
        sr = self.samplerate
        self.zc = sr // 100
        self.block_frame = int(np.round(cfg.block_time * sr / self.zc)) * self.zc
        self.block_frame_16k = 160 * self.block_frame // self.zc
        self.crossfade_frame = int(np.round(cfg.crossfade_length * sr / self.zc)) * self.zc
        self.sola_buffer_frame = min(self.crossfade_frame, 4 * self.zc)
        self.sola_search_frame = self.zc
        self.extra_frame = int(np.round(cfg.extra_time * sr / self.zc)) * self.zc
        device = self.config.device
        self.input_wav = torch.zeros(
            self.extra_frame + self.crossfade_frame + self.sola_search_frame + self.block_frame,
            device=device, dtype=torch.float32,
        )
        self.input_wav_denoise = self.input_wav.clone()
        self.input_wav_res = torch.zeros(
            160 * self.input_wav.shape[0] // self.zc, device=device, dtype=torch.float32,
        )
        self.rms_buffer = np.zeros(4 * self.zc, dtype="float32")
        self.sola_buffer = torch.zeros(self.sola_buffer_frame, device=device, dtype=torch.float32)
        self.nr_buffer = self.sola_buffer.clone()
        self.output_buffer = self.input_wav.clone()
        self.skip_head = self.extra_frame // self.zc
        self.return_length = (
            self.block_frame + self.sola_buffer_frame + self.sola_search_frame
        ) // self.zc
        self.fade_in_window = (
            torch.sin(
                0.5 * np.pi * torch.linspace(
                    0.0, 1.0, steps=self.sola_buffer_frame, device=device, dtype=torch.float32,
                )
            ) ** 2
        )
        self.fade_out_window = 1 - self.fade_in_window
        self.resampler = tat.Resample(orig_freq=sr, new_freq=16000, dtype=torch.float32).to(device)
        if self.rvc.tgt_sr != sr:
            self.resampler2 = tat.Resample(
                orig_freq=self.rvc.tgt_sr, new_freq=sr, dtype=torch.float32,
            ).to(device)
        else:
            self.resampler2 = None
        self.tg = TorchGate(sr=sr, n_fft=4 * self.zc, prop_decrease=0.9).to(device)

    def _start_stream(self, payload: ConfigPayload):
        channels = self.get_device_channels()
        extra = None
        if "WASAPI" in payload.sg_hostapi and payload.sg_wasapi_exclusive:
            extra = sd.WasapiSettings(exclusive=True)
        self.channels = channels
        self.stream = sd.Stream(
            callback=self._audio_callback,
            blocksize=self.block_frame,
            samplerate=self.samplerate,
            channels=channels,
            dtype="float32",
            extra_settings=extra,
        )
        self.stream.start()

    def stop_vc(self):
        if not self.flag_vc:
            raise HTTPException(400, "音声変換が実行されていません")
        self.flag_vc = False
        if self.stream is not None:
            self.stream.abort()
            self.stream.close()
            self.stream = None

    def _audio_callback(self, indata, outdata, frames, times, status):
        import librosa
        import torch
        import torch.nn.functional as F

        cfg = self.gui_config
        start = time.perf_counter()
        indata = librosa.to_mono(indata.T)
        if cfg.threhold > -60:
            indata = np.append(self.rms_buffer, indata)
            rms = librosa.feature.rms(y=indata, frame_length=4 * self.zc, hop_length=self.zc)[:, 2:]
            self.rms_buffer[:] = indata[-4 * self.zc:]
            indata = indata[2 * self.zc - self.zc // 2:]
            mask = librosa.amplitude_to_db(rms, ref=1.0)[0] < cfg.threhold
            for i in range(mask.shape[0]):
                if mask[i]:
                    indata[i * self.zc:(i + 1) * self.zc] = 0
            indata = indata[self.zc // 2:]
        self.input_wav[:-self.block_frame] = self.input_wav[self.block_frame:].clone()
        self.input_wav[-indata.shape[0]:] = torch.from_numpy(indata).to(self.config.device)
        self.input_wav_res[:-self.block_frame_16k] = self.input_wav_res[self.block_frame_16k:].clone()
        if cfg.I_noise_reduce:
            self.input_wav_denoise[:-self.block_frame] = self.input_wav_denoise[self.block_frame:].clone()
            buf = self.input_wav[-self.sola_buffer_frame - self.block_frame:]
            buf = self.tg(buf.unsqueeze(0), self.input_wav.unsqueeze(0)).squeeze(0)
            buf[:self.sola_buffer_frame] *= self.fade_in_window
            buf[:self.sola_buffer_frame] += self.nr_buffer * self.fade_out_window
            self.input_wav_denoise[-self.block_frame:] = buf[:self.block_frame]
            self.nr_buffer[:] = buf[self.block_frame:]
            self.input_wav_res[-self.block_frame_16k - 160:] = self.resampler(
                self.input_wav_denoise[-self.block_frame - 2 * self.zc:]
            )[160:]
        else:
            self.input_wav_res[-160 * (indata.shape[0] // self.zc + 1):] = self.resampler(
                self.input_wav[-indata.shape[0] - 2 * self.zc:]
            )[160:]
        if self.function == "vc":
            infer_wav = self.rvc.infer(
                self.input_wav_res, self.block_frame_16k,
                self.skip_head, self.return_length, cfg.f0method,
            )
            if self.resampler2 is not None:
                infer_wav = self.resampler2(infer_wav)
        elif cfg.I_noise_reduce:
            infer_wav = self.input_wav_denoise[self.extra_frame:].clone()
        else:
            infer_wav = self.input_wav[self.extra_frame:].clone()
        if cfg.O_noise_reduce and self.function == "vc":
            self.output_buffer[:-self.block_frame] = self.output_buffer[self.block_frame:].clone()
            self.output_buffer[-self.block_frame:] = infer_wav[-self.block_frame:]
            infer_wav = self.tg(infer_wav.unsqueeze(0), self.output_buffer.unsqueeze(0)).squeeze(0)
        if cfg.rms_mix_rate < 1 and self.function == "vc":
            src = self.input_wav_denoise[self.extra_frame:] if cfg.I_noise_reduce else self.input_wav[self.extra_frame:]
            rms1 = librosa.feature.rms(y=src[:infer_wav.shape[0]].cpu().numpy(), frame_length=4*self.zc, hop_length=self.zc)
            rms1 = F.interpolate(torch.from_numpy(rms1).to(self.config.device).unsqueeze(0),
                                  size=infer_wav.shape[0] + 1, mode="linear", align_corners=True)[0, 0, :-1]
            rms2 = librosa.feature.rms(y=infer_wav[:].cpu().numpy(), frame_length=4*self.zc, hop_length=self.zc)
            rms2 = F.interpolate(torch.from_numpy(rms2).to(self.config.device).unsqueeze(0),
                                  size=infer_wav.shape[0] + 1, mode="linear", align_corners=True)[0, 0, :-1]
            rms2 = torch.max(rms2, torch.zeros_like(rms2) + 1e-3)
            infer_wav *= torch.pow(rms1 / rms2, torch.tensor(1 - cfg.rms_mix_rate))
        conv_input = infer_wav[None, None, :self.sola_buffer_frame + self.sola_search_frame]
        cor_nom = F.conv1d(conv_input, self.sola_buffer[None, None, :])
        cor_den = torch.sqrt(
            F.conv1d(conv_input ** 2, torch.ones(1, 1, self.sola_buffer_frame, device=self.config.device)) + 1e-8
        )
        sola_offset = torch.argmax(cor_nom[0, 0] / cor_den[0, 0])
        infer_wav = infer_wav[sola_offset:]
        if "privateuseone" in str(self.config.device) or not cfg.use_pv:
            infer_wav[:self.sola_buffer_frame] *= self.fade_in_window
            infer_wav[:self.sola_buffer_frame] += self.sola_buffer * self.fade_out_window
        else:
            infer_wav[:self.sola_buffer_frame] = phase_vocoder(
                self.sola_buffer, infer_wav[:self.sola_buffer_frame],
                self.fade_out_window, self.fade_in_window,
            )
        self.sola_buffer[:] = infer_wav[self.block_frame:self.block_frame + self.sola_buffer_frame]
        outdata[:] = infer_wav[:self.block_frame].repeat(self.channels, 1).t().cpu().numpy()
        elapsed = (time.perf_counter() - start) * 1000
        # 推論時間を WebSocket 用キューに非ブロッキングで送る
        if self.loop is not None:
            try:
                self.loop.call_soon_threadsafe(self._publish_metric, int(elapsed))
            except RuntimeError:
                pass

    def _publish_metric(self, infer_ms: int):
        try:
            self.metrics_queue.put_nowait({"infer_ms": infer_ms, "ts": time.time()})
        except asyncio.QueueFull:
            pass


def phase_vocoder(a, b, fade_out, fade_in):
    import torch

    window = torch.sqrt(fade_out * fade_in)
    fa = torch.fft.rfft(a * window)
    fb = torch.fft.rfft(b * window)
    absab = torch.abs(fa) + torch.abs(fb)
    n = a.shape[0]
    if n % 2 == 0:
        absab[1:-1] *= 2
    else:
        absab[1:] *= 2
    phia = torch.angle(fa)
    phib = torch.angle(fb)
    deltaphase = phib - phia
    deltaphase = deltaphase - 2 * np.pi * torch.floor(deltaphase / 2 / np.pi + 0.5)
    w = 2 * np.pi * torch.arange(n // 2 + 1).to(a) + deltaphase
    t = torch.arange(n).unsqueeze(-1).to(a) / n
    return (
        a * (fade_out ** 2)
        + b * (fade_in ** 2)
        + torch.sum(absab * torch.cos(w * t + phia), -1) * window / n
    )


# ── FastAPI ──────────────────────────────────────────────────


server = AudioServer()
app = FastAPI(title="RVC Realtime GUI API")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.on_event("startup")
async def _on_startup():
    server.loop = asyncio.get_running_loop()


@app.get("/hostapis")
def get_hostapis():
    return server.query_hostapis()


@app.get("/devices")
def get_devices(hostapi: Optional[str] = None):
    info = server.query_devices(hostapi)
    return {"hostapi": info["hostapi"], "inputs": info["inputs"], "outputs": info["outputs"]}


@app.get("/config", response_model=ConfigPayload)
def get_config():
    data = server.load_config()
    try:
        return ConfigPayload(**data)
    except Exception:
        return ConfigPayload()


@app.post("/config")
def post_config(payload: ConfigPayload):
    server.save_config(payload)
    return {"message": "saved"}


@app.post("/start")
def post_start(payload: ConfigPayload):
    server.start_vc(payload)
    server.save_config(payload)
    return {
        "message": "started",
        "samplerate": server.samplerate,
        "delay_ms": int(round(server.delay_time * 1000)),
    }


@app.post("/stop")
def post_stop():
    server.stop_vc()
    return {"message": "stopped"}


@app.get("/status", response_model=StatusResponse)
def get_status():
    return StatusResponse(
        running=server.flag_vc,
        samplerate=server.samplerate,
        delay_ms=int(round(server.delay_time * 1000)),
    )


@app.websocket("/metrics")
async def metrics_ws(ws: WebSocket):
    await ws.accept()
    try:
        while True:
            data = await server.metrics_queue.get()
            await ws.send_json(data)
    except WebSocketDisconnect:
        pass


# ── エントリポイント ─────────────────────────────────────────

if __name__ == "__main__":
    if sys.platform == "win32":
        freeze_support()
    # 行バッファ化（ConPTY 越しに即時フラッシュされるように）
    try:
        sys.stdout.reconfigure(line_buffering=True, write_through=True)
        sys.stderr.reconfigure(line_buffering=True, write_through=True)
    except Exception:
        pass
    # 念のため root logger も stdout に直結（ConPTY 経由で確実に届くように）
    logging.basicConfig(
        level=logging.INFO,
        stream=sys.stdout,
        format="%(asctime)s | %(levelname)s | %(name)s | %(message)s",
        force=True,
    )
    load_dotenv()
    os.environ["OMP_NUM_THREADS"] = "4"
    if sys.platform == "darwin":
        os.environ["PYTORCH_ENABLE_MPS_FALLBACK"] = "1"

    sys.path.append(os.getcwd())
    from infer.lib import rtrvc as rvc_for_realtime
    from configs.config import Config

    # AudioServer.start_vc から見えるようにモジュールグローバルへ
    globals()["rvc_for_realtime"] = rvc_for_realtime

    server.config = Config()
    server.init_workers()

    # uvicorn ログ設定: default/access の両ハンドラを stdout に統一し、
    # ConPTY 経由で必ず GUI のターミナルに届くようにする
    log_cfg = copy.deepcopy(LOGGING_CONFIG)
    log_cfg["handlers"]["default"]["stream"] = "ext://sys.stdout"
    log_cfg["handlers"]["access"]["stream"] = "ext://sys.stdout"
    log_cfg["formatters"]["default"]["use_colors"] = True
    log_cfg["formatters"]["access"]["use_colors"] = True
    # api_gui ロガー / root も同じハンドラに乗せる
    log_cfg["loggers"][""] = {"handlers": ["default"], "level": "INFO"}
    log_cfg["loggers"]["api_gui"] = {"handlers": ["default"], "level": "INFO", "propagate": False}

    uvicorn.run(
        app,
        host="127.0.0.1",
        port=6242,
        log_level="info",
        access_log=True,
        use_colors=True,
        log_config=log_cfg,
    )
