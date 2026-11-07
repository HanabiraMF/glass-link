using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GlassLink.Protocol;

namespace GlassLink.App;

public sealed class AppOptions
{
    public int HttpPort { get; init; } = 5180;
    public int HttpsPort { get; init; } = 5443;
    public string BindAddress { get; init; } = "0.0.0.0";
    public string Controller { get; init; } = ControllerLayerIds.Auto;
    public int Fps { get; init; } = 60;
    public int Bitrate { get; init; } = 16_000_000;
    public int Gop { get; init; } = 120;
    public int OutputIndex { get; init; }
    public bool EnableHttps { get; init; } = true;
    public bool OpenBrowser { get; init; }
    public bool AllowSoftwareEncoder { get; init; }
    public bool ShowHelp { get; init; }
    public string? Token { get; init; }
    public string? HttpsThumbprint { get; init; }

    public string AutoOpenUrl => $"http://localhost:{HttpPort}/";

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            var equals = raw.IndexOf('=');
            var arg = equals > 0 ? raw[..equals] : raw;
            var inlineValue = equals > 0 ? raw[(equals + 1)..] : null;

            string Next()
            {
                if (inlineValue is not null) return inlineValue;
                if (i + 1 >= args.Length) throw new ArgumentException($"参数 {arg} 缺少值。");
                return args[++i];
            }

            switch (arg.ToLowerInvariant())
            {
                case "--port":
                    options = options.WithHttpPort(int.Parse(Next()));
                    break;
                case "--https-port":
                    options = options.WithHttpsPort(int.Parse(Next()));
                    break;
                case "--bind":
                    options = options.WithBindAddress(Next());
                    break;
                case "--controller":
                    var layer = Next().ToLowerInvariant();
                    if (!ControllerLayerIds.IsValid(layer))
                        throw new ArgumentException("--controller 必须是 auto、software 或 hardware。");
                    options = options.WithController(layer);
                    break;
                case "--fps":
                    options = options.WithFps(int.Parse(Next()));
                    break;
                case "--bitrate":
                    options = options.WithBitrate(int.Parse(Next()));
                    break;
                case "--gop":
                    options = options.WithGop(int.Parse(Next()));
                    break;
                case "--output":
                    options = options.WithOutputIndex(int.Parse(Next()));
                    break;
                case "--no-https":
                    options = options.WithHttps(false);
                    break;
                case "--open-browser":
                    options = options.WithOpen(true);
                    break;
                case "--no-open":
                    options = options.WithOpen(false);
                    break;
                case "--require-hardware-encoder":
                    options = options.WithSoftwareEncoder(false);
                    break;
                case "--allow-software-encoder":
                    options = options.WithSoftwareEncoder(true);
                    break;
                case "--token":
                    options = options.WithToken(Next());
                    break;
                case "--https-thumbprint":
                    options = options.WithHttpsThumbprint(Next());
                    break;
                case "--help":
                case "-h":
                    return options.WithShowHelp();
                default:
                    throw new ArgumentException($"未知参数：{arg}。使用 --help 查看帮助。");
            }
        }

        if (options.HttpPort is < 1 or > 65535) throw new ArgumentException("--port 超出范围。");
        if (options.HttpsPort is < 1 or > 65535) throw new ArgumentException("--https-port 超出范围。");
        if (options.Fps is < 15 or > 240) throw new ArgumentException("--fps 必须在 15..240。");
        if (options.Bitrate < 1_000_000) throw new ArgumentException("--bitrate 至少为 1000000。");
        if (options.Gop < 1) throw new ArgumentException("--gop 必须大于 0。");
        return options;
    }

    public const string HelpText = """
GlassLink — 低延迟桌面串流 / 手机反向控制
用法：
  GlassLink [选项]

选项：
  --port <5180>                   HTTP 端口（localhost 网页、证书下载）
  --https-port <5443>             HTTPS 端口（手机 WebCodecs 需要安全上下文）
  --bind <0.0.0.0>                监听地址
  --controller <auto|software|hardware>
                                  输入控制层，默认 auto：优先虚拟硬件层，失败回退软件层
  --fps <60>                      捕获/编码目标帧率（15-240）
  --bitrate <16000000>            H.264 平均码率
  --gop <120>                     关键帧间隔
  --output <0>                    DXGI 输出序号
  --no-https                      仅 HTTP（手机浏览器通常无法使用 WebCodecs）
  --open-browser                  启动后自动打开电脑端网页
  --no-open                       不自动打开电脑端网页（默认）
  --require-hardware-encoder      没有 GPU 硬件编码器时直接失败（默认行为）
  --allow-software-encoder        允许回退 Media Foundation 软件编码（仅开发/兼容）
  --token <token>                 可选访问令牌（URL ?token=...）
  --https-thumbprint <thumbprint> 从 CurrentUser\My 加载已有 HTTPS 证书（可选）
  --help                          显示帮助
""";

    public static void PrintHelp() => Console.WriteLine(HelpText);

    public static string GetPrimaryLanAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address)
                .ToList();
            return candidates.FirstOrDefault()?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private AppOptions WithHttpPort(int value) => Copy(httpPort: value);
    private AppOptions WithHttpsPort(int value) => Copy(httpsPort: value);
    private AppOptions WithBindAddress(string value) => Copy(bindAddress: value);
    private AppOptions WithController(string value) => Copy(controller: value);
    private AppOptions WithFps(int value) => Copy(fps: value);
    private AppOptions WithBitrate(int value) => Copy(bitrate: value);
    private AppOptions WithGop(int value) => Copy(gop: value);
    private AppOptions WithOutputIndex(int value) => Copy(outputIndex: value);
    private AppOptions WithHttps(bool value) => Copy(enableHttps: value);
    private AppOptions WithOpen(bool value) => Copy(openBrowser: value);
    private AppOptions WithSoftwareEncoder(bool value) => Copy(allowSoftwareEncoder: value);
    private AppOptions WithToken(string value) => Copy(token: value);
    private AppOptions WithHttpsThumbprint(string value) => Copy(httpsThumbprint: value);
    private AppOptions WithShowHelp() => Copy(showHelp: true);

    private AppOptions Copy(
        int? httpPort = null, int? httpsPort = null, string? bindAddress = null, string? controller = null,
        int? fps = null, int? bitrate = null, int? gop = null, int? outputIndex = null,
        bool? enableHttps = null, bool? openBrowser = null, bool? allowSoftwareEncoder = null, string? token = null,
        string? httpsThumbprint = null, bool? showHelp = null) =>
        new()
        {
            HttpPort = httpPort ?? HttpPort,
            HttpsPort = httpsPort ?? HttpsPort,
            BindAddress = bindAddress ?? BindAddress,
            Controller = controller ?? Controller,
            Fps = fps ?? Fps,
            Bitrate = bitrate ?? Bitrate,
            Gop = gop ?? Gop,
            OutputIndex = outputIndex ?? OutputIndex,
            EnableHttps = enableHttps ?? EnableHttps,
            OpenBrowser = openBrowser ?? OpenBrowser,
            AllowSoftwareEncoder = allowSoftwareEncoder ?? AllowSoftwareEncoder,
            Token = token ?? Token,
            HttpsThumbprint = httpsThumbprint ?? HttpsThumbprint,
            ShowHelp = showHelp ?? ShowHelp
        };
}
