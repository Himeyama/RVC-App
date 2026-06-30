import os
import sys
from dotenv import load_dotenv
import shutil

load_dotenv()

os.environ["OMP_NUM_THREADS"] = "4"
if sys.platform == "darwin":
    os.environ["PYTORCH_ENABLE_MPS_FALLBACK"] = "1"

now_dir = os.getcwd()
sys.path.append(now_dir)
import multiprocessing

flag_vc = False


def printt(strr, *args):
    if len(args) == 0:
        print(strr)
    else:
        print(strr % args)


def phase_vocoder(a, b, fade_out, fade_in):
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
    result = (
        a * (fade_out**2)
        + b * (fade_in**2)
        + torch.sum(absab * torch.cos(w * t + phia), -1) * window / n
    )
    return result


class Harvest(multiprocessing.Process):
    def __init__(self, inp_q, opt_q):
        multiprocessing.Process.__init__(self)
        self.inp_q = inp_q
        self.opt_q = opt_q

    def run(self):
        import numpy as np
        import pyworld

        while 1:
            idx, x, res_f0, n_cpu, ts = self.inp_q.get()
            f0, t = pyworld.harvest(
                x.astype(np.double),
                fs=16000,
                f0_ceil=1100,
                f0_floor=50,
                frame_period=10,
            )
            res_f0[idx] = f0
            if len(res_f0.keys()) >= n_cpu:
                self.opt_q.put(ts)


if __name__ == "__main__":
    import json
    import re
    import threading
    import time
    import tkinter as tk
    from tkinter import filedialog, messagebox
    from multiprocessing import Queue, cpu_count

    import customtkinter as ctk
    import librosa
    from tools.torchgate import TorchGate
    import numpy as np
    import sounddevice as sd
    import torch
    import torch.nn.functional as F
    import torchaudio.transforms as tat

    from infer.lib import rtrvc as rvc_for_realtime
    from configs.config import Config

    ctk.set_appearance_mode("dark")
    ctk.set_default_color_theme("blue")

    current_dir = os.getcwd()
    inp_q = Queue()
    opt_q = Queue()
    n_cpu = min(cpu_count(), 8)
    for _ in range(n_cpu):
        p = Harvest(inp_q, opt_q)
        p.daemon = True
        p.start()

    class GUIConfig:
        def __init__(self) -> None:
            self.pth_path: str = ""
            self.index_path: str = ""
            self.pitch: int = 0
            self.formant: float = 0.0
            self.sr_type: str = "sr_model"
            self.block_time: float = 0.25
            self.threhold: int = -60
            self.crossfade_time: float = 0.05
            self.extra_time: float = 2.5
            self.I_noise_reduce: bool = False
            self.O_noise_reduce: bool = False
            self.use_pv: bool = False
            self.rms_mix_rate: float = 0.0
            self.index_rate: float = 0.0
            self.n_cpu: int = min(n_cpu, 4)
            self.f0method: str = "fcpe"
            self.sg_hostapi: str = ""
            self.wasapi_exclusive: bool = False
            self.sg_input_device: str = ""
            self.sg_output_device: str = ""

    class GUI(ctk.CTk):
        def __init__(self) -> None:
            super().__init__()
            self.gui_config = GUIConfig()
            self.config = Config()
            self.function = "vc"
            self.delay_time = 0
            self.hostapis = []
            self.input_devices = []
            self.output_devices = []
            self.input_devices_indices = []
            self.output_devices_indices = []
            self.stream = None

            self.title("RVC リアルタイム音声変換")
            self.resizable(True, True)

            self.update_devices()
            data = self.load()
            self.build_ui(data)
            self.protocol("WM_DELETE_WINDOW", self.on_close)

        def load(self):
            try:
                if not os.path.exists("configs/inuse/config.json"):
                    shutil.copy("configs/config.json", "configs/inuse/config.json")
                with open("configs/inuse/config.json", "r") as j:
                    data = json.load(j)
            except Exception:
                data = {
                    "pth_path": "",
                    "index_path": "",
                    "sg_hostapi": self.hostapis[0] if self.hostapis else "",
                    "sg_wasapi_exclusive": False,
                    "sg_input_device": self.input_devices[
                        self.input_devices_indices.index(sd.default.device[0])
                    ] if self.input_devices else "",
                    "sg_output_device": self.output_devices[
                        self.output_devices_indices.index(sd.default.device[1])
                    ] if self.output_devices else "",
                    "sr_type": "sr_model",
                    "threhold": -60,
                    "pitch": 0,
                    "formant": 0.0,
                    "index_rate": 0.0,
                    "rms_mix_rate": 0.0,
                    "block_time": 0.25,
                    "crossfade_length": 0.05,
                    "extra_time": 2.5,
                    "n_cpu": 4,
                    "f0method": "fcpe",
                    "use_jit": False,
                    "use_pv": False,
                }
                with open("configs/inuse/config.json", "w") as j:
                    json.dump(data, j)
            return data

        def build_ui(self, data):
            self.columnconfigure(0, weight=1)

            # ── モデル読み込み ──────────────────────────────────────
            model_frame = ctk.CTkFrame(self)
            model_frame.grid(row=0, column=0, padx=10, pady=(10, 4), sticky="ew")
            model_frame.columnconfigure(1, weight=1)

            ctk.CTkLabel(model_frame, text="モデル読み込み", font=ctk.CTkFont(size=13, weight="bold")).grid(
                row=0, column=0, columnspan=3, padx=10, pady=(8, 4), sticky="w"
            )

            ctk.CTkLabel(model_frame, text="モデル (.pth)").grid(row=1, column=0, padx=(10, 4), pady=4, sticky="w")
            self.pth_path_var = ctk.StringVar(value=data.get("pth_path", ""))
            ctk.CTkEntry(model_frame, textvariable=self.pth_path_var).grid(row=1, column=1, padx=4, pady=4, sticky="ew")
            ctk.CTkButton(model_frame, text="参照...", width=80,
                          command=lambda: self._browse_file(self.pth_path_var, "assets/weights", [("PTH ファイル", "*.pth")])).grid(
                row=1, column=2, padx=(4, 10), pady=4
            )

            ctk.CTkLabel(model_frame, text="インデックス (.index)").grid(row=2, column=0, padx=(10, 4), pady=4, sticky="w")
            self.index_path_var = ctk.StringVar(value=data.get("index_path", ""))
            ctk.CTkEntry(model_frame, textvariable=self.index_path_var).grid(row=2, column=1, padx=4, pady=4, sticky="ew")
            ctk.CTkButton(model_frame, text="参照...", width=80,
                          command=lambda: self._browse_file(self.index_path_var, "logs", [("INDEX ファイル", "*.index")])).grid(
                row=2, column=2, padx=(4, 10), pady=4
            )

            # ── オーディオデバイス ─────────────────────────────────
            dev_frame = ctk.CTkFrame(self)
            dev_frame.grid(row=1, column=0, padx=10, pady=4, sticky="ew")
            dev_frame.columnconfigure(1, weight=1)

            ctk.CTkLabel(dev_frame, text="オーディオデバイス", font=ctk.CTkFont(size=13, weight="bold")).grid(
                row=0, column=0, columnspan=3, padx=10, pady=(8, 4), sticky="w"
            )

            ctk.CTkLabel(dev_frame, text="デバイス種別").grid(row=1, column=0, padx=(10, 4), pady=4, sticky="w")
            self.hostapi_var = ctk.StringVar(value=data.get("sg_hostapi", self.hostapis[0] if self.hostapis else ""))
            self.hostapi_combo = ctk.CTkComboBox(dev_frame, variable=self.hostapi_var,
                                                  values=self.hostapis, command=self._on_hostapi_change)
            self.hostapi_combo.grid(row=1, column=1, padx=4, pady=4, sticky="ew")
            self.wasapi_var = ctk.BooleanVar(value=data.get("sg_wasapi_exclusive", False))
            ctk.CTkCheckBox(dev_frame, text="WASAPI 排他モード", variable=self.wasapi_var).grid(
                row=1, column=2, padx=(4, 10), pady=4
            )

            ctk.CTkLabel(dev_frame, text="入力デバイス").grid(row=2, column=0, padx=(10, 4), pady=4, sticky="w")
            self.input_device_var = ctk.StringVar(value=data.get("sg_input_device", ""))
            self.input_combo = ctk.CTkComboBox(dev_frame, variable=self.input_device_var, values=self.input_devices)
            self.input_combo.grid(row=2, column=1, columnspan=2, padx=(4, 10), pady=4, sticky="ew")

            ctk.CTkLabel(dev_frame, text="出力デバイス").grid(row=3, column=0, padx=(10, 4), pady=4, sticky="w")
            self.output_device_var = ctk.StringVar(value=data.get("sg_output_device", ""))
            self.output_combo = ctk.CTkComboBox(dev_frame, variable=self.output_device_var, values=self.output_devices)
            self.output_combo.grid(row=3, column=1, columnspan=2, padx=(4, 10), pady=4, sticky="ew")

            sr_row = ctk.CTkFrame(dev_frame, fg_color="transparent")
            sr_row.grid(row=4, column=0, columnspan=3, padx=10, pady=(4, 8), sticky="w")
            ctk.CTkButton(sr_row, text="デバイス一覧を更新", width=140, command=self._reload_devices).pack(side="left", padx=(0, 16))
            self.sr_type_var = ctk.StringVar(value=data.get("sr_type", "sr_model"))
            ctk.CTkRadioButton(sr_row, text="モデルのサンプリングレート", variable=self.sr_type_var, value="sr_model").pack(side="left", padx=4)
            ctk.CTkRadioButton(sr_row, text="デバイスのサンプリングレート", variable=self.sr_type_var, value="sr_device").pack(side="left", padx=4)
            ctk.CTkLabel(sr_row, text="サンプリングレート:").pack(side="left", padx=(12, 4))
            self.sr_stream_label = ctk.CTkLabel(sr_row, text="")
            self.sr_stream_label.pack(side="left")

            # ── 基本設定 / パフォーマンス設定（横並び）───────────────
            mid_frame = ctk.CTkFrame(self, fg_color="transparent")
            mid_frame.grid(row=2, column=0, padx=10, pady=4, sticky="ew")
            mid_frame.columnconfigure(0, weight=1)
            mid_frame.columnconfigure(1, weight=1)

            # 基本設定
            basic_frame = ctk.CTkFrame(mid_frame)
            basic_frame.grid(row=0, column=0, padx=(0, 4), sticky="nsew")
            basic_frame.columnconfigure(1, weight=1)

            ctk.CTkLabel(basic_frame, text="基本設定", font=ctk.CTkFont(size=13, weight="bold")).grid(
                row=0, column=0, columnspan=2, padx=10, pady=(8, 4), sticky="w"
            )

            sliders_basic = [
                ("無音閾値 (dB)",     "threhold",   -60,  0,    1,    data.get("threhold", -60)),
                ("ピッチ (半音)",     "pitch",      -16,  16,   1,    data.get("pitch", 0)),
                ("フォルマント",      "formant",    -2,   2,    0.05, data.get("formant", 0.0)),
                ("インデックス強度",  "index_rate", 0.0,  1.0,  0.01, data.get("index_rate", 0.0)),
                ("音量ミックス率",    "rms_mix_rate",0.0, 1.0,  0.01, data.get("rms_mix_rate", 0.0)),
            ]
            self._sliders = {}
            for i, (label, key, from_, to, step, default) in enumerate(sliders_basic, start=1):
                ctk.CTkLabel(basic_frame, text=label, anchor="w").grid(row=i, column=0, padx=(10, 4), pady=3, sticky="w")
                var = ctk.DoubleVar(value=default)
                sl = ctk.CTkSlider(basic_frame, from_=from_, to=to, variable=var,
                                   command=lambda v, k=key: self._on_slider(k, v))
                sl.grid(row=i, column=1, padx=(4, 10), pady=3, sticky="ew")
                self._sliders[key] = var

            # F0 アルゴリズム
            f0_row = ctk.CTkFrame(basic_frame, fg_color="transparent")
            f0_row.grid(row=len(sliders_basic)+1, column=0, columnspan=2, padx=10, pady=(4, 8), sticky="w")
            ctk.CTkLabel(f0_row, text="F0 推定:").pack(side="left", padx=(0, 8))
            self.f0method_var = ctk.StringVar(value=data.get("f0method", "fcpe"))
            for method in ["pm", "harvest", "crepe", "rmvpe", "fcpe"]:
                ctk.CTkRadioButton(f0_row, text=method, variable=self.f0method_var, value=method,
                                   command=lambda m=method: self._on_f0_change(m)).pack(side="left", padx=3)

            # パフォーマンス設定
            perf_frame = ctk.CTkFrame(mid_frame)
            perf_frame.grid(row=0, column=1, padx=(4, 0), sticky="nsew")
            perf_frame.columnconfigure(1, weight=1)

            ctk.CTkLabel(perf_frame, text="パフォーマンス設定", font=ctk.CTkFont(size=13, weight="bold")).grid(
                row=0, column=0, columnspan=2, padx=10, pady=(8, 4), sticky="w"
            )

            sliders_perf = [
                ("バッファ長 (秒)",          "block_time",       0.02, 1.5,  0.01, data.get("block_time", 0.25)),
                ("Harvest CPU スレッド数",   "n_cpu",            1,    n_cpu,1,    data.get("n_cpu", min(self.gui_config.n_cpu, n_cpu))),
                ("クロスフェード長 (秒)",    "crossfade_length", 0.01, 0.15, 0.01, data.get("crossfade_length", 0.05)),
                ("追加推論時間 (秒)",        "extra_time",       0.05, 5.0,  0.01, data.get("extra_time", 2.5)),
            ]
            for i, (label, key, from_, to, step, default) in enumerate(sliders_perf, start=1):
                ctk.CTkLabel(perf_frame, text=label, anchor="w").grid(row=i, column=0, padx=(10, 4), pady=3, sticky="w")
                var = ctk.DoubleVar(value=default)
                sl = ctk.CTkSlider(perf_frame, from_=from_, to=to, variable=var)
                sl.grid(row=i, column=1, padx=(4, 10), pady=3, sticky="ew")
                self._sliders[key] = var

            chk_row = ctk.CTkFrame(perf_frame, fg_color="transparent")
            chk_row.grid(row=len(sliders_perf)+1, column=0, columnspan=2, padx=10, pady=(4, 8), sticky="w")
            self.I_noise_var = ctk.BooleanVar(value=False)
            self.O_noise_var = ctk.BooleanVar(value=False)
            self.use_pv_var  = ctk.BooleanVar(value=data.get("use_pv", False))
            ctk.CTkCheckBox(chk_row, text="入力ノイズ除去", variable=self.I_noise_var,
                            command=self._on_I_noise).pack(side="left", padx=(0, 8))
            ctk.CTkCheckBox(chk_row, text="出力ノイズ除去", variable=self.O_noise_var).pack(side="left", padx=8)
            ctk.CTkCheckBox(chk_row, text="フェーズボコーダー", variable=self.use_pv_var).pack(side="left", padx=8)

            # ── ステータスバー ──────────────────────────────────────
            status_frame = ctk.CTkFrame(self)
            status_frame.grid(row=3, column=0, padx=10, pady=(4, 10), sticky="ew")

            self.start_btn = ctk.CTkButton(status_frame, text="音声変換 開始", width=130,
                                           fg_color="#2e7d32", hover_color="#1b5e20",
                                           command=self._start_vc)
            self.start_btn.pack(side="left", padx=(10, 4), pady=8)

            self.stop_btn = ctk.CTkButton(status_frame, text="音声変換 停止", width=130,
                                          fg_color="#c62828", hover_color="#7f0000",
                                          command=self._stop_vc)
            self.stop_btn.pack(side="left", padx=4, pady=8)

            self.function_var = ctk.StringVar(value="vc")
            ctk.CTkRadioButton(status_frame, text="入力モニタリング", variable=self.function_var, value="im").pack(side="left", padx=12)
            ctk.CTkRadioButton(status_frame, text="音声変換", variable=self.function_var, value="vc").pack(side="left", padx=4)

            ctk.CTkLabel(status_frame, text="アルゴリズム遅延:").pack(side="left", padx=(16, 4))
            self.delay_label = ctk.CTkLabel(status_frame, text="0 ms", width=60)
            self.delay_label.pack(side="left")

            ctk.CTkLabel(status_frame, text="推論時間:").pack(side="left", padx=(12, 4))
            self.infer_label = ctk.CTkLabel(status_frame, text="0 ms", width=60)
            self.infer_label.pack(side="left")

        # ── ヘルパー ──────────────────────────────────────────────

        def _browse_file(self, var, initial_dir, filetypes):
            path = filedialog.askopenfilename(
                initialdir=os.path.join(os.getcwd(), initial_dir),
                filetypes=filetypes,
            )
            if path:
                var.set(path)

        def _on_hostapi_change(self, value):
            self.gui_config.sg_hostapi = value
            self.update_devices(hostapi_name=value)
            self.hostapi_combo.configure(values=self.hostapis)
            self.input_combo.configure(values=self.input_devices)
            self.output_combo.configure(values=self.output_devices)
            if self.input_devices:
                self.input_device_var.set(self.input_devices[0])
            if self.output_devices:
                self.output_device_var.set(self.output_devices[0])

        def _reload_devices(self):
            self._on_hostapi_change(self.hostapi_var.get())

        def _on_slider(self, key, value):
            if key == "threhold":
                self.gui_config.threhold = int(float(value))
            elif key == "pitch":
                self.gui_config.pitch = int(float(value))
                if hasattr(self, "rvc"):
                    self.rvc.change_key(int(float(value)))
            elif key == "formant":
                self.gui_config.formant = float(value)
                if hasattr(self, "rvc"):
                    self.rvc.change_formant(float(value))
            elif key == "index_rate":
                self.gui_config.index_rate = float(value)
                if hasattr(self, "rvc"):
                    self.rvc.change_index_rate(float(value))
            elif key == "rms_mix_rate":
                self.gui_config.rms_mix_rate = float(value)

        def _on_f0_change(self, method):
            self.gui_config.f0method = method

        def _on_I_noise(self):
            self.gui_config.I_noise_reduce = self.I_noise_var.get()
            if self.stream is not None:
                delta = (1 if self.I_noise_var.get() else -1) * min(
                    self._sliders["crossfade_length"].get(), 0.04
                )
                self.delay_time += delta
                self.delay_label.configure(text=f"{int(round(self.delay_time * 1000))} ms")

        def _start_vc(self):
            global flag_vc
            if flag_vc:
                return
            if not self._set_values():
                return
            printt("CUDA 使用可能: %s", torch.cuda.is_available())
            self.start_vc()
            self._save_settings()
            if self.stream is not None:
                self.delay_time = (
                    self.stream.latency[-1]
                    + self._sliders["block_time"].get()
                    + self._sliders["crossfade_length"].get()
                    + 0.01
                )
            if self.I_noise_var.get():
                self.delay_time += min(self._sliders["crossfade_length"].get(), 0.04)
            self.sr_stream_label.configure(text=str(self.gui_config.samplerate))
            self.delay_label.configure(text=f"{int(round(self.delay_time * 1000))} ms")

        def _stop_vc(self):
            self.stop_stream()

        def _set_values(self):
            pth = self.pth_path_var.get().strip()
            idx = self.index_path_var.get().strip()
            if not pth:
                messagebox.showerror("エラー", "PTH ファイルを選択してください")
                return False
            if not idx:
                messagebox.showerror("エラー", "INDEX ファイルを選択してください")
                return False
            pattern = re.compile("[^\x00-\x7F]+")
            if pattern.findall(pth):
                messagebox.showerror("エラー", "PTH ファイルのパスに非ASCII文字を含めないでください")
                return False
            if pattern.findall(idx):
                messagebox.showerror("エラー", "INDEX ファイルのパスに非ASCII文字を含めないでください")
                return False
            self.set_devices(self.input_device_var.get(), self.output_device_var.get())
            self.config.use_jit = False
            self.gui_config.sg_hostapi = self.hostapi_var.get()
            self.gui_config.sg_wasapi_exclusive = self.wasapi_var.get()
            self.gui_config.sg_input_device = self.input_device_var.get()
            self.gui_config.sg_output_device = self.output_device_var.get()
            self.gui_config.pth_path = pth
            self.gui_config.index_path = idx
            self.gui_config.sr_type = self.sr_type_var.get()
            self.gui_config.threhold = int(self._sliders["threhold"].get())
            self.gui_config.pitch = int(self._sliders["pitch"].get())
            self.gui_config.formant = self._sliders["formant"].get()
            self.gui_config.block_time = self._sliders["block_time"].get()
            self.gui_config.crossfade_time = self._sliders["crossfade_length"].get()
            self.gui_config.extra_time = self._sliders["extra_time"].get()
            self.gui_config.I_noise_reduce = self.I_noise_var.get()
            self.gui_config.O_noise_reduce = self.O_noise_var.get()
            self.gui_config.use_pv = self.use_pv_var.get()
            self.gui_config.rms_mix_rate = self._sliders["rms_mix_rate"].get()
            self.gui_config.index_rate = self._sliders["index_rate"].get()
            self.gui_config.n_cpu = int(self._sliders["n_cpu"].get())
            self.gui_config.f0method = self.f0method_var.get()
            return True

        def _save_settings(self):
            settings = {
                "pth_path": self.gui_config.pth_path,
                "index_path": self.gui_config.index_path,
                "sg_hostapi": self.gui_config.sg_hostapi,
                "sg_wasapi_exclusive": self.gui_config.sg_wasapi_exclusive,
                "sg_input_device": self.gui_config.sg_input_device,
                "sg_output_device": self.gui_config.sg_output_device,
                "sr_type": self.gui_config.sr_type,
                "threhold": self.gui_config.threhold,
                "pitch": self.gui_config.pitch,
                "formant": self.gui_config.formant,
                "index_rate": self.gui_config.index_rate,
                "rms_mix_rate": self.gui_config.rms_mix_rate,
                "block_time": self.gui_config.block_time,
                "crossfade_length": self.gui_config.crossfade_time,
                "extra_time": self.gui_config.extra_time,
                "n_cpu": self.gui_config.n_cpu,
                "use_jit": False,
                "use_pv": self.gui_config.use_pv,
                "f0method": self.gui_config.f0method,
            }
            with open("configs/inuse/config.json", "w") as j:
                json.dump(settings, j)

        def on_close(self):
            self.stop_stream()
            self.destroy()

        # ── デバイス管理 ──────────────────────────────────────────

        def update_devices(self, hostapi_name=None):
            global flag_vc
            flag_vc = False
            sd._terminate()
            sd._initialize()
            devices = sd.query_devices()
            hostapis = sd.query_hostapis()
            for hostapi in hostapis:
                for device_idx in hostapi["devices"]:
                    devices[device_idx]["hostapi_name"] = hostapi["name"]
            self.hostapis = [hostapi["name"] for hostapi in hostapis]
            if hostapi_name not in self.hostapis:
                hostapi_name = self.hostapis[0] if self.hostapis else None
            self.input_devices = [
                d["name"] for d in devices
                if d["max_input_channels"] > 0 and d.get("hostapi_name") == hostapi_name
            ]
            self.output_devices = [
                d["name"] for d in devices
                if d["max_output_channels"] > 0 and d.get("hostapi_name") == hostapi_name
            ]
            self.input_devices_indices = [
                d["index"] if "index" in d else d["name"]
                for d in devices
                if d["max_input_channels"] > 0 and d.get("hostapi_name") == hostapi_name
            ]
            self.output_devices_indices = [
                d["index"] if "index" in d else d["name"]
                for d in devices
                if d["max_output_channels"] > 0 and d.get("hostapi_name") == hostapi_name
            ]

        def set_devices(self, input_device, output_device):
            sd.default.device[0] = self.input_devices_indices[
                self.input_devices.index(input_device)
            ]
            sd.default.device[1] = self.output_devices_indices[
                self.output_devices.index(output_device)
            ]
            printt("入力デバイス: %s:%s", str(sd.default.device[0]), input_device)
            printt("出力デバイス: %s:%s", str(sd.default.device[1]), output_device)

        def get_device_samplerate(self):
            return int(sd.query_devices(device=sd.default.device[0])["default_samplerate"])

        def get_device_channels(self):
            max_in = sd.query_devices(device=sd.default.device[0])["max_input_channels"]
            max_out = sd.query_devices(device=sd.default.device[1])["max_output_channels"]
            return min(max_in, max_out, 2)

        # ── 音声変換コア ──────────────────────────────────────────

        def start_vc(self):
            torch.cuda.empty_cache()
            self.rvc = rvc_for_realtime.RVC(
                self.gui_config.pitch,
                self.gui_config.formant,
                self.gui_config.pth_path,
                self.gui_config.index_path,
                self.gui_config.index_rate,
                self.gui_config.n_cpu,
                inp_q,
                opt_q,
                self.config,
                self.rvc if hasattr(self, "rvc") else None,
            )
            self.gui_config.samplerate = (
                self.rvc.tgt_sr
                if self.gui_config.sr_type == "sr_model"
                else self.get_device_samplerate()
            )
            self.gui_config.channels = self.get_device_channels()
            self.zc = self.gui_config.samplerate // 100
            self.block_frame = (
                int(np.round(self.gui_config.block_time * self.gui_config.samplerate / self.zc)) * self.zc
            )
            self.block_frame_16k = 160 * self.block_frame // self.zc
            self.crossfade_frame = (
                int(np.round(self.gui_config.crossfade_time * self.gui_config.samplerate / self.zc)) * self.zc
            )
            self.sola_buffer_frame = min(self.crossfade_frame, 4 * self.zc)
            self.sola_search_frame = self.zc
            self.extra_frame = (
                int(np.round(self.gui_config.extra_time * self.gui_config.samplerate / self.zc)) * self.zc
            )
            self.input_wav: torch.Tensor = torch.zeros(
                self.extra_frame + self.crossfade_frame + self.sola_search_frame + self.block_frame,
                device=self.config.device, dtype=torch.float32,
            )
            self.input_wav_denoise: torch.Tensor = self.input_wav.clone()
            self.input_wav_res: torch.Tensor = torch.zeros(
                160 * self.input_wav.shape[0] // self.zc,
                device=self.config.device, dtype=torch.float32,
            )
            self.rms_buffer: np.ndarray = np.zeros(4 * self.zc, dtype="float32")
            self.sola_buffer: torch.Tensor = torch.zeros(
                self.sola_buffer_frame, device=self.config.device, dtype=torch.float32
            )
            self.nr_buffer: torch.Tensor = self.sola_buffer.clone()
            self.output_buffer: torch.Tensor = self.input_wav.clone()
            self.skip_head = self.extra_frame // self.zc
            self.return_length = (self.block_frame + self.sola_buffer_frame + self.sola_search_frame) // self.zc
            self.fade_in_window: torch.Tensor = (
                torch.sin(
                    0.5 * np.pi * torch.linspace(
                        0.0, 1.0, steps=self.sola_buffer_frame,
                        device=self.config.device, dtype=torch.float32,
                    )
                ) ** 2
            )
            self.fade_out_window: torch.Tensor = 1 - self.fade_in_window
            self.resampler = tat.Resample(
                orig_freq=self.gui_config.samplerate, new_freq=16000, dtype=torch.float32,
            ).to(self.config.device)
            if self.rvc.tgt_sr != self.gui_config.samplerate:
                self.resampler2 = tat.Resample(
                    orig_freq=self.rvc.tgt_sr, new_freq=self.gui_config.samplerate, dtype=torch.float32,
                ).to(self.config.device)
            else:
                self.resampler2 = None
            self.tg = TorchGate(
                sr=self.gui_config.samplerate, n_fft=4 * self.zc, prop_decrease=0.9
            ).to(self.config.device)
            self.start_stream()

        def start_stream(self):
            global flag_vc
            if not flag_vc:
                flag_vc = True
                extra_settings = None
                if "WASAPI" in self.gui_config.sg_hostapi and self.gui_config.sg_wasapi_exclusive:
                    extra_settings = sd.WasapiSettings(exclusive=True)
                self.stream = sd.Stream(
                    callback=self.audio_callback,
                    blocksize=self.block_frame,
                    samplerate=self.gui_config.samplerate,
                    channels=self.gui_config.channels,
                    dtype="float32",
                    extra_settings=extra_settings,
                )
                self.stream.start()

        def stop_stream(self):
            global flag_vc
            if flag_vc:
                flag_vc = False
                if self.stream is not None:
                    self.stream.abort()
                    self.stream.close()
                    self.stream = None

        def audio_callback(self, indata: np.ndarray, outdata: np.ndarray, frames, times, status):
            global flag_vc
            start_time = time.perf_counter()
            indata = librosa.to_mono(indata.T)
            if self.gui_config.threhold > -60:
                indata = np.append(self.rms_buffer, indata)
                rms = librosa.feature.rms(y=indata, frame_length=4 * self.zc, hop_length=self.zc)[:, 2:]
                self.rms_buffer[:] = indata[-4 * self.zc :]
                indata = indata[2 * self.zc - self.zc // 2 :]
                db_threhold = (librosa.amplitude_to_db(rms, ref=1.0)[0] < self.gui_config.threhold)
                for i in range(db_threhold.shape[0]):
                    if db_threhold[i]:
                        indata[i * self.zc : (i + 1) * self.zc] = 0
                indata = indata[self.zc // 2 :]
            self.input_wav[: -self.block_frame] = self.input_wav[self.block_frame :].clone()
            self.input_wav[-indata.shape[0] :] = torch.from_numpy(indata).to(self.config.device)
            self.input_wav_res[: -self.block_frame_16k] = self.input_wav_res[self.block_frame_16k :].clone()
            # 入力ノイズ除去とリサンプリング
            if self.gui_config.I_noise_reduce:
                self.input_wav_denoise[: -self.block_frame] = self.input_wav_denoise[self.block_frame :].clone()
                input_wav = self.input_wav[-self.sola_buffer_frame - self.block_frame :]
                input_wav = self.tg(input_wav.unsqueeze(0), self.input_wav.unsqueeze(0)).squeeze(0)
                input_wav[: self.sola_buffer_frame] *= self.fade_in_window
                input_wav[: self.sola_buffer_frame] += self.nr_buffer * self.fade_out_window
                self.input_wav_denoise[-self.block_frame :] = input_wav[: self.block_frame]
                self.nr_buffer[:] = input_wav[self.block_frame :]
                self.input_wav_res[-self.block_frame_16k - 160 :] = self.resampler(
                    self.input_wav_denoise[-self.block_frame - 2 * self.zc :]
                )[160:]
            else:
                self.input_wav_res[-160 * (indata.shape[0] // self.zc + 1) :] = self.resampler(
                    self.input_wav[-indata.shape[0] - 2 * self.zc :]
                )[160:]
            # 推論
            if self.function_var.get() == "vc":
                infer_wav = self.rvc.infer(
                    self.input_wav_res,
                    self.block_frame_16k,
                    self.skip_head,
                    self.return_length,
                    self.gui_config.f0method,
                )
                if self.resampler2 is not None:
                    infer_wav = self.resampler2(infer_wav)
            elif self.gui_config.I_noise_reduce:
                infer_wav = self.input_wav_denoise[self.extra_frame :].clone()
            else:
                infer_wav = self.input_wav[self.extra_frame :].clone()
            # 出力ノイズ除去
            if self.gui_config.O_noise_reduce and self.function_var.get() == "vc":
                self.output_buffer[: -self.block_frame] = self.output_buffer[self.block_frame :].clone()
                self.output_buffer[-self.block_frame :] = infer_wav[-self.block_frame :]
                infer_wav = self.tg(infer_wav.unsqueeze(0), self.output_buffer.unsqueeze(0)).squeeze(0)
            # 音量エンベロープミキシング
            if self.gui_config.rms_mix_rate < 1 and self.function_var.get() == "vc":
                input_wav = (
                    self.input_wav_denoise[self.extra_frame :]
                    if self.gui_config.I_noise_reduce
                    else self.input_wav[self.extra_frame :]
                )
                rms1 = librosa.feature.rms(
                    y=input_wav[: infer_wav.shape[0]].cpu().numpy(),
                    frame_length=4 * self.zc, hop_length=self.zc,
                )
                rms1 = F.interpolate(
                    torch.from_numpy(rms1).to(self.config.device).unsqueeze(0),
                    size=infer_wav.shape[0] + 1, mode="linear", align_corners=True,
                )[0, 0, :-1]
                rms2 = librosa.feature.rms(
                    y=infer_wav[:].cpu().numpy(),
                    frame_length=4 * self.zc, hop_length=self.zc,
                )
                rms2 = F.interpolate(
                    torch.from_numpy(rms2).to(self.config.device).unsqueeze(0),
                    size=infer_wav.shape[0] + 1, mode="linear", align_corners=True,
                )[0, 0, :-1]
                rms2 = torch.max(rms2, torch.zeros_like(rms2) + 1e-3)
                infer_wav *= torch.pow(rms1 / rms2, torch.tensor(1 - self.gui_config.rms_mix_rate))
            # SOLA アルゴリズム（https://github.com/yxlllc/DDSP-SVC より）
            conv_input = infer_wav[None, None, : self.sola_buffer_frame + self.sola_search_frame]
            cor_nom = F.conv1d(conv_input, self.sola_buffer[None, None, :])
            cor_den = torch.sqrt(
                F.conv1d(conv_input**2, torch.ones(1, 1, self.sola_buffer_frame, device=self.config.device)) + 1e-8
            )
            if sys.platform == "darwin":
                _, sola_offset = torch.max(cor_nom[0, 0] / cor_den[0, 0])
                sola_offset = sola_offset.item()
            else:
                sola_offset = torch.argmax(cor_nom[0, 0] / cor_den[0, 0])
            printt("SOLAオフセット = %d", int(sola_offset))
            infer_wav = infer_wav[sola_offset:]
            if "privateuseone" in str(self.config.device) or not self.gui_config.use_pv:
                infer_wav[: self.sola_buffer_frame] *= self.fade_in_window
                infer_wav[: self.sola_buffer_frame] += self.sola_buffer * self.fade_out_window
            else:
                infer_wav[: self.sola_buffer_frame] = phase_vocoder(
                    self.sola_buffer, infer_wav[: self.sola_buffer_frame],
                    self.fade_out_window, self.fade_in_window,
                )
            self.sola_buffer[:] = infer_wav[self.block_frame : self.block_frame + self.sola_buffer_frame]
            outdata[:] = (
                infer_wav[: self.block_frame].repeat(self.gui_config.channels, 1).t().cpu().numpy()
            )
            total_time = time.perf_counter() - start_time
            if flag_vc:
                self.infer_label.configure(text=f"{int(total_time * 1000)} ms")
            printt("推論時間: %.2f 秒", total_time)

    app = GUI()
    app.mainloop()
