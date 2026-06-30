using System.Text.Json.Serialization;

namespace RvcRealtimeGui.Models;

public class RvcConfig
{
    [JsonPropertyName("pth_path")]            public string PthPath { get; set; } = "";
    [JsonPropertyName("index_path")]          public string IndexPath { get; set; } = "";
    [JsonPropertyName("sg_hostapi")]          public string HostApi { get; set; } = "";
    [JsonPropertyName("sg_wasapi_exclusive")] public bool WasapiExclusive { get; set; }
    [JsonPropertyName("sg_input_device")]     public string InputDevice { get; set; } = "";
    [JsonPropertyName("sg_output_device")]    public string OutputDevice { get; set; } = "";
    [JsonPropertyName("sr_type")]             public string SrType { get; set; } = "sr_model";
    [JsonPropertyName("threhold")]            public int Threshold { get; set; } = -60;
    [JsonPropertyName("pitch")]               public int Pitch { get; set; }
    [JsonPropertyName("formant")]             public double Formant { get; set; }
    [JsonPropertyName("index_rate")]          public double IndexRate { get; set; }
    [JsonPropertyName("rms_mix_rate")]        public double RmsMixRate { get; set; }
    [JsonPropertyName("block_time")]          public double BlockTime { get; set; } = 0.25;
    [JsonPropertyName("crossfade_length")]    public double CrossfadeLength { get; set; } = 0.05;
    [JsonPropertyName("extra_time")]          public double ExtraTime { get; set; } = 2.5;
    [JsonPropertyName("n_cpu")]               public int NCpu { get; set; } = 4;
    [JsonPropertyName("f0method")]            public string F0Method { get; set; } = "fcpe";
    [JsonPropertyName("use_pv")]              public bool UsePv { get; set; }
    [JsonPropertyName("I_noise_reduce")]      public bool InputNoiseReduce { get; set; }
    [JsonPropertyName("O_noise_reduce")]      public bool OutputNoiseReduce { get; set; }
}

public class DevicesResponse
{
    [JsonPropertyName("hostapi")] public string HostApi { get; set; } = "";
    [JsonPropertyName("inputs")]  public List<string> Inputs { get; set; } = [];
    [JsonPropertyName("outputs")] public List<string> Outputs { get; set; } = [];
}

public class StartResponse
{
    [JsonPropertyName("message")]    public string Message { get; set; } = "";
    [JsonPropertyName("samplerate")] public int Samplerate { get; set; }
    [JsonPropertyName("delay_ms")]   public int DelayMs { get; set; }
}

public class MetricsPayload
{
    [JsonPropertyName("infer_ms")] public int InferMs { get; set; }
    [JsonPropertyName("ts")]       public double Ts { get; set; }
}
