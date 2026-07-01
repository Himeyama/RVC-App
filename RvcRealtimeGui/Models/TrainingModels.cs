using System.Text.Json.Serialization;

namespace RvcRealtimeGui.Models;

public class PreprocessRequest
{
    [JsonPropertyName("trainset_dir")]     public string TrainsetDir { get; set; } = "";
    [JsonPropertyName("exp_dir")]          public string ExpDir { get; set; } = "";
    [JsonPropertyName("sr")]               public string Sr { get; set; } = "40k";
    [JsonPropertyName("n_p")]              public int NP { get; set; } = 4;
    [JsonPropertyName("noparallel")]       public bool NoParallel { get; set; }
    [JsonPropertyName("preprocess_per")]   public double PreprocessPer { get; set; } = 3.0;
}

public class ExtractF0FeatureRequest
{
    [JsonPropertyName("exp_dir")]      public string ExpDir { get; set; } = "";
    [JsonPropertyName("gpus")]         public string Gpus { get; set; } = "0";
    [JsonPropertyName("n_p")]          public int NP { get; set; } = 4;
    [JsonPropertyName("f0method")]     public string F0Method { get; set; } = "rmvpe_gpu";
    [JsonPropertyName("if_f0")]        public bool IfF0 { get; set; } = true;
    [JsonPropertyName("version")]      public string Version { get; set; } = "v2";
    [JsonPropertyName("gpus_rmvpe")]   public string GpusRmvpe { get; set; } = "0-0";
}

public class TrainRequest
{
    [JsonPropertyName("exp_dir")]                  public string ExpDir { get; set; } = "";
    [JsonPropertyName("sr")]                        public string Sr { get; set; } = "40k";
    [JsonPropertyName("if_f0")]                     public bool IfF0 { get; set; } = true;
    [JsonPropertyName("spk_id")]                    public int SpkId { get; set; }
    [JsonPropertyName("save_epoch")]                public int SaveEpoch { get; set; } = 5;
    [JsonPropertyName("total_epoch")]               public int TotalEpoch { get; set; } = 20;
    [JsonPropertyName("batch_size")]                public int BatchSize { get; set; } = 4;
    [JsonPropertyName("if_save_latest")]            public bool IfSaveLatest { get; set; }
    [JsonPropertyName("pretrained_g")]              public string PretrainedG { get; set; } = "";
    [JsonPropertyName("pretrained_d")]              public string PretrainedD { get; set; } = "";
    [JsonPropertyName("gpus")]                       public string Gpus { get; set; } = "0";
    [JsonPropertyName("if_cache_gpu")]              public bool IfCacheGpu { get; set; }
    [JsonPropertyName("if_save_every_weights")]     public bool IfSaveEveryWeights { get; set; }
    [JsonPropertyName("version")]                    public string Version { get; set; } = "v2";
}

public class TrainIndexRequest
{
    [JsonPropertyName("exp_dir")]  public string ExpDir { get; set; } = "";
    [JsonPropertyName("version")]  public string Version { get; set; } = "v2";
}

public class Train1KeyRequest
{
    [JsonPropertyName("exp_dir")]                  public string ExpDir { get; set; } = "";
    [JsonPropertyName("trainset_dir")]             public string TrainsetDir { get; set; } = "";
    [JsonPropertyName("sr")]                        public string Sr { get; set; } = "40k";
    [JsonPropertyName("if_f0")]                     public bool IfF0 { get; set; } = true;
    [JsonPropertyName("spk_id")]                    public int SpkId { get; set; }
    [JsonPropertyName("n_p")]                       public int NP { get; set; } = 4;
    [JsonPropertyName("f0method")]                  public string F0Method { get; set; } = "rmvpe_gpu";
    [JsonPropertyName("save_epoch")]                public int SaveEpoch { get; set; } = 5;
    [JsonPropertyName("total_epoch")]               public int TotalEpoch { get; set; } = 20;
    [JsonPropertyName("batch_size")]                public int BatchSize { get; set; } = 4;
    [JsonPropertyName("if_save_latest")]            public bool IfSaveLatest { get; set; }
    [JsonPropertyName("pretrained_g")]              public string PretrainedG { get; set; } = "";
    [JsonPropertyName("pretrained_d")]              public string PretrainedD { get; set; } = "";
    [JsonPropertyName("gpus")]                       public string Gpus { get; set; } = "0";
    [JsonPropertyName("if_cache_gpu")]              public bool IfCacheGpu { get; set; }
    [JsonPropertyName("if_save_every_weights")]     public bool IfSaveEveryWeights { get; set; }
    [JsonPropertyName("version")]                    public string Version { get; set; } = "v2";
    [JsonPropertyName("gpus_rmvpe")]                public string GpusRmvpe { get; set; } = "0-0";
}

public class JobStartResponse
{
    [JsonPropertyName("job_id")] public string JobId { get; set; } = "";
}

public class JobStatusResponse
{
    [JsonPropertyName("job_id")]    public string JobId { get; set; } = "";
    [JsonPropertyName("kind")]      public string Kind { get; set; } = "";
    [JsonPropertyName("status")]    public string Status { get; set; } = "";
    [JsonPropertyName("log_delta")] public string LogDelta { get; set; } = "";
    [JsonPropertyName("error")]     public string? Error { get; set; }
}

// ── UVR5 ボーカル分離 ────────────────────────────────────────

public class UvrSeparateRequest
{
    [JsonPropertyName("model_name")]       public string ModelName { get; set; } = "";
    [JsonPropertyName("inp_root")]         public string InpRoot { get; set; } = "";
    [JsonPropertyName("save_root_vocal")]  public string SaveRootVocal { get; set; } = "opt";
    [JsonPropertyName("save_root_ins")]    public string SaveRootIns { get; set; } = "opt";
    [JsonPropertyName("agg")]              public int Agg { get; set; } = 10;
    [JsonPropertyName("format0")]          public string Format0 { get; set; } = "flac";
}

public class Uvr5ModelsResponse
{
    [JsonPropertyName("models")] public List<string> Models { get; set; } = [];
}

// ── モデル管理（マージ・情報表示・変更・抽出） ────────────────

public class ModelMergeRequest
{
    [JsonPropertyName("path1")]   public string Path1 { get; set; } = "";
    [JsonPropertyName("path2")]   public string Path2 { get; set; } = "";
    [JsonPropertyName("alpha1")]  public double Alpha1 { get; set; } = 0.5;
    [JsonPropertyName("sr")]      public string Sr { get; set; } = "40k";
    [JsonPropertyName("f0")]      public bool F0 { get; set; } = true;
    [JsonPropertyName("info")]    public string Info { get; set; } = "";
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "v2";
}

public class ModelInfoRequest
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

public class ModelInfoResponse
{
    [JsonPropertyName("info")] public string Info { get; set; } = "";
}

public class ModelChangeInfoRequest
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("info")] public string Info { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class ModelExtractRequest
{
    [JsonPropertyName("path")]    public string Path { get; set; } = "";
    [JsonPropertyName("name")]    public string Name { get; set; } = "";
    [JsonPropertyName("sr")]      public string Sr { get; set; } = "40k";
    [JsonPropertyName("if_f0")]   public bool IfF0 { get; set; } = true;
    [JsonPropertyName("info")]    public string Info { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "v2";
}

public class MessageResponse
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}
