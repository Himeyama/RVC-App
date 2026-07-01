"""学習パイプライン（前処理・特徴抽出・学習・インデックス作成）の
Gradio非依存版ロジック。api_gui.py の TrainingJobManager から呼び出される。

infer-web.py の preprocess_dataset / extract_f0_feature / click_train / train_index
を、i18n・yield ストリーミングを排して「コマンド生成」と「同期処理」に分離したもの。
"""

import json
import logging
import os
import pathlib
import platform
import traceback
from random import shuffle
from typing import Generator

import numpy as np

logger = logging.getLogger(__name__)

sr_dict = {
    "32k": 32000,
    "40k": 40000,
    "48k": 48000,
}


def build_preprocess_cmd(
    python_cmd: str,
    now_dir: str,
    trainset_dir: str,
    exp_dir: str,
    sr: str,
    n_p: int,
    noparallel: bool,
    preprocess_per: float,
) -> tuple[str, str]:
    """前処理コマンドとログファイルパスを返す。"""
    sr_value = sr_dict[sr]
    log_dir = "%s/logs/%s" % (now_dir, exp_dir)
    os.makedirs(log_dir, exist_ok=True)
    log_path = "%s/preprocess.log" % log_dir
    open(log_path, "w").close()
    cmd = '"%s" infer/modules/train/preprocess.py "%s" %s %s "%s" %s %.1f' % (
        python_cmd,
        trainset_dir,
        sr_value,
        n_p,
        log_dir,
        noparallel,
        preprocess_per,
    )
    return cmd, log_path


def build_extract_f0_feature_cmds(
    python_cmd: str,
    now_dir: str,
    gpus: str,
    n_p: int,
    f0method: str,
    if_f0: bool,
    exp_dir: str,
    version: str,
    gpus_rmvpe: str,
    device: str,
    is_half: bool,
) -> tuple[list[str], str]:
    """特徴抽出（F0 + HuBERT特徴）のコマンド一覧とログファイルパスを返す。

    戻り値のコマンドは順不同で並列実行してよい（元実装通り）。
    """
    log_dir = "%s/logs/%s" % (now_dir, exp_dir)
    os.makedirs(log_dir, exist_ok=True)
    log_path = "%s/extract_f0_feature.log" % log_dir
    open(log_path, "w").close()

    cmds: list[str] = []

    if if_f0:
        if f0method != "rmvpe_gpu":
            cmds.append(
                '"%s" infer/modules/train/extract/extract_f0_print.py "%s" %s %s'
                % (python_cmd, log_dir, n_p, f0method)
            )
        elif gpus_rmvpe != "-":
            gpu_list = gpus_rmvpe.split("-")
            leng = len(gpu_list)
            for idx, n_g in enumerate(gpu_list):
                cmds.append(
                    '"%s" infer/modules/train/extract/extract_f0_rmvpe.py %s %s %s "%s" %s'
                    % (python_cmd, leng, idx, n_g, log_dir, is_half)
                )
        else:
            cmds.append(
                '"%s" infer/modules/train/extract/extract_f0_rmvpe_dml.py "%s"'
                % (python_cmd, log_dir)
            )

    gpu_list = gpus.split("-")
    leng = len(gpu_list)
    for idx, n_g in enumerate(gpu_list):
        cmds.append(
            '"%s" infer/modules/train/extract_feature_print.py %s %s %s %s "%s" %s %s'
            % (python_cmd, device, leng, idx, n_g, log_dir, version, is_half)
        )

    return cmds, log_path


def write_filelist(
    now_dir: str,
    exp_dir1: str,
    if_f0: bool,
    spk_id: int,
    version: str,
    sr2: str,
) -> None:
    """train.py起動前に必要な filelist.txt と config.json を生成する。"""
    exp_dir = "%s/logs/%s" % (now_dir, exp_dir1)
    os.makedirs(exp_dir, exist_ok=True)
    gt_wavs_dir = "%s/0_gt_wavs" % exp_dir
    feature_dir = (
        "%s/3_feature256" % exp_dir if version == "v1" else "%s/3_feature768" % exp_dir
    )
    if if_f0:
        f0_dir = "%s/2a_f0" % exp_dir
        f0nsf_dir = "%s/2b-f0nsf" % exp_dir
        names = (
            set(name.split(".")[0] for name in os.listdir(gt_wavs_dir))
            & set(name.split(".")[0] for name in os.listdir(feature_dir))
            & set(name.split(".")[0] for name in os.listdir(f0_dir))
            & set(name.split(".")[0] for name in os.listdir(f0nsf_dir))
        )
    else:
        names = set(name.split(".")[0] for name in os.listdir(gt_wavs_dir)) & set(
            name.split(".")[0] for name in os.listdir(feature_dir)
        )

    opt = []
    for name in names:
        if if_f0:
            opt.append(
                "%s/%s.wav|%s/%s.npy|%s/%s.wav.npy|%s/%s.wav.npy|%s"
                % (
                    gt_wavs_dir.replace("\\", "\\\\"),
                    name,
                    feature_dir.replace("\\", "\\\\"),
                    name,
                    f0_dir.replace("\\", "\\\\"),
                    name,
                    f0nsf_dir.replace("\\", "\\\\"),
                    name,
                    spk_id,
                )
            )
        else:
            opt.append(
                "%s/%s.wav|%s/%s.npy|%s"
                % (
                    gt_wavs_dir.replace("\\", "\\\\"),
                    name,
                    feature_dir.replace("\\", "\\\\"),
                    name,
                    spk_id,
                )
            )

    fea_dim = 256 if version == "v1" else 768
    for _ in range(2):
        if if_f0:
            opt.append(
                "%s/logs/mute/0_gt_wavs/mute%s.wav|%s/logs/mute/3_feature%s/mute.npy|"
                "%s/logs/mute/2a_f0/mute.wav.npy|%s/logs/mute/2b-f0nsf/mute.wav.npy|%s"
                % (now_dir, sr2, now_dir, fea_dim, now_dir, now_dir, spk_id)
            )
        else:
            opt.append(
                "%s/logs/mute/0_gt_wavs/mute%s.wav|%s/logs/mute/3_feature%s/mute.npy|%s"
                % (now_dir, sr2, now_dir, fea_dim, spk_id)
            )
    shuffle(opt)
    with open("%s/filelist.txt" % exp_dir, "w") as f:
        f.write("\n".join(opt))

    config_path = (
        "v1/%s.json" % sr2 if version == "v1" or sr2 == "40k" else "v2/%s.json" % sr2
    )
    config_save_path = os.path.join(exp_dir, "config.json")
    if not pathlib.Path(config_save_path).exists():
        from configs.config import Config

        json_config = Config().json_config
        with open(config_save_path, "w", encoding="utf-8") as f:
            json.dump(
                json_config[config_path], f, ensure_ascii=False, indent=4, sort_keys=True
            )
            f.write("\n")


def build_train_cmd(
    python_cmd: str,
    exp_dir1: str,
    sr2: str,
    if_f0: bool,
    save_epoch: int,
    total_epoch: int,
    batch_size: int,
    if_save_latest: bool,
    pretrained_g: str,
    pretrained_d: str,
    gpus: str,
    if_cache_gpu: bool,
    if_save_every_weights: bool,
    version: str,
) -> str:
    """train.py 起動コマンドを返す（filelistはwrite_filelistで事前生成しておくこと）。"""
    pg_flag = "-pg %s" % pretrained_g if pretrained_g else ""
    pd_flag = "-pd %s" % pretrained_d if pretrained_d else ""
    if gpus:
        return (
            '"%s" infer/modules/train/train.py -e "%s" -sr %s -f0 %s -bs %s -g %s '
            "-te %s -se %s %s %s -l %s -c %s -sw %s -v %s"
            % (
                python_cmd,
                exp_dir1,
                sr2,
                1 if if_f0 else 0,
                batch_size,
                gpus,
                total_epoch,
                save_epoch,
                pg_flag,
                pd_flag,
                1 if if_save_latest else 0,
                1 if if_cache_gpu else 0,
                1 if if_save_every_weights else 0,
                version,
            )
        )
    return (
        '"%s" infer/modules/train/train.py -e "%s" -sr %s -f0 %s -bs %s '
        "-te %s -se %s %s %s -l %s -c %s -sw %s -v %s"
        % (
            python_cmd,
            exp_dir1,
            sr2,
            1 if if_f0 else 0,
            batch_size,
            total_epoch,
            save_epoch,
            pg_flag,
            pd_flag,
            1 if if_save_latest else 0,
            1 if if_cache_gpu else 0,
            1 if if_save_every_weights else 0,
            version,
        )
    )


def run_train_index(
    now_dir: str,
    exp_dir1: str,
    version: str,
    outside_index_root: str,
    n_cpu: int,
) -> Generator[str, None, None]:
    """faiss インデックス作成（同一プロセス内、CPU処理）。既存 infer-web.py の
    train_index ロジックをそのまま踏襲し、進捗メッセージを逐次 yield する。
    """
    import faiss
    from sklearn.cluster import MiniBatchKMeans

    exp_dir = "%s/logs/%s" % (now_dir, exp_dir1)
    os.makedirs(exp_dir, exist_ok=True)
    feature_dir = (
        "%s/3_feature256" % exp_dir if version == "v1" else "%s/3_feature768" % exp_dir
    )
    if not os.path.exists(feature_dir):
        yield "特徴抽出を先に実行してください"
        return
    listdir_res = list(os.listdir(feature_dir))
    if len(listdir_res) == 0:
        yield "特徴抽出を先に実行してください"
        return

    npys = []
    for name in sorted(listdir_res):
        phone = np.load("%s/%s" % (feature_dir, name))
        npys.append(phone)
    big_npy = np.concatenate(npys, 0)
    big_npy_idx = np.arange(big_npy.shape[0])
    np.random.shuffle(big_npy_idx)
    big_npy = big_npy[big_npy_idx]

    if big_npy.shape[0] > 2e5:
        yield "Trying doing kmeans %s shape to 10k centers." % (big_npy.shape[0],)
        try:
            big_npy = (
                MiniBatchKMeans(
                    n_clusters=10000,
                    verbose=True,
                    batch_size=256 * n_cpu,
                    compute_labels=False,
                    init="random",
                )
                .fit(big_npy)
                .cluster_centers_
            )
        except Exception:
            yield traceback.format_exc()

    np.save("%s/total_fea.npy" % exp_dir, big_npy)
    n_ivf = min(int(16 * np.sqrt(big_npy.shape[0])), big_npy.shape[0] // 39)
    yield "%s,%s" % (big_npy.shape, n_ivf)

    index = faiss.index_factory(256 if version == "v1" else 768, "IVF%s,Flat" % n_ivf)
    yield "training"
    index_ivf = faiss.extract_index_ivf(index)
    index_ivf.nprobe = 1
    index.train(big_npy)
    faiss.write_index(
        index,
        "%s/trained_IVF%s_Flat_nprobe_%s_%s_%s.index"
        % (exp_dir, n_ivf, index_ivf.nprobe, exp_dir1, version),
    )

    yield "adding"
    batch_size_add = 8192
    for i in range(0, big_npy.shape[0], batch_size_add):
        index.add(big_npy[i : i + batch_size_add])
    added_index_path = "%s/added_IVF%s_Flat_nprobe_%s_%s_%s.index" % (
        exp_dir,
        n_ivf,
        index_ivf.nprobe,
        exp_dir1,
        version,
    )
    faiss.write_index(index, added_index_path)
    yield "インデックス作成完了: added_IVF%s_Flat_nprobe_%s_%s_%s.index" % (
        n_ivf,
        index_ivf.nprobe,
        exp_dir1,
        version,
    )

    try:
        link = os.link if platform.system() == "Windows" else os.symlink
        link(
            added_index_path,
            "%s/%s_IVF%s_Flat_nprobe_%s_%s_%s.index"
            % (outside_index_root, exp_dir1, n_ivf, index_ivf.nprobe, exp_dir1, version),
        )
        yield "外部インデックスにリンクしました: %s" % outside_index_root
    except Exception:
        yield "外部インデックスへのリンクに失敗しました: %s" % outside_index_root
