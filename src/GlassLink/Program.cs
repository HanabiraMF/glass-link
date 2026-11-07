using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;
using GlassLink.App;
using GlassLink.Encoding;
using GlassLink.Input;
using GlassLink.Logging;
using GlassLink.Protocol;
using GlassLink.Security;
using GlassLink.Tray;
using Microsoft.Extensions.Logging;

namespace GlassLink;

/// <summary>
/// Silent WinExe entry point. Kestrel runs in the background; WinForms provides
/// the tray icon and message loop. No console window is ever created.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var logPath = FileLoggerProvider.Initialize();
        var fileProvider = new FileLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(fileProvider);
        });

        var bootLogger = loggerFactory.CreateLogger("GlassLink.Boot");
        bootLogger.LogInformation("============================================================");
        bootLogger.LogInformation("GlassLink WinExe starting. Log: {LogPath}", logPath);

        NativeLibraryResolver.Install();

        AppOptions options;
        try
        {
            options = AppOptions.Parse(args);
        }
        catch (Exception ex)
        {
            bootLogger.LogError(ex, "Invalid command line.");
            MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + AppOptions.HelpText,
                "GlassLink 启动参数错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            MessageBox.Show(AppOptions.HelpText, "GlassLink 帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) =>
        {
            bootLogger.LogError(e.Exception, "Unhandled UI thread exception.");
            MessageBox.Show(e.Exception.Message, "GlassLink 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            bootLogger.LogCritical(e.ExceptionObject as Exception, "Unhandled application exception.");
        };

        LocalCertificate? certificate = null;
        GlassLinkRuntime? runtime = null;
        WebApplication? app = null;
        try
        {
            MediaFoundationEncoder.MFStartup();

            if (options.EnableHttps)
                certificate = TryCreateCertificate(options, bootLogger);

            runtime = new GlassLinkRuntime(options, loggerFactory, certificate);
            try
            {
                runtime.InitializeAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                bootLogger.LogError(ex, "Input controller initialization failed; the host will continue.");
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory
            });

            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddProvider(fileProvider);

            ConfigureKestrel(builder, options, certificate);
            app = builder.Build();

            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15)
            });

            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                await next();
            });

            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                        context.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                        context.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Context.Response.Headers.CacheControl = "no-cache";
                    }
                }
            });

            MapEndpoints(app, runtime, options, certificate, bootLogger);

            try
            {
                app.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                bootLogger.LogError(ex, "HTTP server failed to start.");
                MessageBox.Show("GlassLink 服务启动失败：" + Environment.NewLine + ex.Message +
                    Environment.NewLine + Environment.NewLine + "请检查端口是否被占用，或修改 --port / --https-port。",
                    "GlassLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            var lan = AppOptions.GetPrimaryLanAddress();
            var tokenSuffix = string.IsNullOrEmpty(options.Token)
                ? ""
                : "?token=" + Uri.EscapeDataString(options.Token);
            var desktopUrl = $"http://localhost:{options.HttpPort}/{tokenSuffix}";
            var mobileUrl = certificate is not null
                ? $"https://{lan}:{options.HttpsPort}/mobile.html{tokenSuffix}"
                : $"http://{lan}:{options.HttpPort}/mobile.html{tokenSuffix}";

            bootLogger.LogInformation("Web server started. Desktop: {DesktopUrl}", desktopUrl);
            bootLogger.LogInformation("Mobile: {MobileUrl}", mobileUrl);
            if (certificate is not null)
                bootLogger.LogInformation("Root CA: {RootCa}", certificate.RootCertificatePath);

            if (options.OpenBrowser)
            {
                try { Process.Start(new ProcessStartInfo(desktopUrl) { UseShellExecute = true }); }
                catch (Exception ex) { bootLogger.LogWarning(ex, "Could not open the browser."); }
            }

            var tray = new TrayApplicationContext(runtime, desktopUrl, mobileUrl,
                loggerFactory.CreateLogger<TrayApplicationContext>());
            Application.Run(tray);
            return 0;
        }
        catch (Exception ex)
        {
            bootLogger.LogCritical(ex, "Fatal startup error.");
            MessageBox.Show("GlassLink 启动失败：" + Environment.NewLine + ex.Message +
                Environment.NewLine + Environment.NewLine + "日志：" + logPath,
                "GlassLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            bootLogger.LogInformation("GlassLink shutting down.");
            try
            {
                if (app is not null)
                {
                    app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { bootLogger.LogWarning(ex, "Error stopping web host."); }

            try { runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { bootLogger.LogWarning(ex, "Error disposing runtime."); }

            certificate?.ServerCertificate.Dispose();
            certificate?.RootCertificate.Dispose();
            MediaFoundationEncoder.MFShutdown();
        }
    }

    private static LocalCertificate? TryCreateCertificate(AppOptions options, ILogger bootLogger)
    {
        if (!string.IsNullOrWhiteSpace(options.HttpsThumbprint))
        {
            try
            {
                var thumbprint = options.HttpsThumbprint.Replace(" ", "", StringComparison.Ordinal).Trim();
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                    .OfType<X509Certificate2>()
                    .FirstOrDefault(c => c.HasPrivateKey);
                if (found is not null)
                {
                    bootLogger.LogInformation("Using HTTPS certificate from CurrentUser\\My: {Subject} [{Thumbprint}].",
                        found.Subject, found.Thumbprint);
                    return new LocalCertificate
                    {
                        ServerCertificate = found,
                        RootCertificate = X509CertificateLoader.LoadCertificate(found.Export(X509ContentType.Cert)),
                        RootCertificatePath = "",
                        DnsNames = Array.Empty<string>(),
                        IpAddresses = Array.Empty<string>()
                    };
                }

                bootLogger.LogError("No certificate with private key found for thumbprint {Thumbprint}.", thumbprint);
            }
            catch (Exception ex)
            {
                bootLogger.LogError(ex, "Failed to load HTTPS certificate by thumbprint.");
            }
        }

        try
        {
            var cert = LocalCertificate.EnsureCreated(bootLogger);
            bootLogger.LogInformation("HTTPS enabled. Root CA: {Path}", cert.RootCertificatePath);
            return cert;
        }
        catch (Exception ex)
        {
            bootLogger.LogError(ex, "HTTPS certificate setup failed. Continuing with HTTP only.");
            return null;
        }
    }

    private static void MapEndpoints(WebApplication app, GlassLinkRuntime runtime, AppOptions options,
        LocalCertificate? certificate, ILogger logger)
    {
        app.MapGet("/mobile", () => Results.Redirect("/mobile.html"));

        app.MapGet("/api/status", (HttpContext context) =>
        {
            if (!runtime.IsAuthorized(context)) return Results.Unauthorized();
            return Results.Json(runtime.GetStatus(), ProtocolJson.Options);
        });

        app.MapPost("/api/stream/start", async (HttpContext context) =>
        {
            if (!runtime.IsAuthorized(context)) return Results.Unauthorized();
            await runtime.StartStreamAsync();
            return Results.Json(new { ok = true, status = runtime.GetStatus() }, ProtocolJson.Options);
        });

        app.MapPost("/api/stream/stop", async (HttpContext context) =>
        {
            if (!runtime.IsAuthorized(context)) return Results.Unauthorized();
            await runtime.StopStreamAsync();
            return Results.Json(new { ok = true, status = runtime.GetStatus() }, ProtocolJson.Options);
        });

        app.MapPost("/api/video/settings", async (HttpContext context) =>
        {
            if (!runtime.IsAuthorized(context)) return Results.Unauthorized();
            var request = await context.Request.ReadFromJsonAsync<VideoSettingsRequest>(ProtocolJson.Options, context.RequestAborted);
            if (request?.Fps is null && request?.Bitrate is null)
                return Results.BadRequest(new { error = "fps 或 bitrate 至少提供一个。" });
            var current = runtime.Stream.VideoConfig;
            runtime.Stream.ApplyVideoSettings(request!.Fps ?? current?.Fps ?? options.Fps,
                request.Bitrate ?? current?.Bitrate ?? options.Bitrate);
            return Results.Json(new { ok = true, video = runtime.Stream.VideoConfig }, ProtocolJson.Options);
        });

        app.MapPost("/api/controller", async (HttpContext context) =>
        {
            if (!runtime.IsAuthorized(context)) return Results.Unauthorized();
            var request = await context.Request.ReadFromJsonAsync<ControllerSetRequest>(ProtocolJson.Options, context.RequestAborted);
            if (request?.Layer is null || !ControllerLayerIds.IsValid(request.Layer))
                return Results.BadRequest(new { error = "layer 必须是 auto、software 或 hardware。" });
            var status = await runtime.SetControllerAsync(request.Layer, context.RequestAborted);
            return Results.Json(new { ok = true, controller = status }, ProtocolJson.Options);
        });

        var receivedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassLink", "Received");
        Directory.CreateDirectory(receivedDir);
        var received = new System.Collections.Concurrent.ConcurrentDictionary<string, (string Path, string Name)>();

        app.MapPost("/api/files", async (HttpContext context) =>
        {
            if (!runtime.IsAuthorized(context)) return Results.Unauthorized();
            var rawName = context.Request.Headers["X-File-Name"].ToString();
            if (string.IsNullOrWhiteSpace(rawName)) rawName = "file.bin";
            var name = Path.GetFileName(Uri.UnescapeDataString(rawName));
            if (string.IsNullOrWhiteSpace(name)) name = "file.bin";
            var id = Guid.NewGuid().ToString("N")[..12];
            var path = Path.Combine(receivedDir, id + Path.GetExtension(name));
            await using (var target = File.Create(path))
                await context.Request.Body.CopyToAsync(target, context.RequestAborted);
            received[id] = (path, name);
            var url = $"/api/files/{id}";
            runtime.Hub.BroadcastText(ProtocolJson.Serialize(new { type = "file", name, url }));
            return Results.Json(new { ok = true, name, url }, ProtocolJson.Options);
        });

        app.MapGet("/api/files/{id}", (string id) =>
        {
            if (!received.TryGetValue(id, out var entry) || !File.Exists(entry.Path))
                return Results.NotFound();
            return Results.File(entry.Path, "application/octet-stream", entry.Name);
        });

        app.MapGet("/cert/root-ca.cer", () =>
        {
            if (certificate is null || !File.Exists(certificate.RootCertificatePath))
                return Results.NotFound(new { error = "未启用 HTTPS 或根证书尚未生成。" });
            return Results.File(certificate.RootCertificatePath, "application/x-x509-ca-cert", "GlassLink-Local-CA.cer");
        });

        // Installing kernel drivers requires UAC and a reboot; never do it silently
        // from the web process. This endpoint opens the installer bundle directory.
        app.MapPost("/api/drivers/open-installer", (HttpContext context) =>
        {
            if (!runtime.IsAuthorized(context)) return Results.Unauthorized();
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "drivers"),
                Path.Combine(AppContext.BaseDirectory, "scripts")
            };
            var scriptDir = candidates.FirstOrDefault(Directory.Exists);
            if (scriptDir is null)
                return Results.NotFound(new { error = "未找到驱动脚本目录。" });

            try
            {
                var command = Path.Combine(scriptDir, "install-input-drivers.cmd");
                var target = File.Exists(command) ? command : scriptDir;
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                return Results.Json(new { ok = true, path = scriptDir }, ProtocolJson.Options);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not open driver installer folder.");
                return Results.Problem(ex.Message);
            }
        });

        app.Map("/ws", async context =>
        {
            if (!runtime.IsAuthorized(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or missing token.");
                return;
            }
            await runtime.Hub.HandleAsync(context);
        });
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, AppOptions options, LocalCertificate? certificate)
    {
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 512L * 1024 * 1024;

            var bindAll = options.BindAddress is "0.0.0.0" or "*" or "+";
            if (bindAll)
            {
                server.ListenAnyIP(options.HttpPort);
                if (certificate is not null)
                    server.ListenAnyIP(options.HttpsPort, listen => listen.UseHttps(certificate.ServerCertificate));
            }
            else
            {
                var address = IPAddress.Parse(options.BindAddress);
                server.Listen(address, options.HttpPort);
                if (certificate is not null)
                    server.Listen(address, options.HttpsPort, listen => listen.UseHttps(certificate.ServerCertificate));
            }
        });
    }

    private sealed record ControllerSetRequest
    {
        public string? Layer { get; init; }
    }

    private sealed record VideoSettingsRequest
    {
        public int? Fps { get; init; }
        public int? Bitrate { get; init; }
    }
}
