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
