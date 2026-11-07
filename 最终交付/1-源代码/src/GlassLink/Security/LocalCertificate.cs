using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GlassLink.Security;

/// <summary>
/// Creates a persistent local root CA and a server certificate trusted by it.
/// WebCodecs requires a secure context on a phone, so plain HTTP to a LAN IP is
/// not sufficient. Install the exported CA certificate once on the phone.
/// This is development/LAN infrastructure, not a publicly-trusted certificate.
/// </summary>
public sealed class LocalCertificate
{
    private const string PfxPassword = "GlassLink-Local-Dev";
    private const X509KeyStorageFlags CertificateKeyFlags =
        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet;

    public required X509Certificate2 ServerCertificate { get; init; }
    public required X509Certificate2 RootCertificate { get; init; }
    public required string RootCertificatePath { get; init; }
    public required IReadOnlyList<string> DnsNames { get; init; }
    public required IReadOnlyList<string> IpAddresses { get; init; }

    public static LocalCertificate EnsureCreated(ILogger logger)
    {
        var preferredDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlassLink",
            "certs");
        var directory = preferredDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (UnauthorizedAccessException)
        {
            // Published apps and restricted sandboxes may not be allowed to write
            // under LocalAppData. Fall back to the application directory.
            directory = Path.Combine(AppContext.BaseDirectory, "certs");
            Directory.CreateDirectory(directory);
            logger.LogWarning("Could not write to {Preferred}; using {Fallback} for local HTTPS certificates.",
                preferredDirectory, directory);
        }

        var rootPfxPath = Path.Combine(directory, "glasslink-root-ca.pfx");
        var rootCerPath = Path.Combine(directory, "GlassLink-Local-CA.cer");
        var serverPfxPath = Path.Combine(directory, "glasslink-server.pfx");

        var root = LoadOrCreateRoot(rootPfxPath, logger);
        var names = BuildDnsNames();
        var addresses = BuildIpAddresses();
        var server = LoadOrCreateServer(serverPfxPath, root, names, addresses, logger);

        try
        {
            File.WriteAllBytes(rootCerPath, root.Export(X509ContentType.Cert));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not export root CA certificate to {Path}.", rootCerPath);
        }

        return new LocalCertificate
        {
            ServerCertificate = server,
            RootCertificate = root,
            RootCertificatePath = rootCerPath,
            DnsNames = names,
            IpAddresses = addresses
        };
    }

    private static X509Certificate2 LoadOrCreateRoot(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, PfxPassword, CertificateKeyFlags);
                if (existing.NotAfter > DateTimeOffset.UtcNow.AddDays(30) && existing.HasPrivateKey)
                {
                    logger.LogInformation("Loaded local root CA: {Subject}, expires {Expiry:yyyy-MM-dd}.",
                        existing.Subject, existing.NotAfter);
                    return existing;
                }
                existing.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Existing root CA could not be loaded; generating a new one.");
            }
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=GlassLink Local Root CA, O=GlassLink Development",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var selfSigned = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));
        var persisted = X509CertificateLoader.LoadPkcs12(
            selfSigned.Export(X509ContentType.Pfx, PfxPassword),
            PfxPassword,
            CertificateKeyFlags);
        File.WriteAllBytes(path, persisted.Export(X509ContentType.Pfx, PfxPassword));
        logger.LogInformation("Created local root CA: {Path}", path);
        return persisted;
    }

    private static X509Certificate2 LoadOrCreateServer(
        string path,
        X509Certificate2 root,
        IReadOnlyList<string> dnsNames,
        IReadOnlyList<string> ipAddresses,
        ILogger logger)
    {
        // Always issue a fresh leaf certificate signed by the persistent root CA.
        // This guarantees the SAN always contains the current LAN IP addresses;
        // phones only need to trust the stable root CA once.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=GlassLink, O=GlassLink Development",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames) san.AddDnsName(dns);
        foreach (var ip in ipAddresses)
        {
            if (IPAddress.TryParse(ip, out var address)) san.AddIpAddress(address);
        }
        request.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        using var signed = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), serial);
        using var withKey = signed.CopyWithPrivateKey(rsa);
        var server = X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pfx, PfxPassword),
            PfxPassword,
            CertificateKeyFlags);
        File.WriteAllBytes(path, server.Export(X509ContentType.Pfx, PfxPassword));
        logger.LogInformation("Created HTTPS server certificate signed by local root CA (SAN: {Dns} / {Ips}).",
            string.Join(", ", dnsNames), string.Join(", ", ipAddresses));
        return server;
    }

    private static List<string> BuildDnsNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        try
        {
            names.Add(Dns.GetHostName());
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                    names.Add(address.ToString());
            }
        }
        catch { }
        return names.ToList();
    }

    private static List<string> BuildIpAddresses()
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal) { "127.0.0.1", "::1" };
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork ||
                        (unicast.Address.AddressFamily == AddressFamily.InterNetworkV6 && unicast.Address.ScopeId == 0))
                        addresses.Add(unicast.Address.ToString());
                }
            }
        }
        catch { }
        return addresses.ToList();
    }
}
