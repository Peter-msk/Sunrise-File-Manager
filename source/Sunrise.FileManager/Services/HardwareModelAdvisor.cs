namespace Sunrise.FileManager.Services;

public sealed record GpuHardware(string Name, double DedicatedGb);
public sealed record StorageHardware(string Name, string Medium, double FreeGb, bool IsModelDrive);
public sealed record PcHardware(string Cpu, int LogicalCores, double RamGb, double AvailableRamGb,
    IReadOnlyList<GpuHardware> Gpus, IReadOnlyList<StorageHardware> Drives, string ModelPath, string? Warning = null);
public sealed record LocalModel(string Tag, string Label, double DownloadGb, double MinRamGb, double MinVramGb);
public sealed record ModelRecommendation(LocalModel Model, bool Fits, int Priority, string Reason);
public static class HardwareModelAdvisor
{
    // Download sizes are approximate; RAM thresholds are conservative planning estimates, not benchmarks.
    public static readonly LocalModel[] Catalog =
    [
        new("qwen3:0.6b", "초경량 · 응답 속도 우선", 0.523, 4, 2),
        new("qwen3:1.7b", "경량 · 일반 파일 분류", 1.4, 6, 3),
        new("qwen3:4b-instruct", "균형 · 한국어 요약 및 분류", 2.5, 12, 5),
        new("qwen3:8b", "정확도 우선 · 넉넉한 메모리 필요", 5.2, 16, 8),
        new("qwen3:14b", "대형 · 고성능 그래픽카드 권장", 9.3, 32, 16)
    ];
    public static IReadOnlyList<ModelRecommendation> Rank(PcHardware pc)
    {
        double vram = pc.Gpus.Select(x => x.DedicatedGb).DefaultIfEmpty(0).Max();
        var drive = pc.Drives.FirstOrDefault(x => x.IsModelDrive);
        int preferred = pc.RamGb >= 32 && vram >= 16 ? 4 : pc.RamGb >= 16 && vram >= 8 ? 3 :
            pc.RamGb >= 16 && (vram >= 5 || (pc.LogicalCores >= 4 && drive?.Medium == "SSD")) ? 2 : pc.RamGb >= 8 ? 1 : 0;
        return Catalog.Select((m, index) =>
        {
            double space = m.DownloadGb * 1.5 + 2;
            bool ram = pc.RamGb >= m.MinRamGb;
            bool disk = drive is null || drive.FreeGb >= space;
            bool fits = ram && disk;
            string reason = !ram ? $"RAM {m.MinRamGb:0}GB 이상 권장 · 현재 메모리 부족" :
                !disk ? $"모델 저장 드라이브에 약 {space:0.0}GB 여유 필요" :
                vram >= m.MinVramGb ? "전용 그래픽 메모리 여유 · Ollama 드라이버 지원 확인 필요" :
                "CPU 실행 기준 · 작은 모델일수록 응답이 빠를 가능성이 높습니다";
            if (drive is null) reason += " · 저장 공간 확인 불가";
            if (drive?.Medium == "HDD") reason += " · HDD는 첫 모델 로딩이 느릴 수 있습니다";
            if (pc.AvailableRamGb > 0 && pc.AvailableRamGb < m.MinRamGb * 0.65) reason += " · 다른 프로그램을 닫아 메모리를 확보하세요";
            if (index >= 3 && vram < m.MinVramGb) reason += " · CPU에서는 처리 시간이 길어질 수 있습니다";
            int score = (fits ? 1000 : 0) - Math.Abs(index - preferred) * 100 - (index > preferred ? 30 : 0);
            return new ModelRecommendation(m, fits, score, reason);
        }).OrderByDescending(x => x.Priority).ToArray();
    }
    public static string Describe(PcHardware pc) =>
        $"CPU: {pc.Cpu} ({pc.LogicalCores}개 논리 코어)\nRAM: {pc.RamGb:0.0}GB · 사용 가능 {pc.AvailableRamGb:0.0}GB\n" +
        "GPU: " + (pc.Gpus.Count == 0 ? "조회 불가 또는 전용 GPU 없음" : string.Join(" / ", pc.Gpus.Select(x => $"{x.Name} · 전용 {x.DedicatedGb:0.0}GB"))) + "\n" +
        "저장장치: " + (pc.Drives.Count == 0 ? "조회 불가" : string.Join(" / ", pc.Drives.Select(x => $"{x.Name} {x.Medium} · 여유 {x.FreeGb:0.0}GB"))) + "\n모델 저장: " + pc.ModelPath +
        "\n추천은 사양에 따른 추정입니다. GPU 가속은 Ollama와 드라이버의 실제 지원 여부에 따라 달라집니다." +
        (pc.Warning is null ? "" : "\n" + pc.Warning);
}
