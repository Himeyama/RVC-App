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
import subprocess
import sys
import threading
import time
import uuid
from dataclasses import dataclass, field
from enum import Enum
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

from i18n.i18n import I18nAuto

i18n = I18nAuto()

logger = logging.getLogger("api_gui")

CONFIG_PATH = "configs/inuse/config.json"
DEFAULT_CONFIG_PATH = "configs/config.json"
NOW_DIR = os.getcwd()

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


class PreprocessRequest(BaseModel):
    trainset_dir: str
    exp_dir: str
    sr: str = "40k"
    n_p: int = 4
    noparallel: bool = False
    preprocess_per: float = 3.0


class ExtractF0FeatureRequest(BaseModel):
    exp_dir: str
    gpus: str = "0"
    n_p: int = 4
    f0method: str = "rmvpe_gpu"
    if_f0: bool = True
    version: str = "v2"
    gpus_rmvpe: str = "0-0"


class TrainRequest(BaseModel):
    exp_dir: str
    sr: str = "40k"
    if_f0: bool = True
    spk_id: int = 0
    save_epoch: int = 5
    total_epoch: int = 20
    batch_size: int = 4
    if_save_latest: bool = False
    pretrained_g: str = ""
    pretrained_d: str = ""
    gpus: str = "0"
    if_cache_gpu: bool = False
    if_save_every_weights: bool = False
    version: str = "v2"


class TrainIndexRequest(BaseModel):
    exp_dir: str
    version: str = "v2"


class Train1KeyRequest(BaseModel):
    exp_dir: str
    trainset_dir: str
    sr: str = "40k"
    if_f0: bool = True
    spk_id: int = 0
    n_p: int = 4
    f0method: str = "rmvpe_gpu"
    save_epoch: int = 5
    total_epoch: int = 20
    batch_size: int = 4
    if_save_latest: bool = False
    pretrained_g: str = ""
    pretrained_d: str = ""
    gpus: str = "0"
    if_cache_gpu: bool = False
    if_save_every_weights: bool = False
    version: str = "v2"
    gpus_rmvpe: str = "0-0"


class UvrSeparateRequest(BaseModel):
    model_name: str
    inp_root: str
    save_root_vocal: str = "opt"
    save_root_ins: str = "opt"
    agg: int = 10
    format0: str = "flac"


class ModelMergeRequest(BaseModel):
    path1: str
    path2: str
    alpha1: float = 0.5
    sr: str = "40k"
    f0: bool = True
    info: str = ""
    name: str
    version: str = "v2"


class ModelInfoRequest(BaseModel):
    path: str


class ModelChangeInfoRequest(BaseModel):
    path: str
    info: str
    name: str = ""


class ModelExtractRequest(BaseModel):
    path: str
    name: str
    sr: str = "40k"
    if_f0: bool = True
    info: str = ""
    version: str = "v2"


class JobStatusResponse(BaseModel):
    job_id: str
    kind: str
    status: str
    log_delta: str = ""
    error: Optional[str] = None


class JobStatus(str, Enum):
    running = "running"
    succeeded = "succeeded"
    failed = "failed"


@dataclass
class TrainingJob:
    job_id: str
    kind: str
    status: JobStatus = JobStatus.running
    log_path: Optional[str] = None
    log_offset: int = 0
    memory_log: list = field(default_factory=list)
    memory_log_offset: int = 0
    error: Optional[str] = None


class TrainingJobManager:
    """学習系サブプロセスのジョブ管理。AudioServerとは独立した責務。"""

    def __init__(self):
        self._jobs: dict[str, TrainingJob] = {}
        self._lock = threading.Lock()

    def get_job(self, job_id: str) -> TrainingJob:
        with self._lock:
            job = self._jobs.get(job_id)
        if job is None:
            raise HTTPException(404, f"ジョブが見つかりません: {job_id}")
        return job

    def _new_job(self, kind: str) -> TrainingJob:
        job = TrainingJob(job_id=uuid.uuid4().hex, kind=kind)
        with self._lock:
            self._jobs[job.job_id] = job
        return job

    def read_log_delta(self, job: TrainingJob) -> str:
        if job.log_path is not None and os.path.exists(job.log_path):
            with open(job.log_path, "r", encoding="utf-8", errors="ignore") as f:
                f.seek(job.log_offset)
                delta = f.read()
                job.log_offset = f.tell()
            return delta
        if job.memory_log:
            delta = "\n".join(job.memory_log[job.memory_log_offset :])
            job.memory_log_offset = len(job.memory_log)
            return delta
        return ""

    def _run_popen_job(self, job: TrainingJob, cmds: list[str]):
        try:
            for cmd in cmds:
                logger.info("Execute: %s", cmd)
                p = subprocess.Popen(cmd, shell=True, cwd=NOW_DIR)
                p.wait()
                if p.returncode != 0:
                    job.status = JobStatus.failed
                    job.error = f"コマンドが失敗しました (exit={p.returncode}): {cmd}"
                    return
            job.status = JobStatus.succeeded
        except Exception as e:
            job.status = JobStatus.failed
            job.error = str(e)

    def start_preprocess(self, req: PreprocessRequest) -> str:
        from infer.modules.train import training_jobs

        cmd, log_path = training_jobs.build_preprocess_cmd(
            python_cmd=_config().python_cmd,
            now_dir=NOW_DIR,
            trainset_dir=req.trainset_dir,
            exp_dir=req.exp_dir,
            sr=req.sr,
            n_p=req.n_p,
            noparallel=req.noparallel,
            preprocess_per=req.preprocess_per,
        )
        job = self._new_job("preprocess")
        job.log_path = log_path
        threading.Thread(
            target=self._run_popen_job, args=(job, [cmd]), daemon=True
        ).start()
        return job.job_id

    def start_extract_f0_feature(self, req: ExtractF0FeatureRequest) -> str:
        from infer.modules.train import training_jobs

        cfg = _config()
        cmds, log_path = training_jobs.build_extract_f0_feature_cmds(
            python_cmd=cfg.python_cmd,
            now_dir=NOW_DIR,
            gpus=req.gpus,
            n_p=req.n_p,
            f0method=req.f0method,
            if_f0=req.if_f0,
            exp_dir=req.exp_dir,
            version=req.version,
            gpus_rmvpe=req.gpus_rmvpe,
            device=cfg.device,
            is_half=cfg.is_half,
        )
        job = self._new_job("extract_f0_feature")
        job.log_path = log_path
        threading.Thread(
            target=self._run_popen_job, args=(job, cmds), daemon=True
        ).start()
        return job.job_id

    def start_train(self, req: TrainRequest) -> str:
        from infer.modules.train import training_jobs

        training_jobs.write_filelist(
            now_dir=NOW_DIR,
            exp_dir1=req.exp_dir,
            if_f0=req.if_f0,
            spk_id=req.spk_id,
            version=req.version,
            sr2=req.sr,
        )
        cmd = training_jobs.build_train_cmd(
            python_cmd=_config().python_cmd,
            exp_dir1=req.exp_dir,
            sr2=req.sr,
            if_f0=req.if_f0,
            save_epoch=req.save_epoch,
            total_epoch=req.total_epoch,
            batch_size=req.batch_size,
            if_save_latest=req.if_save_latest,
            pretrained_g=req.pretrained_g,
            pretrained_d=req.pretrained_d,
            gpus=req.gpus,
            if_cache_gpu=req.if_cache_gpu,
            if_save_every_weights=req.if_save_every_weights,
            version=req.version,
        )
        job = self._new_job("train")
        job.log_path = "%s/logs/%s/train.log" % (NOW_DIR, req.exp_dir)
        threading.Thread(
            target=self._run_popen_job, args=(job, [cmd]), daemon=True
        ).start()
        return job.job_id

    def _run_train_index_job(self, job: TrainingJob, exp_dir: str, version: str):
        from infer.modules.train import training_jobs

        try:
            for message in training_jobs.run_train_index(
                now_dir=NOW_DIR,
                exp_dir1=exp_dir,
                version=version,
                outside_index_root=os.getenv("outside_index_root", "assets/indices"),
                n_cpu=_config().n_cpu,
            ):
                job.memory_log.append(message)
            job.status = JobStatus.succeeded
        except Exception as e:
            job.status = JobStatus.failed
            job.error = str(e)

    def start_train_index(self, req: TrainIndexRequest) -> str:
        job = self._new_job("train_index")
        threading.Thread(
            target=self._run_train_index_job,
            args=(job, req.exp_dir, req.version),
            daemon=True,
        ).start()
        return job.job_id

    def _run_train1key_job(self, job: TrainingJob, req: Train1KeyRequest):
        from infer.modules.train import training_jobs

        def append(label: str, text: str):
            for line in text.splitlines() or [""]:
                job.memory_log.append(f"[{label}] {line}")

        try:
            append("前処理", "開始")
            preprocess_job = self._new_job("preprocess")
            cmd, log_path = training_jobs.build_preprocess_cmd(
                python_cmd=_config().python_cmd,
                now_dir=NOW_DIR,
                trainset_dir=req.trainset_dir,
                exp_dir=req.exp_dir,
                sr=req.sr,
                n_p=req.n_p,
                noparallel=False,
                preprocess_per=3.0,
            )
            self._run_popen_job(preprocess_job, [cmd])
            append("前処理", "完了" if preprocess_job.status == JobStatus.succeeded else f"失敗: {preprocess_job.error}")
            if preprocess_job.status != JobStatus.succeeded:
                job.status = JobStatus.failed
                job.error = preprocess_job.error
                return

            append("特徴抽出", "開始")
            cfg = _config()
            extract_job = self._new_job("extract_f0_feature")
            cmds, _log_path = training_jobs.build_extract_f0_feature_cmds(
                python_cmd=cfg.python_cmd,
                now_dir=NOW_DIR,
                gpus=req.gpus,
                n_p=req.n_p,
                f0method=req.f0method,
                if_f0=req.if_f0,
                exp_dir=req.exp_dir,
                version=req.version,
                gpus_rmvpe=req.gpus_rmvpe,
                device=cfg.device,
                is_half=cfg.is_half,
            )
            self._run_popen_job(extract_job, cmds)
            append("特徴抽出", "完了" if extract_job.status == JobStatus.succeeded else f"失敗: {extract_job.error}")
            if extract_job.status != JobStatus.succeeded:
                job.status = JobStatus.failed
                job.error = extract_job.error
                return

            append("学習", "開始")
            training_jobs.write_filelist(
                now_dir=NOW_DIR,
                exp_dir1=req.exp_dir,
                if_f0=req.if_f0,
                spk_id=req.spk_id,
                version=req.version,
                sr2=req.sr,
            )
            train_cmd = training_jobs.build_train_cmd(
                python_cmd=cfg.python_cmd,
                exp_dir1=req.exp_dir,
                sr2=req.sr,
                if_f0=req.if_f0,
                save_epoch=req.save_epoch,
                total_epoch=req.total_epoch,
                batch_size=req.batch_size,
                if_save_latest=req.if_save_latest,
                pretrained_g=req.pretrained_g,
                pretrained_d=req.pretrained_d,
                gpus=req.gpus,
                if_cache_gpu=req.if_cache_gpu,
                if_save_every_weights=req.if_save_every_weights,
                version=req.version,
            )
            train_job = self._new_job("train")
            self._run_popen_job(train_job, [train_cmd])
            append("学習", "完了" if train_job.status == JobStatus.succeeded else f"失敗: {train_job.error}")
            if train_job.status != JobStatus.succeeded:
                job.status = JobStatus.failed
                job.error = train_job.error
                return

            append("インデックス作成", "開始")
            for message in training_jobs.run_train_index(
                now_dir=NOW_DIR,
                exp_dir1=req.exp_dir,
                version=req.version,
                outside_index_root=os.getenv("outside_index_root", "assets/indices"),
                n_cpu=cfg.n_cpu,
            ):
                append("インデックス作成", message)

            job.status = JobStatus.succeeded
        except Exception as e:
            job.status = JobStatus.failed
            job.error = str(e)

    def start_train1key(self, req: Train1KeyRequest) -> str:
        job = self._new_job("train1key")
        threading.Thread(
            target=self._run_train1key_job, args=(job, req), daemon=True
        ).start()
        return job.job_id

    def _run_uvr_job(self, job: TrainingJob, req: UvrSeparateRequest):
        from infer.modules.uvr5.modules import uvr

        try:
            prev = ""
            for message in uvr(
                req.model_name,
                req.inp_root,
                req.save_root_vocal,
                [],
                req.save_root_ins,
                req.agg,
                req.format0,
            ):
                # uvr() は累積済みの全文を毎回 yield するため、差分だけ記録する
                if message.startswith(prev):
                    delta = message[len(prev) :].lstrip("\n")
                else:
                    delta = message
                if delta:
                    job.memory_log.append(delta)
                prev = message
            job.status = JobStatus.succeeded
        except Exception as e:
            job.status = JobStatus.failed
            job.error = str(e)

    def start_uvr(self, req: UvrSeparateRequest) -> str:
        job = self._new_job("uvr_separate")
        threading.Thread(
            target=self._run_uvr_job, args=(job, req), daemon=True
        ).start()
        return job.job_id


def _config():
    if server.config is None:
        raise HTTPException(503, "サーバーが初期化中です")
    return server.config


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
training_manager = TrainingJobManager()
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


# ── 学習系エンドポイント ─────────────────────────────────────


@app.post("/train/preprocess")
def post_train_preprocess(req: PreprocessRequest):
    job_id = training_manager.start_preprocess(req)
    return {"job_id": job_id}


@app.post("/train/extract_f0_feature")
def post_train_extract_f0_feature(req: ExtractF0FeatureRequest):
    job_id = training_manager.start_extract_f0_feature(req)
    return {"job_id": job_id}


@app.post("/train/start")
def post_train_start(req: TrainRequest):
    job_id = training_manager.start_train(req)
    return {"job_id": job_id}


@app.post("/train/index")
def post_train_index(req: TrainIndexRequest):
    job_id = training_manager.start_train_index(req)
    return {"job_id": job_id}


@app.post("/train/one_click")
def post_train_one_click(req: Train1KeyRequest):
    job_id = training_manager.start_train1key(req)
    return {"job_id": job_id}


@app.get("/train/jobs/{job_id}", response_model=JobStatusResponse)
def get_train_job(job_id: str):
    job = training_manager.get_job(job_id)
    log_delta = training_manager.read_log_delta(job)
    return JobStatusResponse(
        job_id=job.job_id,
        kind=job.kind,
        status=job.status.value,
        log_delta=log_delta,
        error=job.error,
    )


# ── UVR5 ボーカル分離 ───────────────────────────────────────


@app.get("/uvr5_models")
def get_uvr5_models():
    root = os.getenv("weight_uvr5_root", "assets/uvr5_weights")
    names = []
    if os.path.isdir(root):
        for name in os.listdir(root):
            if name.endswith(".pth") or "onnx" in name:
                names.append(name.replace(".pth", ""))
    return {"models": names}


@app.post("/uvr/separate")
def post_uvr_separate(req: UvrSeparateRequest):
    job_id = training_manager.start_uvr(req)
    return {"job_id": job_id}


# ── モデル管理（マージ・情報表示・変更・抽出） ───────────────


@app.post("/model/merge")
def post_model_merge(req: ModelMergeRequest):
    from infer.lib.train.process_ckpt import merge

    result = merge(
        req.path1,
        req.path2,
        req.alpha1,
        req.sr,
        i18n("是") if req.f0 else i18n("否"),
        req.info,
        req.name,
        req.version,
    )
    if result != "Success.":
        raise HTTPException(400, result)
    return {"message": result}


@app.post("/model/info")
def post_model_info(req: ModelInfoRequest):
    from infer.lib.train.process_ckpt import show_info

    result = show_info(req.path)
    if result.startswith("Traceback"):
        raise HTTPException(400, result)
    return {"info": result}


@app.post("/model/change_info")
def post_model_change_info(req: ModelChangeInfoRequest):
    from infer.lib.train.process_ckpt import change_info

    result = change_info(req.path, req.info, req.name)
    if result != "Success.":
        raise HTTPException(400, result)
    return {"message": result}


@app.post("/model/extract")
def post_model_extract(req: ModelExtractRequest):
    from infer.lib.train.process_ckpt import extract_small_model

    result = extract_small_model(
        req.path, req.name, req.sr, req.if_f0, req.info, req.version
    )
    if result != "Success.":
        raise HTTPException(400, result)
    return {"message": result}


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
